using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Helpers;
using livepaper.Models;
using livepaper.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace livepaper.Web;

// API: headless backend for the Electron/React UI. Loopback-only minimal API + WS /events +
// image proxy. Endpoints are thin wrappers over the existing Helpers/Services/Scrapers — no
// business logic lives here (it sits in AppOps / SteamOps / the Helpers).
public static class ServerHost
{
    private const string FirefoxUA =
        "Mozilla/5.0 (X11; Linux x86_64; rv:150.0) Gecko/20100101 Firefox/150.0";

    // Order matches the UI's source pills. WE-local (index 3) is hidden when
    // AutoImportWallpaperEngine is on (the trash button does delete-from-source instead).
    private static readonly IBgsProvider[] Providers =
    {
        new MotionBgsService(), new MoewallsService(), new DesktophutService(),
        new WallpaperEngineService(), new SteamWorkshopService(),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly FileExtensionContentTypeProvider Mime = new();

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "livepaper");

    public static void Run(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        // LP_PORT pins the port (dev: dotnet-watch restarts keep the same port → the open UI survives);
        // otherwise bind a random free port.
        var fixedPort = int.TryParse(Environment.GetEnvironmentVariable("LP_PORT"), out var lp) ? lp : 0;
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, fixedPort));
        builder.Services.AddCors(); // loopback-only; lets `vite dev` (other port) talk to us
        var app = builder.Build();

        app.UseCors(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
        app.UseWebSockets();
        EventBus.WirePlayer();

        // Serve the built React UI same-origin (so the client uses relative paths, no CORS).
        // LP_UI_DIR points at app/ui/dist (dev) or the bundled wwwroot (packaged).
        var uiDir = Environment.GetEnvironmentVariable("LP_UI_DIR");
        if (!string.IsNullOrEmpty(uiDir) && Directory.Exists(uiDir))
        {
            var fp = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(uiDir));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fp });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = fp });
        }

        MapEndpoints(app);

        app.Start();
        var port = new Uri(app.Urls.First()).Port;
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Path.Combine(ConfigDir, "serve.port"), port.ToString()); // Electron reads this
        Console.WriteLine($"livepaper --serve listening on 127.0.0.1:{port}");

        // The serve backend owns the AudioMonitor in-process while running: take over from any detached
        // `--monitor` on startup so the AutoMute toggle can Start/Stop it live (`/settings`), then hand
        // back to a detached `--monitor` on shutdown so mute survives app close.
        AudioMonitor.KillDetachedMonitor();
        // Same ownership handoff for the TIMED-PLAYLIST timer: `--serve` runs the timer IN-PROCESS, so it
        // must KILL any detached `--timer-daemon` (e.g. one spawned at boot by `--restore`) — otherwise
        // BOTH own mpvpaper and the daemon keeps cycling its own (stale) playlist over ours ("creep") —
        // and CLAIM ownership (WriteGuiTimerPid) so SpawnTimerDaemon's IsGuiTimerAlive guard skips while we
        // live. Handed back to a detached daemon on shutdown (below) so the playlist survives app close.
        PlayerHelper.KillTimerDaemon();
        PlayerHelper.WriteGuiTimerPid();
        // ...and RESUME the tick in-process so the playlist the daemon was running keeps advancing on
        // GUI-open instead of freezing (the other half of the take-over — mirrors AudioMonitor.Start
        // below). ResumeTimedTimer preserves the countdown and does NOT relaunch the on-screen wallpaper.
        if (PlayerHelper.IsTimedPlaylistActive()) PlayerHelper.ResumeTimedTimer();
        var startup = SettingsService.Load();
        if (startup.AutoMute)
            AudioMonitor.Start(startup.AutoMuteDelayMs, startup.AutoUnmuteDelayMs, startup.AutoMuteThresholdDb, startup.AutoMuteOnlyIfMprisActive);
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            var s = SettingsService.Load();
            AudioMonitor.Stop();
            if (s.AutoMute && PlayerHelper.IsPlaying) AudioMonitor.SpawnDetachedMonitor();
            // Release timer ownership, then hand the timed playlist back to a detached `--timer-daemon`
            // so it keeps cycling after the app closes (the Electron shell only kills the backend — it
            // doesn't run `--restore`). Clear-first so SpawnTimerDaemon's IsGuiTimerAlive guard lets it spawn.
            PlayerHelper.ClearGuiTimerPid();
            if (PlayerHelper.IsTimedPlaylistActive()) PlayerHelper.SpawnTimerDaemon();
        });

        // WE auto-import on startup, off-thread so the API is up immediately;
        // the UI refreshes its library on the library-synced WS event.
        _ = Task.Run(() =>
        {
            try { Program.RunHeadlessAutoSync(); EventBus.Broadcast("library-synced", new { count = 0 }); }
            catch { }
        });

        // While the app is open, measure any un-measured library audio in the background so loudness
        // normalization is ready before clips play (only when the feature is on).
        if (startup.NormalizeAudio)
            _ = Task.Run(async () => { try { await LibraryService.BackfillLoudnessAsync(); } catch { } });

        app.WaitForShutdown();
    }

    // Push live settings onto the (startup-built, reused) provider instances per request. Without this
    // the workshop/WE services keep their startup defaults (e.g. AllowScenes=false), so toggling scene
    // support has no effect and scene-only results (Puppet Warp etc.) are silently filtered to 0.
    private static void ConfigureProvider(IBgsProvider p, AppSettings s, WorkshopFilter? filter)
    {
        if (p is SteamWorkshopService ws)
        {
            ws.AllowScenes = s.AllowScenes;
            ws.WorkshopBasePath = s.WallpaperEnginePath;
            if (filter != null) ws.Filter = filter;
        }
        else if (p is WallpaperEngineService we)
        {
            we.WorkshopPath = s.WallpaperEnginePath;
            we.AllowScenes = s.AllowScenes;
        }
    }

    // Cache an animated gif, trimming a leading fade-from-black (frame 0 black → loop flashes black).
    // Detect the leading black segment with ffmpeg blackdetect, trim from its end, re-encode (palette).
    // Falls back to the raw bytes on any failure / no leading black. One-time per gif (then cached).
    private static readonly Regex _blackRe = new(@"black_start:([\d.]+)\s+black_end:([\d.]+)", RegexOptions.Compiled);
    private static async Task CacheGifTrimmed(byte[] data, string outPath)
    {
        var tmp = outPath + ".src.gif";
        await File.WriteAllBytesAsync(tmp, data);
        try
        {
            double ss = 0;
            // blackdetect → stderr lines: "black_start:0 black_end:0.4 ..."
            var det = RunFfmpeg(["-hide_banner", "-i", tmp, "-vf", "blackdetect=d=0.03:pix_th=0.10", "-an", "-f", "null", "-"]);
            var m = _blackRe.Match(det);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var bs)
                && bs < 0.15 && double.TryParse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture, out var be))
                ss = be;
            if (ss > 0.02)
            {
                RunFfmpeg(["-y", "-ss", ss.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", tmp,
                    "-vf", "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse", "-loop", "0", outPath]);
                if (new FileInfo(outPath).Length > 0) { File.Delete(tmp); return; }
            }
        }
        catch { }
        try { File.Move(tmp, outPath, true); } catch { try { await File.WriteAllBytesAsync(outPath, data); File.Delete(tmp); } catch { } }
    }

    // A non-black/white STILL for the card thumbnail (so AutoPlayGifs-off doesn't show the gif's
    // black frame-0). First frame with YAVG 30–225, else frame 1, else frame 0.
    private static void ExtractStill(string gifPath, string outJpg, int width = 0)
    {
        string tmp = outJpg + ".tmp.jpg";
        bool Ok() => File.Exists(tmp) && new FileInfo(tmp).Length > 0;
        void Del() { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
        // PERF: decode-to-size — downscale the still to the card's pixel width (never upscale: min(w,iw)).
        // Card thumbs are ~160–360px but previews are ~1024px → a big GPU texture per card = memory + GC.
        // The enlarged modal asks for the native still (width=0) so it stays crisp → no quality loss.
        // lanczos = sharp downscale; -q:v 2 = near-lossless JPEG (the default mjpeg qscale was ugly).
        string scale = width > 0 ? $",scale='min({width},iw)':-2:flags=lanczos" : "";
        try { RunFfmpeg(["-i", gifPath, "-vf", "signalstats,metadata=select:key=lavfi.signalstats.YAVG:value=30:function=greater,metadata=select:key=lavfi.signalstats.YAVG:value=225:function=less" + scale, "-frames:v", "1", "-q:v", "2", tmp, "-y"]); if (Ok()) { File.Move(tmp, outJpg, true); return; } } catch { }
        Del();
        try { RunFfmpeg(["-i", gifPath, "-vf", @"select=eq(n\,1)" + scale, "-frames:v", "1", "-q:v", "2", tmp, "-y"]); if (Ok()) { File.Move(tmp, outJpg, true); return; } } catch { }
        Del();
        try { RunFfmpeg(["-i", gifPath, "-vf", "select=eq(n\\,0)" + scale, "-frames:v", "1", "-q:v", "2", tmp, "-y"]); if (Ok()) File.Move(tmp, outJpg, true); } catch { }
        Del();
    }

    // PERF: cap concurrent ffmpeg. A fast scroll over hundreds of gif cards fires many /still + /anim
    // requests at once; ungated, each spawns up to 3 ffmpeg → CPU meltdown + slow thumbnails. Bound to
    // ~half the cores (min 2, max 6) so extractions run in parallel but never swamp the machine.
    private static readonly SemaphoreSlim _ffmpegGate = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));
    private static string RunFfmpeg(string[] args)
    {
        _ffmpegGate.Wait();
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            // drain both pipes concurrently — a full stderr buffer while we block reading stdout (or vice
            // versa) deadlocks the child; ReadToEndAsync on one + sync read on the other avoids it.
            var errTask = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            var err = errTask.GetAwaiter().GetResult();
            p.WaitForExit(15000);
            return err;
        }
        finally { _ffmpegGate.Release(); }
    }

    // In-flight dedup: concurrent requests for the SAME output file share one extraction Task (instead
    // of N threads racing to write the same path + running N× the ffmpeg work). Keyed by output path.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _extracting = new();
    private static readonly object _logLock = new(); // serializes /debug/log appends
    private static Task ExtractOnce(string outPath, Func<Task> make) =>
        _extracting.GetOrAdd(outPath, key => Task.Run(async () =>
        {
            try { await make(); } finally { _extracting.TryRemove(key, out _); }
        }));

    // LRU-bound the animated-preview cache by total SIZE (gifs vary 0.3–1MB+, so size is more
    // predictable than a file count). Keep the most-recently-used up to maxBytes; delete the rest.
    private static void PruneAnimCache(string dir, long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles().OrderByDescending(f => f.LastAccessTimeUtc).ToArray();
            long total = 0;
            foreach (var f in files)
            {
                total += f.Length;
                if (total > maxBytes) try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        // ---- sources / browse / detail ----------------------------------------------------
        app.MapGet("/sources", () =>
        {
            bool autoImport = SettingsService.Load().AutoImportWallpaperEngine;
            return Results.Json(Providers.Select((p, i) => new
            {
                index = i, name = p.Name,
                supportsSearch = p.SupportsSearch, supportsPagination = p.SupportsPagination,
                supportsSorting = p.SupportsSorting, supportsTagFilter = p.SupportsTagFilter,
                pageSizeHint = p.PageSizeHint,
                available = !(i == 3 && autoImport), // WE-local hidden when auto-import on
            }));
        });

        app.MapPost("/browse", async (BrowseReq r) =>
        {
            if (r.Source < 0 || r.Source >= Providers.Length) return Results.BadRequest("bad source");
            var p = Providers[r.Source];
            ConfigureProvider(p, SettingsService.Load(), r.Filter); // refresh AllowScenes/path/filter from live settings
            var page = r.Page <= 0 ? 1 : r.Page;
            var list = !string.IsNullOrWhiteSpace(r.Query) && p.SupportsSearch
                ? await p.SearchAsync(r.Query!, page)
                : await p.GetLatestAsync(page);
            return Results.Json(list);
        });

        app.MapPost("/detail", async (DetailReq r) =>
        {
            if (r.Source < 0 || r.Source >= Providers.Length) return Results.BadRequest("bad source");
            ConfigureProvider(Providers[r.Source], SettingsService.Load(), null);
            return Results.Json(await Providers[r.Source].GetDetailAsync(r.Result));
        });

        // ---- settings / themes ------------------------------------------------------------
        app.MapGet("/settings", () => Results.Json(SettingsService.Load()));
        app.MapGet("/monitors", async () => Results.Json(await MonitorDetector.DetectAsync()));

        // DIAGNOSTIC: the renderer posts a heartbeat (+ long-task events) here; appended to a capped log
        // in a SEPARATE process so it survives a renderer main-thread wedge. When the UI freezes the
        // heartbeat stops — the gap timestamps the wedge and the last line before it = the trigger context
        // (tab, scrolling, gif/anim count, JS heap, fps). Read ~/.cache/livepaper/freeze.log after a freeze.
        app.MapPost("/debug/log", async (HttpContext ctx) =>
        {
            string body;
            using (var sr = new StreamReader(ctx.Request.Body)) body = await sr.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(body))
                try
                {
                    var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "livepaper", "freeze.log");
                    lock (_logLock)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        if (File.Exists(path) && new FileInfo(path).Length > 5_000_000) File.Move(path, path + ".1", true);
                        File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {body.Trim()}\n");
                    }
                }
                catch { }
            return Results.Ok();
        });
        // clear the re-downloadable preview caches only (NOT steamcmd downloads or state files)
        app.MapPost("/cache/clear", () =>
        {
            var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "livepaper");
            long freed = 0; int count = 0;
            foreach (var sub in new[] { "workshop_previews", "we_thumbs" })
            {
                var d = Path.Combine(baseDir, sub);
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d))
                    try { freed += new FileInfo(f).Length; File.Delete(f); count++; } catch { }
            }
            return Results.Json(new { freedBytes = freed, count });
        });
        app.MapPost("/settings", (AppSettings s) =>
        {
            var prev = SettingsService.Load();
            // The UI caches the whole AppSettings at load and re-POSTs it on every change. Fields
            // mutated backend-side WHILE the UI is open (LastSession via apply/random/next; steam
            // tokens via QR sign-in) would be clobbered by the stale copy. Preserve those from disk
            // — they are never edited through this form.
            s.LastSession = prev.LastSession;
            s.SteamRefreshToken = prev.SteamRefreshToken;
            s.SteamAccessToken = prev.SteamAccessToken;
            s.SteamAccountName = prev.SteamAccountName;
            s.SteamLoginSecure = prev.SteamLoginSecure;
            SettingsService.Save(s);
            // live-apply the cheap ones, but respect the PLAYING wallpaper's per-item override so a global
            // change never clobbers an active override (effective = override ?? new global).
            var curPlaying = PlayerHelper.QueryCurrentPath();
            PlayerHelper.SetVolume((curPlaying != null ? LibraryService.ReadVolumeOverride(curPlaying) : null) ?? s.Volume);
            PlayerHelper.SetSpeed((curPlaying != null ? LibraryService.ReadSpeedOverride(curPlaying) : null) ?? s.Speed);
            PlayerHelper.SetVideoScale(s.VideoScale); // fill/fit applies live to the playing video (no relaunch)
            // loudness normalization: apply live to the playing wallpaper (toggle/retune now), and when
            // it's on, (re)start the background measurement backfill so all clips get measured while open.
            PlayerHelper.ApplyNormalizationLive(curPlaying);
            if (s.NormalizeAudio) _ = LibraryService.BackfillLoudnessAsync();
            // live-apply the remaining playback settings to the playing wallpaper (Loop, NoAudio,
            // VideoFps, DisableCache, Demuxer*, HwDec + scene mute) — formerly needed a stop/replay.
            PlayerHelper.ApplyPlaybackSettingsLive(prev, s, curPlaying);
            // restart-daemon interval/mode changed → re-arm the in-process leak-restart timer now
            // (0 → off) instead of only on the next play.
            if (prev.RestartIntervalSeconds != s.RestartIntervalSeconds || prev.RestartOnSwitchOnly != s.RestartOnSwitchOnly)
                PlayerHelper.UpdateRestartTimer();
            // rotation (interval / advance-on-end / wait-for-video-end) changed → live-update a RUNNING
            // timed playlist that follows the GLOBAL settings (a playlist with its own override is
            // updated by /playlist/state instead). No replay needed.
            if (PlayerHelper.IsTimedPlaylistActive()
                && (prev.GlobalIntervalSeconds != s.GlobalIntervalSeconds
                    || prev.GlobalAdvanceOnVideoEnd != s.GlobalAdvanceOnVideoEnd
                    || prev.GlobalWaitForVideoEnd != s.GlobalWaitForVideoEnd)
                && !(PlaylistService.LoadCurrentState()?.Settings.OverrideGlobalSettings ?? false))
            {
                PlayerHelper.ApplyRotationLive(s.GlobalIntervalSeconds, s.GlobalWaitForVideoEnd, s.GlobalAdvanceOnVideoEnd);
            }
            // React live only when an AutoMute field changed — Start (restarts) when enabled,
            // Stop (also unmutes) when disabled. Applies to the currently-playing wallpaper immediately.
            bool amChanged = prev.AutoMute != s.AutoMute
                || prev.AutoMuteDelayMs != s.AutoMuteDelayMs
                || prev.AutoUnmuteDelayMs != s.AutoUnmuteDelayMs
                || prev.AutoMuteThresholdDb != s.AutoMuteThresholdDb
                || prev.AutoMuteOnlyIfMprisActive != s.AutoMuteOnlyIfMprisActive;
            if (amChanged)
            {
                if (s.AutoMute)
                    AudioMonitor.Start(s.AutoMuteDelayMs, s.AutoUnmuteDelayMs, s.AutoMuteThresholdDb, s.AutoMuteOnlyIfMprisActive);
                else
                    AudioMonitor.Stop();
            }
            return Results.Ok();
        });
        app.MapGet("/themes", () => Results.Json(ThemeService.All));
        app.MapGet("/mpv-preview", () => Results.Text(SettingsService.Load().BuildMpvOptions()));
        app.MapGet("/lwe-preview", () => Results.Text(SettingsService.Load().BuildLweOptions()));
        app.MapPost("/library/import", async (ImportReq r) =>
        {
            var item = await ImportService.ImportAsync(r.Path, r.Title);
            return item == null ? Results.BadRequest("import failed") : Results.Json(item);
        });

        // ---- library ----------------------------------------------------------------------
        app.MapGet("/library", () => Results.Json(LibraryService.LoadAll()));
        app.MapPost("/library/delete", (LibraryItem item) => { LibraryService.Delete(item); return Results.Ok(); });
        app.MapPost("/library/clear", () => { LibraryService.DeleteAll(); return Results.Ok(); });
        // save the override (drops to global when == global) THEN apply it live to the playing wallpaper
        app.MapPost("/library/volume", (OverrideReq r) => { LibraryService.SaveVolumeOverride(r.Path, r.IntValue); PlayerHelper.ApplyOverrideLive(r.Path); return Results.Ok(); });
        app.MapPost("/library/speed", (OverrideReq r) => { LibraryService.SaveSpeedOverride(r.Path, r.DoubleValue); PlayerHelper.ApplyOverrideLive(r.Path); return Results.Ok(); });
        // Live PREVIEW during a slider drag: set mpv/LWE volume|speed NOW — no persist, no QueryCurrentPath.
        // Cheap vs hammering /library/volume per tick (which writes index.json + 2 IPC calls + a pactl spawn
        // for scenes, ×~60/s → backlog so the desktop only catches up on release). The UI throttles these and
        // gates on "the edited item is the playing one", then persists once on release via /library/{volume,speed}.
        app.MapPost("/preview", (PreviewReq r) => { if (r.Volume.HasValue) PlayerHelper.SetVolume(r.Volume.Value); if (r.Speed.HasValue) PlayerHelper.SetSpeed(r.Speed.Value); return Results.Ok(); });
        app.MapPost("/library/whitelist", (WhitelistReq r) => { LibraryService.SetWhitelisted(r.Path, r.Value); return Results.Ok(); });
        app.MapPost("/library/sync", () =>
        {
            var s = SettingsService.Load();
            var added = LibraryService.SyncWallpaperEngine(s.WallpaperEnginePath, s.AllowScenes, s.WeCopyFiles);
            EventBus.Broadcast("library-synced", new { count = added.Count });
            return Results.Json(new { added });
        });

        // ---- trash / undo (soft delete) ---------------------------------------------------
        app.MapPost("/library/trash", (TrashReq r) => Results.Json(new { batch = AppOps.Trash(r.Items) }));
        app.MapPost("/library/undo", () => { var u = AppOps.Undo(); return Results.Json(new { ok = u.Ok, count = u.Count, title = u.Title }); });
        app.MapGet("/library/undo-depth", () => Results.Json(new { depth = AppOps.UndoDepth() }));
        app.MapGet("/library/duration", (string path) => Results.Json(new { seconds = AppOps.GetDurationSeconds(path) }));

        // ---- download (web sources; workshop-acquire = Phase 5) ---------------------------
        app.MapPost("/download", async (DownloadReq r, HttpContext ctx) =>
        {
            if (r.Source < 0 || r.Source >= Providers.Length) return Results.BadRequest("bad source");
            var detail = await Providers[r.Source].GetDetailAsync(r.Result);
            var item = await AppOps.DownloadAsync(detail, r.Result.ThumbnailUrl, r.Result.PageUrl, ctx.RequestAborted, r.Result.AnimatedThumbnailUrl);
            if (r.Apply) { var s = SettingsService.Load(); PlayerHelper.Apply(item.VideoPath, s.BuildMpvOptions());
                s.LastSession = new LastSession { Paths = new List<string> { item.VideoPath } }; SettingsService.Save(s); }
            return Results.Json(item);
        });

        // ---- transitions ------------------------------------------------------------------
        // The effect catalog (id, name, category, defaultOn, uniforms) for the picker UI.
        app.MapGet("/transitions", () => Results.Json(TransitionService.Manifest()));
        // Live-preview assets: the composed fragment per effect + the shared vertex shader + two
        // sample frames. The UI compiles the SAME source the native renderer does (WYSIWYG previews).
        app.MapGet("/transitions/frag/{id}", (string id) =>
            TransitionService.ComposedFragment(id) is { } f ? Results.Text(f, "text/plain") : Results.NotFound());
        app.MapGet("/transitions/vert", () =>
            TransitionService.VertSource() is { } v ? Results.Text(v, "text/plain") : Results.NotFound());
        app.MapGet("/transitions/preview/{which}", (string which) =>
            TransitionService.PreviewImagePath(which) is { } p ? Results.File(p, "image/jpeg") : Results.NotFound());

        // ---- playlist ---------------------------------------------------------------------
        app.MapGet("/playlist/state", () => Results.Json(PlaylistService.LoadCurrentState()));
        app.MapPost("/playlist/state", (CustomPlaylist p) =>
        {
            var prevP = PlaylistService.LoadCurrentState();
            PlaylistService.SaveCurrentState(p);
            // Live-apply a rotation change (interval / advance-on-end / wait, or the override toggle) to
            // the RUNNING timed playlist — no replay. Diff the EFFECTIVE rotation vs the previously-saved
            // state so a plain reorder/add (which also POSTs state) never resets the countdown. When the
            // playlist follows globals, /settings owns the live-apply (this stays a no-op then).
            if (PlayerHelper.IsTimedPlaylistActive())
            {
                // Order (Sequential↔Shuffle) or the strip contents (drag-reorder / add / remove) changed →
                // reflect it on the RUNNING playlist live (keeps the current item playing + the countdown).
                // Diffed so a rotation/transition-only save doesn't needlessly re-shuffle. Reorder FIRST so
                // it rebuilds _timedPaths before the rotation re-chains the video-end wait.
                bool orderChanged = (prevP?.Settings.Order ?? PlaylistOrder.Sequential) != p.Settings.Order;
                bool listChanged = prevP == null || !prevP.VideoPaths.SequenceEqual(p.VideoPaths);
                if ((orderChanged || listChanged) && p.VideoPaths.Count > 0)
                    PlayerHelper.ReorderPlaylist(p.VideoPaths, true, p.Settings.Order == PlaylistOrder.Shuffle);

                var g = SettingsService.Load();
                (int iv, bool wait, bool adv) Eff(CustomPlaylist? c) =>
                    (c?.Settings.OverrideGlobalSettings ?? false)
                        ? (c!.Settings.IntervalSeconds, c.Settings.WaitForVideoEnd, c.Settings.AdvanceOnVideoEnd)
                        : (g.GlobalIntervalSeconds, g.GlobalWaitForVideoEnd, g.GlobalAdvanceOnVideoEnd);
                var now = Eff(p);
                if (now != Eff(prevP)) PlayerHelper.ApplyRotationLive(now.iv, now.wait, now.adv);
            }
            return Results.Ok();
        });
        app.MapGet("/playlist/names", () => Results.Json(PlaylistService.ListNames()));
        app.MapPost("/playlist/save", (SavePlaylistReq r) => { PlaylistService.Save(r.Name, r.Playlist); return Results.Ok(); });
        app.MapPost("/playlist/load", (NameReq r) => Results.Json(PlaylistService.Load(r.Name)));
        app.MapPost("/playlist/play", (PlayReq r) => Results.Json(new { message = AppOps.PlayPlaylist(r.Paths, r.Settings ?? new()) }));
        app.MapPost("/playlist/play-from", (PlayFromReq r) => Results.Json(new { message = AppOps.PlayFrom(r.Paths, r.StartIndex, r.Settings ?? new()) }));

        // ---- open externally (file manager / web page) ------------------------------------
        // "Open Page" (xdg-open url) / "Open in File Manager" (reveal → parent dir).
        app.MapPost("/open", (OpenReq r) =>
        {
            try
            {
                // Library reveal: resolve the real media (symlink target / scene dir), not the .id/.scene.
                if (r.Url == null && r.Reveal && r.Path != null)
                    AppOps.RevealLibraryItem(r.Path, r.IsScene, r.WorkshopId, r.CopiedSceneDir);
                else
                {
                    string target = r.Url ?? r.Path ?? "";
                    if (target.Length > 0)
                        Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false });
                }
            }
            catch { }
            return Results.Ok();
        });

        // ---- steam / workshop -------------------------------------------------------------
        app.MapGet("/steam/status", () => Results.Json(SteamOps.Status()));
        app.MapPost("/steam/qr/start", () => { SteamOps.StartQr(); return Results.Ok(); });
        app.MapPost("/steam/qr/cancel", () => { SteamOps.CancelQr(); return Results.Ok(); });
        app.MapPost("/steam/signout", () => { SteamOps.SignOut(); return Results.Ok(); });
        // steamcmd one-time interactive sign-in (caches sentry; later runs non-interactive).
        app.MapPost("/steam/steamcmd-signin", () =>
        {
            var s = SettingsService.Load();
            var exe = WorkshopDownloader.FindSteamCmd(s.SteamCmdPath);
            if (exe == null) return Results.BadRequest("steamcmd not found");
            WorkshopDownloader.LaunchSteamCmdSignIn(exe, s.SteamUsername);
            return Results.Ok();
        });
        app.MapPost("/workshop/delete-from-source", (TrashReq r) => Results.Json(new { batch = AppOps.DeleteFromSource(r.Items) }));
        app.MapPost("/workshop/drain", () => { SteamOps.StartDrain(); return Results.Ok(); });
        app.MapGet("/workshop/queue", () => Results.Json(new
        {
            pending = WorkshopUnsubQueue.SnapshotPending(), hasPending = WorkshopUnsubQueue.HasPending(),
        }));

        // ---- playback ---------------------------------------------------------------------
        app.MapPost("/apply", (PathReq r) =>
        {
            var s = SettingsService.Load();
            PlayerHelper.Apply(r.Path, s.BuildMpvOptions());      // apply + save as last session
            s.LastSession = new LastSession { Paths = new List<string> { r.Path } };
            SettingsService.Save(s);
            return Results.Ok();
        });
        app.MapPost("/stop", () => { PlayerHelper.Stop(); return Results.Ok(); });
        app.MapPost("/next", () => { PlayerHelper.NextWallpaper(); return Results.Ok(); });
        app.MapPost("/prev", () => { PlayerHelper.PreviousWallpaper(); return Results.Ok(); });
        app.MapPost("/random", () => { PlayerHelper.ApplyRandom(); return Results.Ok(); });
        app.MapPost("/pause", () => { PlayerHelper.TogglePause(); return Results.Ok(); });
        app.MapPost("/volume", (ValueReq r) =>
        {
            if (r.Delta is int d) PlayerHelper.AdjustVolume(d);
            else if (r.Value is int v) PlayerHelper.SetVolume(v);
            return Results.Ok();
        });
        app.MapPost("/mute", (MuteReq r) =>
        {
            if (r.User) PlayerHelper.SetUserMute(r.Value); else PlayerHelper.SetMute(r.Value);
            return Results.Ok();
        });
        app.MapGet("/current", () => Results.Json(new
        {
            path = PlayerHelper.QueryCurrentPath(),
            sceneId = PlayerHelper.QueryCurrentSceneWorkshopId(),
            playing = PlayerHelper.IsPlaying,
            timed = PlayerHelper.IsTimedPlaylistActive(),
            position = PlayerHelper.QueryTimePos(), // live mpv time-pos → sync the in-app preview
        }));

        // ---- image proxy + local media ----------------------------------------------------
        // API: browser can't send the Firefox UA / Referer (moewalls needs it) and would hit
        // CORS — proxy remote thumbs/gifs through here. ?u=<url>&r=<referer>
        app.MapGet("/img", async (string u, string? r, HttpContext ctx) =>
        {
            try
            {
                using var msg = new HttpRequestMessage(HttpMethod.Get, u);
                msg.Headers.TryAddWithoutValidation("User-Agent", FirefoxUA);
                if (!string.IsNullOrEmpty(r)) msg.Headers.Referrer = new Uri(r);
                using var resp = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead);
                ctx.Response.StatusCode = (int)resp.StatusCode;
                ctx.Response.ContentType = resp.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                await resp.Content.CopyToAsync(ctx.Response.Body);
            }
            catch { ctx.Response.StatusCode = 502; }
        });

        // local library/cache media (loopback-only; the app already owns the filesystem)
        app.MapGet("/media", (string path) =>
        {
            if (!File.Exists(path)) return Results.NotFound();
            var ct = Mime.TryGetContentType(path, out var t) ? t : "application/octet-stream";
            // Stream a seekable FileStream with range support — <video> sends `Range: bytes=…` and the
            // Results.File(path,…) overload was 500ing on it, so the in-app wallpaper preview wouldn't load.
            var fs = File.OpenRead(path);
            return Results.Stream(fs, ct, enableRangeProcessing: true);
        });

        // Animated preview (workshop gifs): like library, serve from a LOCAL CACHED FILE with range/length
        // instead of the live /img proxy (chunked, no length) — the live stream can't loop cleanly, so the
        // gif flashed black at the loop seam. Cache once, then serve the stable file → loops smoothly.
        app.MapGet("/anim", async (string u) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache", "livepaper", "workshop_previews");
                Directory.CreateDirectory(dir);
                bool remote = Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");

                // LOCAL gif (library/playlist): a raw local .gif flashes black at the fade-from-black loop
                // seam just like a remote one, and /media serves it untrimmed. So trim local gifs the same
                // way (cache keyed on path+mtime). Other local formats (webp/png) → serve as-is via /media.
                if (!remote)
                {
                    if (!File.Exists(u)) return Results.NotFound();
                    if (!u.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                        return Results.Stream(File.OpenRead(u), Mime.TryGetContentType(u, out var lt) ? lt : "image/gif", enableRangeProcessing: true);
                    string lkey = u; try { lkey = u + "|" + new FileInfo(u).LastWriteTimeUtc.Ticks; } catch { }
                    var lhash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lkey)))[..24];
                    var trimmed = Path.Combine(dir, lhash + "_trim.gif");
                    if (!File.Exists(trimmed))
                        await ExtractOnce(trimmed, async () =>
                        {
                            if (File.Exists(trimmed)) return;
                            await CacheGifTrimmed(await File.ReadAllBytesAsync(u), trimmed); // strip leading fade-from-black
                            PruneAnimCache(dir, 1024L * 1024 * 1024);
                        });
                    try { File.SetLastAccessTimeUtc(trimmed, DateTime.UtcNow); } catch { }
                    return Results.Stream(File.OpenRead(trimmed), "image/gif", enableRangeProcessing: true);
                }

                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(u)))[..24];
                var existing = Directory.GetFiles(dir, hash + ".*").FirstOrDefault();
                if (existing == null)
                {
                    // dedup keyed on the hash (ext unknown until fetched) → concurrent hits share one download+trim
                    await ExtractOnce(Path.Combine(dir, hash), async () =>
                    {
                        if (Directory.GetFiles(dir, hash + ".*").Any()) return;
                        using var msg = new HttpRequestMessage(HttpMethod.Get, u);
                        msg.Headers.TryAddWithoutValidation("User-Agent", FirefoxUA);
                        using var resp = await Http.SendAsync(msg);
                        resp.EnsureSuccessStatusCode();
                        var mt = resp.Content.Headers.ContentType?.MediaType ?? "image/gif";
                        var ext = mt.Contains("webp") ? ".webp" : mt.Contains("png") ? ".png" : mt.Contains("jpeg") ? ".jpg" : ".gif";
                        var outp = Path.Combine(dir, hash + ext);
                        var bytes = await resp.Content.ReadAsByteArrayAsync();
                        if (ext == ".gif") await CacheGifTrimmed(bytes, outp); // strip leading fade-from-black
                        else await File.WriteAllBytesAsync(outp, bytes);
                        PruneAnimCache(dir, 1024L * 1024 * 1024); // ~1 GB LRU cap (an updated preview = a new immutable URL → new file)
                    });
                    existing = Directory.GetFiles(dir, hash + ".*").FirstOrDefault();
                }
                if (existing == null) return Results.StatusCode(502);
                try { File.SetLastAccessTimeUtc(existing, DateTime.UtcNow); } catch { } // touch for LRU
                var ctOut = Mime.TryGetContentType(existing, out var t) ? t : "image/gif";
                return Results.Stream(File.OpenRead(existing), ctOut, enableRangeProcessing: true);
            }
            catch { return Results.StatusCode(502); }
        });

        // Static thumbnail for an animated gif/webp preview: a non-black/white still, so AutoPlayGifs-off /
        // the loop-gap shows a real frame, not the gif's black frame-0 — and so a gif used as a *static*
        // thumbnail (library items whose only
        // sibling image is the preview gif) doesn't animate on its own. `u` = remote URL OR local path.
        app.MapGet("/still", async (string u, int? w) =>
        {
            try
            {
                // decode-to-size: card layer asks for w=<card px> (small texture); enlarged modal omits w
                // → native still. Bucket to 64px so a handful of cache entries cover all card sizes.
                int width = w is > 0 ? Math.Min(2048, ((w.Value + 63) / 64) * 64) : 0;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".cache", "livepaper", "workshop_previews");
                Directory.CreateDirectory(dir);
                bool remote = Uri.TryCreate(u, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https");
                // local: key the cache on path+mtime so a replaced file re-extracts; remote URLs are immutable
                string key = u;
                if (!remote) { try { key = u + "|" + new FileInfo(u).LastWriteTimeUtc.Ticks; } catch { } }
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
                var jpg = Path.Combine(dir, hash + (width > 0 ? $"_still{width}.jpg" : "_still.jpg"));
                if (!remote && !File.Exists(u)) return Results.NotFound();
                if (!File.Exists(jpg))
                    await ExtractOnce(jpg, async () =>
                    {
                        if (File.Exists(jpg)) return; // won the race already
                        string srcGif; bool tempSrc = false;
                        if (remote)
                        {
                            using var msg = new HttpRequestMessage(HttpMethod.Get, u);
                            msg.Headers.TryAddWithoutValidation("User-Agent", FirefoxUA);
                            using var resp = await Http.SendAsync(msg);
                            resp.EnsureSuccessStatusCode();
                            srcGif = jpg + ".src.gif"; tempSrc = true;
                            await File.WriteAllBytesAsync(srcGif, await resp.Content.ReadAsByteArrayAsync());
                        }
                        else srcGif = u; // ffmpeg reads the local gif/webp directly
                        ExtractStill(srcGif, jpg, width);
                        if (tempSrc) { try { File.Delete(srcGif); } catch { } }
                        PruneAnimCache(dir, 1024L * 1024 * 1024);
                    });
                if (!File.Exists(jpg)) return Results.StatusCode(502);
                try { File.SetLastAccessTimeUtc(jpg, DateTime.UtcNow); } catch { }
                return Results.Stream(File.OpenRead(jpg), "image/jpeg", enableRangeProcessing: true);
            }
            catch { return Results.StatusCode(502); }
        });

        // ---- WebSocket events -------------------------------------------------------------
        app.Map("/events", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var sock = await ctx.WebSockets.AcceptWebSocketAsync();
            var id = Guid.NewGuid();
            EventBus.Add(id, sock);
            var buf = new byte[1024];
            try
            {
                while (sock.State == WebSocketState.Open)
                {
                    var res = await sock.ReceiveAsync(buf, CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }
            finally { EventBus.Remove(id); }
        });
    }

    // ---- request DTOs --------------------------------------------------------------------
    public record BrowseReq(int Source, int Page, string? Query, WorkshopFilter? Filter);
    public record DetailReq(int Source, WallpaperResult Result);
    public record PathReq(string Path);
    public record OverrideReq(string Path, int? IntValue, double? DoubleValue);
    public record PreviewReq(int? Volume, double? Speed);
    public record WhitelistReq(string Path, bool Value);
    public record ValueReq(int? Delta, int? Value);
    public record MuteReq(bool User, bool Value);
    public record TrashReq(List<LibraryItem> Items);
    public record DownloadReq(int Source, WallpaperResult Result, bool Apply);
    public record SavePlaylistReq(string Name, CustomPlaylist Playlist);
    public record NameReq(string Name);
    public record PlayReq(List<string> Paths, PlaylistSettings? Settings);
    public record PlayFromReq(List<string> Paths, int StartIndex, PlaylistSettings? Settings);
    public record ImportReq(string Path, string Title);
    public record OpenReq(string? Path, string? Url, bool Reveal, bool IsScene = false, string? WorkshopId = null, string? CopiedSceneDir = null);
}
