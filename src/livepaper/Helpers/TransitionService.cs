using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using livepaper.Models;

namespace livepaper.Helpers;

// WE-style wallpaper transitions: freeze the outgoing wallpaper (`from`) and the incoming one's
// first frame (`to`), then run a gl-transitions GLSL effect between them on an opaque per-output
// wlr-layer-shell surface (the `lp-transition` native renderer), and hand off to the live wallpaper
// underneath. The overlay covers only the wallpaper region (real windows stay on top), exactly like
// Wallpaper Engine. See .claude/rules/player.md + transitions/README.md.
//
// This service is the orchestration layer PlayerHelper.SwitchToFile calls: it picks the effect,
// captures the two frames per output (ffmpeg for video, grim for a live scene, the item's preview
// image for an incoming scene), composes the shader, and spawns the renderer detached.
public static class TransitionService
{
    public sealed record Config(bool Enabled, List<string> EffectIds, int DurationMs, int DurationMaxMs, bool Shuffle);

    private static readonly Random _rng = new();
    private static string? _lastEffect;
    private static readonly object _lock = new();

    // ---- effective config (per-playlist override vs global), mirrors AppOps.Eff* ---------------
    public static Config Effective(AppSettings s, PlaylistSettings? p)
    {
        bool ovr = p?.OverrideGlobalSettings == true;
        return ovr
            ? new Config(p!.TransitionEnabled, p.TransitionEffectIds ?? [], p.TransitionDurationMs, p.TransitionDurationMaxMs, p.TransitionShuffle)
            : new Config(s.GlobalTransitionEnabled, s.GlobalTransitionEffectIds ?? [], s.GlobalTransitionDurationMs, s.GlobalTransitionDurationMaxMs, s.GlobalTransitionShuffle);
    }

    // The effective config for the CURRENT session (single apply => global; playlist => its settings).
    public static Config CurrentConfig()
    {
        var s = SettingsService.Load();
        var ps = PlaylistService.LoadCurrentState()?.Settings;
        return Effective(s, ps);
    }

    public static bool Available => ResolveDir() != null && ResolveBinary() != null;

    // ---- effect / duration selection -----------------------------------------------------------
    public static string? PickEffect(Config c)
    {
        var ids = (c.EffectIds ?? []).Where(EffectExists).Distinct().ToList();
        if (ids.Count == 0) return null;
        if (ids.Count == 1) return ids[0];
        lock (_lock)
        {
            string pick;
            if (c.Shuffle)
            {
                var pool = ids.Where(i => i != _lastEffect).ToList();
                if (pool.Count == 0) pool = ids;
                pick = pool[_rng.Next(pool.Count)];
            }
            else // sequential cycle through the enabled set
            {
                int idx = _lastEffect == null ? -1 : ids.IndexOf(_lastEffect);
                pick = ids[(idx + 1) % ids.Count];
            }
            _lastEffect = pick;
            return pick;
        }
    }

    // Like PickEffect but restricted to reveal-capable effects — used when B is a live scene (warp
    // effects can't sample the un-textured scene). null → none of the enabled effects are reveal-type.
    public static string? PickRevealEffect(Config c)
    {
        var ids = (c.EffectIds ?? []).Where(EffectExists).Where(IsRevealEffect).Distinct().ToList();
        if (ids.Count == 0) return null;
        if (ids.Count == 1) return ids[0];
        lock (_lock)
        {
            string pick;
            if (c.Shuffle)
            {
                var pool = ids.Where(i => i != _lastEffect).ToList();
                if (pool.Count == 0) pool = ids;
                pick = pool[_rng.Next(pool.Count)];
            }
            else
            {
                int idx = _lastEffect == null ? -1 : ids.IndexOf(_lastEffect);
                pick = ids[(idx + 1) % ids.Count];
            }
            _lastEffect = pick;
            return pick;
        }
    }

    public static int PickDuration(Config c)
    {
        int min = Math.Clamp(c.DurationMs, 50, 10000);
        if (c.DurationMaxMs > min) return _rng.Next(min, Math.Clamp(c.DurationMaxMs, min, 10000) + 1);
        return min;
    }

    // Wall-clock tick after which the most-recently-started overlay's effect+teardown is done.
    // True while an overlay is animating → the timed advance-on-end re-arm waits this out before
    // reading mpv playtime-remaining (mid file-swap, mpv still reports the OUTGOING file's near-end
    // value → would read as "ended now" → spurious immediate second switch / rapid A→B→A flip).
    private static long _activeUntilTicks;
    public static bool InProgress => DateTime.UtcNow.Ticks < System.Threading.Interlocked.Read(ref _activeUntilTicks);

    // Last measured spawn→cover latency (ms): how long the renderer took to warm its libmpv decoders and
    // paint the first opaque frame after spawn. The timed video-end advance leads by this + the effect
    // duration so the transition COMPLETES at the video's natural end (else the overlay's live A-decode
    // loops back to A's start during the effect tail). Machine/resolution-stable → a good next-run estimate.
    public static int LastCoverMs { get; private set; }

    // One-shot: the timed video-end advance sets this right before its SwitchToFile → the next V→V
    // full-live transition passes --align-a-end, and the RENDERER gates the effect start on A's DECODED
    // remaining so progress hits 1 exactly at A's EOF (closed-loop — the lead above is only slack).
    private static bool _alignAEnd;
    public static void RequestAlignAEnd() => _alignAEnd = true;

    // Stop/kill: the marked transition is dead — clear InProgress (and the align request) so the
    // NEXT session's first video-end wait isn't stalled by a stale marker (it delays the sample by
    // up to durationMs+5s → a short first clip would fire its transition late → end-freeze).
    public static void ClearInProgress()
    {
        System.Threading.Interlocked.Exchange(ref _activeUntilTicks, 0);
        _alignAEnd = false;
    }

    // ---- the entry SwitchToFile calls ----------------------------------------------------------
    // Captures both frames per output (warmup/scene fallback), composes the shader, and spawns
    // lp-transition detached. FULL-LIVE: for a video side it also hands the renderer the actual file
    // (`--from-video`/`--to-video` + start offset) so the renderer decodes it with libmpv and BOTH
    // sides keep PLAYING through the effect. The incoming video B is played LIVE underneath the
    // opaque overlay (the caller loads it unpaused) and is revealed at teardown at a matching position.
    // Returns true if the overlay was started (caller hides its switch under it); false => instant cut.
    public static bool TryStart(string? fromPath, bool fromScene, string toPath, bool toScene, Config cfg, out bool pauseB)
    {
        pauseB = false;
        bool alignA = _alignAEnd; _alignAEnd = false; // one-shot — consume even on an early bail
        try
        {
            if (!cfg.Enabled || string.IsNullOrEmpty(fromPath)) return false;
            var dir = ResolveDir(); var bin = ResolveBinary();
            if (dir == null || bin == null) return false;
            // Kill any prior overlay before starting a new one — a transition the user PAUSED is frozen
            // and never tears itself down, and rapid switches could otherwise stack overlays.
            foreach (var p in Process.GetProcessesByName("lp-transition"))
                using (p) { try { p.Kill(true); } catch { } }
            var effect = PickEffect(cfg);
            if (effect == null) return false;
            int durationMs = PickDuration(cfg);

            var monitors = MonitorDetector.DetectAsync().GetAwaiter().GetResult();
            if (monitors.Count == 0) return false;

            // per-transition scratch dir (cleaned up after the run)
            string work = Path.Combine(RuntimeDir(), "transition", DateTime.UtcNow.Ticks.ToString());
            Directory.CreateDirectory(work);

            string vert = Path.Combine(dir, "wrap.vert");
            // (the effect fragment is composed AFTER the method/effect resolution below — the effect
            //  gate may re-pick a reveal-capable effect for a live-scene B)

            double? timePos = fromScene ? null : PlayerHelper.QueryTimePos();
            double epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; // wall clock at the A-position sample
            double? fromDur = fromScene ? null : PlayerHelper.QueryDuration();      // wrap A's advanced start (loops)
            var aset = SettingsService.Load();
            int audioVol = (aset.NoAudio || PlayerHelper.IsMuted) ? 0 : aset.Volume; // crossfade target (0 = silent, matches a muted wallpaper)
            _captureScale = (aset.VideoScale ?? "fill").Equals("fit", StringComparison.OrdinalIgnoreCase) ? "fit" : "fill"; // match mpvpaper's Video scale
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // ── resolve the method for THIS switch (auto-fallback: stay as close to the picker as the
            //    A/B kinds allow; reveal is the universal mode every invalid pick lands on) ──────────
            //  reveal    = freeze A, reveal the LIVE B underneath (alpha; B = mpvpaper OR LWE scene)
            //  frozen    = both sides frozen stills (B handed off frame0→frame0; needs a video B)
            //  full-live = overlay decodes A+B live (both video)
            //  reveal-b  = full-live A (live decode) + reveal a LIVE scene-B underneath (V→S full-live)
            string method = (aset.TransitionMethod ?? "reveal").ToLowerInvariant();
            if (method != "frozen" && method != "full-live" && method != "reveal") method = "reveal";
            // full-live: each VIDEO side is decoded live into a texture; a SCENE side can't be a live
            // texture → A scene = grim still (fromTex fallback), B scene = revealed transparent. So
            // full-live STAYS full-live even for S→V — still-A + live-decoded-B, BOTH textured → ALL
            // effects (warp included), B plays live. Only a SCENE B forces the transparent reveal-b path.
            // frozen needs a textured video-B for a clean frame0→frame0 handoff; a scene B (no matchable
            // still — continuous animation, no seek) would jump → reveal it live instead.
            if (method == "frozen" && toScene) method = "reveal";
            // S→V LIVE-COMPOSITE ("reveal-a"): A is a scene → instead of capturing a still of it (stale
            // frame / baked-in windows) we leave it LIVE underneath and decode B in the overlay, blended
            // over the live scene by the compositor's alpha. No scene-A capture, B plays live (real audio
            // crossfade). Uses the renderer's decoder path (--method full-live) + --reveal-a.
            bool revealA = fromScene && !toScene;
            if (revealA) method = "full-live";
            bool revealB = method == "full-live" && toScene && !revealA;     // V→S / S→S: (decode|still) A + reveal scene-B
            bool fullLive = method == "full-live" && !toScene && !fromScene; // V→V: decode A + decode B → all effects
            bool revealMode = method == "reveal";
            // 4) effect gate: when a side is live-revealed the effect must be reveal-capable (sample the
            //    textured side unmoved + punch alpha). reveal-a reveals the live scene-A through the
            //    transparent fromTex → a warp effect would sample transparent A → re-pick a reveal effect.
            if ((revealMode || revealB || revealA) && !IsRevealEffect(effect))
            {
                var re = PickRevealEffect(cfg);
                if (re != null) effect = re;
                else if (!toScene && !revealA) { method = "frozen"; revealMode = false; }
                // revealA / scene-B with no reveal effect → leave the warp (reveals, just imperfectly)
            }
            // reveal / reveal-a / reveal-b: B plays LIVE (overlay or underneath), don't pause mpvpaper.
            // frozen / full-live with a video B: load B PAUSED — renderer unpauses at teardown.
            pauseB = !revealMode && !revealB && !revealA && !toScene;

            // compose the effect fragment now that the effect is final
            string frag = Path.Combine(work, "effect.frag");
            File.WriteAllText(frag, ComposeFragment(dir, effect));

            string readyFile = Path.Combine(work, "ready");
            var args = new List<string> { "--duration-ms", durationMs.ToString(), "--vert", vert, "--frag", frag,
                                          "--method", method, "--ready-file", readyFile, "--scale", _captureScale };
            // diagnostic timing log (decoder warm-up, first-frame latency, decode fps) → easy to share
            try {
                string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "livepaper", "lp-transition.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                args.Add("--log"); args.Add(logPath);
            } catch { }
            foreach (var u in EffectUniforms(effect)) args.Add(u);
            // Mirror the DESKTOP loop semantic into the overlay decoders (settings.Loop is exactly
            // what mpvpaper runs with — timed / wait-for-video-end sessions launch with it too):
            // loop ctx → a side shorter than the effect wraps through it like the wallpaper would;
            // Loop off → it freezes on its last frame (and the B pre-seek clamps instead of wrapping).
            args.Add("--from-loop"); args.Add(aset.Loop ? "1" : "0");
            args.Add("--to-loop");   args.Add(aset.Loop ? "1" : "0");
            // Timed video-end V→V: gate the effect on A's decoded remaining (see RequestAlignAEnd).
            // Needs a live A decoder → full-live only (reveal/frozen freeze A at the cover anyway,
            // so there is nothing to align — a still can't rewind).
            if (alignA && fullLive) args.Add("--align-a-end");
            if (fullLive || revealB || revealA)
            {
                // reveal-b (V→S full-live): decode A live + composite TRANSPARENT so the live LWE-B
                // at level 0 shows through the effect (no B decoder — the scene can't be a texture).
                if (revealB) args.Add("--reveal-b");
                // reveal-a (S→V): decode B live + composite TRANSPARENT so the live scene-A at level 0
                // shows through → no scene-A capture; B plays (no --to-paused) for a real audio crossfade.
                if (revealA) args.Add("--reveal-a");
                // hand the renderer the actual videos to decode live; A epoch-compensated to mpvpaper-A's
                // live position (wrapped by its duration for loops), B held until first paint, audio crossfaded.
                if (!fromScene) {
                    args.Add("--from-video"); args.Add(fromPath!);
                    args.Add("--from-start"); args.Add((timePos ?? 0).ToString(inv));
                    args.Add("--from-epoch"); args.Add(epoch.ToString(inv));
                    if (fromDur is > 0) { args.Add("--from-duration"); args.Add(fromDur.Value.ToString(inv)); }
                }
                // B is held PAUSED at frame 0 in the overlay (--to-paused) for ALL video-B paths incl.
                // reveal-a: the effect reveals B's frame 0 (a still), and at teardown the renderer unpauses
                // BOTH the overlay-B and mpvpaper-B from frame 0 → frame0→frame0 handoff, NO seek (exactly
                // what V→V does — B never advances during the effect, so there's no position to match/repeat).
                if (!toScene) { args.Add("--to-video"); args.Add(toPath); args.Add("--to-start"); args.Add("0"); args.Add("--to-paused"); }
                args.Add("--audio-volume"); args.Add(audioVol.ToString());
                args.Add("--lag-offset"); args.Add((aset.TransitionLagOffsetMs / 1000.0).ToString(inv)); // A-sync aim (per-machine)
                // normalize the overlay's OWN audio during the effect (mpvpaper-B only takes over at
                // teardown), so the crossfade is leveled too — same loudnorm filter, per measured side.
                if (aset.NormalizeAudio && !aset.NoAudio)
                {
                    if (!fromScene && LibraryService.LoudnormAf(fromPath!, aset.NormalizeTargetLufs) is { } fa) { args.Add("--from-af"); args.Add(fa); }
                    if (!toScene   && LibraryService.LoudnormAf(toPath,    aset.NormalizeTargetLufs) is { } ta) { args.Add("--to-af");   args.Add(ta); }
                }
                // control socket → a keybind (toggle-mute / volume±) during the effect reaches the
                // overlay's own audio live (mpvpaper isn't audible during full-live).
                args.Add("--control-sock"); args.Add(Path.Combine(RuntimeDir(), "lp-transition.ctl"));
                // mirror the wallpaper's playback options so the LIVE effect matches mpvpaper: per-side
                // effective speed (the A-sync is speed-aware), the fps cap, and the hwdec mode.
                if (!fromScene) { args.Add("--from-speed"); args.Add((LibraryService.ReadSpeedOverride(fromPath!) ?? aset.Speed).ToString(inv)); }
                if (!toScene)   { args.Add("--to-speed");   args.Add((LibraryService.ReadSpeedOverride(toPath)   ?? aset.Speed).ToString(inv)); }
                if (aset.VideoFps > 0) { args.Add("--fps"); args.Add(aset.VideoFps.ToString()); }
                if (!string.IsNullOrWhiteSpace(aset.HwDec)) { args.Add("--hwdec"); args.Add(aset.HwDec); }
            }
            // frozen / full-live / reveal-a with a video B → renderer unpauses mpvpaper's paused-frame0 B
            // at teardown for the seamless frame0→frame0 handoff (reveal-a's mpvpaper-B is launched late but
            // also paused at frame0, so the same unpause applies).
            if (pauseB || revealA) { args.Add("--mpv-unpause"); args.Add(IpcSocket()); }
            // scene B (LWE) is slow to render → hold the opaque cover before revealing so the launch
            // flash stays hidden underneath; a video B is instant → no hold. Reuses SceneTransitionDelayMs
            // (the existing scene-transition lead time) — same knob, no separate setting.
            if (toScene) { args.Add("--reveal-hold-ms"); args.Add(aset.SceneTransitionDelayMs.ToString()); }
            // reveal-a (S→V): B is decoded in the overlay (not at BACKGROUND), so after the effect the
            // overlay must hold the opaque-B frame while the backend launches mpvpaper-B underneath, then
            // tear down → seamless handoff (no flash back to the live scene-A).
            if (revealA) { args.Add("--hold-end-ms"); args.Add("1300"); args.Add("--mpv-seek-b"); }

            TransLog($"START method={method} effect={effect} dur={durationMs}ms posA={timePos?.ToString("F2", inv) ?? "?"} " +
                     $"from={System.IO.Path.GetFileName(fromPath)} to={System.IO.Path.GetFileName(toPath)}");
            var swCap = System.Diagnostics.Stopwatch.StartNew();

            // Frozen/reveal: PAUSE mpvpaper so A freezes on its CURRENT frame; the overlay then covers
            // that same frame → no rewind (A freezes for the transition anyway; this just starts it a
            // touch early). PlayerHelper's B loadfile unpauses it after cover. Full-live/scenes skip.
            bool pauseA = !fullLive && !revealB && !fromScene && timePos.HasValue; // reveal-b decodes A live too
            if (pauseA) { MpvSetPause(true); TransLog("paused mpvpaper → A frozen on current frame"); }

            // Capture A (exact last frame) + B (first frame) ONCE to a png each — mpvpaper mirrors every
            // output, so one SW-accurate decode each, then cheap per-output scales below (was N decodes).
            string aPng = Path.Combine(work, "a.png"), bPng = Path.Combine(work, "b.png");
            bool aOk = fromScene || CaptureFramePng(fromPath!, timePos ?? 0, aPng);   // scene A → grim per output
            bool bOk = revealMode || toScene || CaptureFramePng(toPath, 0, bPng);     // reveal: no B; scene B → preview
            TransLog($"captured A={(fromScene ? "scene" : aOk ? "ok" : "FAIL")} B={(revealMode ? "live" : toScene ? "scene" : bOk ? "ok" : "FAIL")} in {swCap.ElapsedMilliseconds}ms");

            int outputs = 0;
            foreach (var m in monitors)
            {
                int w = m.Width > 0 ? m.Width : 1920, h = m.Height > 0 ? m.Height : 1080;
                string fromRaw = Path.Combine(work, $"{m.Name}.from.raw");
                bool okFrom = fromScene ? GrabSceneFrame(m.Name, w, h, fromRaw)
                                        : aOk && ScalePngToRaw(aPng, w, h, fromRaw);
                if (!okFrom) continue;
                args.Add("--output"); args.Add(m.Name);
                args.Add("--from"); args.Add(fromRaw);
                if (!revealMode && !revealB) // frozen / full-live need B's still (reveal & reveal-b reveal the live B)
                {
                    string toRaw = Path.Combine(work, $"{m.Name}.to.raw");
                    bool okTo = toScene ? RenderScenePreview(toPath, w, h, toRaw)
                                        : bOk && ScalePngToRaw(bPng, w, h, toRaw);
                    if (!okTo) continue;
                    args.Add("--to"); args.Add(toRaw);
                }
                args.Add("--width"); args.Add(w.ToString());
                args.Add("--height"); args.Add(h.ToString());
                outputs++;
            }
            if (outputs == 0) { TryDeleteDir(work); return false; }
            TransLog($"scaled {outputs} output(s); total capture {swCap.ElapsedMilliseconds}ms");

            var psi = new ProcessStartInfo(bin) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var swSpawn = System.Diagnostics.Stopwatch.StartNew();
            var proc = Process.Start(psi);
            if (proc == null) { TryDeleteDir(work); return false; }

            // Block until the overlay covers the screen with live frames (the renderer touches
            // readyFile only once EVERY output has a live A frame; until then it paints transparent
            // and the live wallpaper shows through). Only then does the caller switch mpvpaper to B,
            // so B never flashes during the overlay's libmpv warm-up and the teardown positions match.
            // Capped (~3.5s) so a renderer that never paints can't hang the switch.
            for (int i = 0; i < 440 && !File.Exists(readyFile) && !proc.HasExited; i++)
                System.Threading.Thread.Sleep(8);
            if (File.Exists(readyFile)) LastCoverMs = (int)swSpawn.ElapsedMilliseconds; // feeds the timed-advance lead
            TransLog($"overlay covered after {swSpawn.ElapsedMilliseconds}ms (spawn→cover); switching B now (pauseB={pauseB})");

            // best-effort cleanup of the scratch dir once the overlay is done (it uploads the raws
            // to GL textures at startup, so deleting after the run is safe).
            int grace = durationMs + 4000;
            _ = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(grace); TryDeleteDir(work); });
            // Cover is done; the effect + teardown still run for ~durationMs (then the renderer
            // unpauses mpvpaper-B). Mark in-flight that long (+settle) so a timed advance-on-end
            // re-arm doesn't sample mpv until B is actually playing — see InProgress. align-a-end
            // additionally holds at p=0 for up to the backend's lead slack → widen the window.
            System.Threading.Interlocked.Exchange(ref _activeUntilTicks,
                DateTime.UtcNow.AddMilliseconds(durationMs + (alignA ? 5000 : 1500)).Ticks);
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[transition] start failed: {e.Message}");
            return false;
        }
    }

    // ---- frame capture -------------------------------------------------------------------------
    // Matches mpvpaper's "Video scale": fill = cover (scale-up + center-crop), fit = letterbox.
    // Set per-transition in TryStart; the captured stills are then framed exactly like the wallpaper.
    private static string _captureScale = "fill";
    private static string ScaleVf(int w, int h) =>
        _captureScale == "fit"
            ? $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:color=black"
            : $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";

    // Decode ONE exact frame to a png at source res (no scale → reused for every output's scale).
    // FRAME-ACCURATE: -ss BEFORE -i is fast but snaps to the nearest keyframe (can be SECONDS off →
    // wrong "last frame"). For atSec>0 fast-seek to ~2s before, then -ss AFTER -i decodes to the EXACT
    // frame; atSec≈0 (first frame) is exact. SW decode — hwaccel's per-call cuda init was slower here.
    private static bool CaptureFramePng(string videoPath, double atSec, string png)
    {
        if (!File.Exists(videoPath)) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        string seekArgs;
        if (atSec <= 0.05)
            seekArgs = $"-ss 0 -i \"{videoPath}\"";
        else
        {
            double pre = Math.Max(0, atSec - 2.0);
            seekArgs = $"-ss {pre.ToString(ci)} -i \"{videoPath}\" -ss {(atSec - pre).ToString(ci)}";
        }
        return RunFfmpeg($"-nostdin -y {seekArgs} -frames:v 1 \"{png}\"") && FileNonEmpty(png);
    }

    private static bool GrabSceneFrame(string output, int w, int h, string outRaw)
    {
        // Prefer LWE's own --screenshot dump (written per output at scene launch): it's the scene's
        // framebuffer, so it has NO compositor windows. Fall back to grim (which captures the live
        // composite → any open windows show through the cover) only if the dump is missing.
        string? png = null; bool grimmed = false;
        var shot = PlayerHelper.SceneShotPath(output);
        if (FileNonEmpty(shot)) png = shot;
        else
        {
            png = outRaw + ".png"; grimmed = true;
            try
            {
                var psi = new ProcessStartInfo("grim") { UseShellExecute = false };
                psi.ArgumentList.Add("-o"); psi.ArgumentList.Add(output); psi.ArgumentList.Add(png);
                using var p = Process.Start(psi);
                if (p == null) return false;
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch { } return false; }
                if (p.ExitCode != 0 || !FileNonEmpty(png)) return false;
            }
            catch { return false; }
        }
        bool ok = RunFfmpeg($"-nostdin -y -i \"{png}\" -vf {ScaleVf(w, h)} -pix_fmt rgba -f rawvideo \"{outRaw}\"") && FileNonEmpty(outRaw);
        if (grimmed) TryDelete(png);   // keep the LWE dump (reused next transition); only clean the grim temp
        return ok;
    }

    private static bool RenderScenePreview(string sceneFolder, int w, int h, string outRaw)
    {
        // incoming scene: use its library preview image (a clean representative still; avoids
        // launching linux-wallpaperengine just to grab a frame). ffmpeg decodes png/jpg/gif/webp.
        var preview = ScenePreview(sceneFolder);
        if (preview == null) return false;
        return RunFfmpeg($"-nostdin -y -i \"{preview}\" -frames:v 1 -vf {ScaleVf(w, h)} -pix_fmt rgba -f rawvideo \"{outRaw}\"")
               && FileNonEmpty(outRaw);
    }

    private static string? ScenePreview(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var key = Path.GetFileName(folder);
        foreach (var name in new[] { "preview.gif", "preview.webp", "preview.png", "preview.jpg", "preview.jpeg",
                                     key + ".gif", key + ".webp", key + ".png", key + ".jpg", key + ".jpeg" })
        {
            var p = Path.Combine(folder, name);
            if (File.Exists(p)) return p;
        }
        // fall back to any image in the folder
        foreach (var p in Directory.EnumerateFiles(folder))
        {
            var ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp") return p;
        }
        return null;
    }

    private static bool RunFfmpeg(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("ffmpeg")
            {
                Arguments = args, UseShellExecute = false,
                RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.BeginErrorReadLine(); p.BeginOutputReadLine();
            if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    // ---- shader composition + uniform defaults -------------------------------------------------
    private static string ComposeFragment(string dir, string effectId)
    {
        var wrap = File.ReadAllText(Path.Combine(dir, "wrap.frag.template"));
        var body = File.ReadAllText(Path.Combine(dir, "glsl", effectId + ".glsl"));
        return wrap.Replace("//<<BODY>>", body);
    }

    // ---- assets for the UI's live WebGL previews (served by ServerHost) -------------------------
    // The composed fragment for an effect (wrap + body) — the UI compiles the SAME source the
    // native renderer does, so a preview tile matches the live transition exactly.
    public static string? ComposedFragment(string effectId)
    {
        var dir = ResolveDir();
        if (dir == null || !EffectExists(effectId)) return null;
        try { return ComposeFragment(dir, effectId); } catch { return null; }
    }
    public static string? VertSource()
    {
        var dir = ResolveDir();
        try { return dir == null ? null : File.ReadAllText(Path.Combine(dir, "wrap.vert")); } catch { return null; }
    }
    // a representative sample frame ("a"|"b") for preview tiles
    public static string? PreviewImagePath(string which)
    {
        var dir = ResolveDir();
        if (dir == null) return null;
        var p = Path.Combine(dir, "preview", (which == "b" ? "b" : "a") + ".jpg");
        return File.Exists(p) ? p : null;
    }

    private static List<string> EffectUniforms(string effectId)
    {
        var result = new List<string>();
        var entry = Manifest().FirstOrDefault(e => e.Id == effectId);
        if (entry?.Uniforms == null) return result;
        foreach (var u in entry.Uniforms)
        {
            if (u.Default == null || u.Default.Count == 0) continue;
            result.Add("--uniform"); result.Add(u.Name); result.Add(u.Type);
            foreach (var v in u.Default) result.Add(v.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static bool EffectExists(string id) =>
        ResolveDir() is { } d && File.Exists(Path.Combine(d, "glsl", id + ".glsl"));

    // Effects that render correctly in REVEAL mode (freeze A over a live B): they sample the incoming
    // side UNMOVED via a plain mix/step, so making it transparent simply reveals the wallpaper-B
    // underneath. Warp/3D/zoom/distort effects move or blend B's pixels → they'd punch transparency,
    // so they fall back to frozen. Conservative on purpose; expandable as effects are vetted.
    private static readonly HashSet<string> RevealEffects = new(StringComparer.OrdinalIgnoreCase)
    {
        "fade", "fadegrayscale", "HSVfade", "dissolve", "BlockDissolve", "colorphase",
        "wipeLeft", "wipeRight", "wipeUp", "wipeDown", "directionalwipe",
        "circleopen", "circle", "angular", "Radial", "randomsquares", "squareswire",
        "crosshatch", "PolkaDotsCurtain", "chessboard",
    };
    public static bool IsRevealEffect(string id) => RevealEffects.Contains(id);

    // ---- manifest --------------------------------------------------------------------------------
    public sealed class Effect
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public bool DefaultOn { get; set; }
        public List<EffectUniform>? Uniforms { get; set; }
        // true → plays LIVE (B keeps playing) in Reveal mode; false → falls back to Frozen there.
        public bool Reveal => IsRevealEffect(Id);
    }
    public sealed class EffectUniform
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public List<double>? Default { get; set; }
    }

    private static List<Effect>? _manifest;
    public static List<Effect> Manifest()
    {
        if (_manifest != null) return _manifest;
        try
        {
            var dir = ResolveDir();
            if (dir == null) return _manifest = [];
            var json = File.ReadAllText(Path.Combine(dir, "manifest.json"));
            _manifest = JsonSerializer.Deserialize<List<Effect>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        }
        catch { _manifest = []; }
        return _manifest;
    }

    // ---- path resolution -------------------------------------------------------------------------
    private static string? _dir, _bin;
    public static string? ResolveDir()
    {
        if (_dir != null) return _dir.Length == 0 ? null : _dir;
        var env = Environment.GetEnvironmentVariable("LP_TRANSITIONS_DIR");
        foreach (var cand in Candidates(env, installed: "transitions", repoTail: "transitions"))
            if (cand != null && File.Exists(Path.Combine(cand, "manifest.json"))) { _dir = cand; return cand; }
        _dir = ""; return null;
    }
    private static string? ResolveBinary()
    {
        if (_bin != null) return _bin.Length == 0 ? null : _bin;
        var env = Environment.GetEnvironmentVariable("LP_TRANSITION_BIN");
        if (env != null && File.Exists(env)) { _bin = env; return env; }
        // installed on PATH (~/.local/bin) or the repo build
        foreach (var cand in new[] {
            PathLookup("lp-transition"),
            RepoPath("src/native/lp-transition/lp-transition") })
            if (cand != null && File.Exists(cand)) { _bin = cand; return cand; }
        _bin = ""; return null;
    }

    private static IEnumerable<string?> Candidates(string? env, string installed, string repoTail)
    {
        yield return env;
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "livepaper-web", installed);
        yield return RepoPath(repoTail);
    }

    // walk up from the running assembly's dir looking for {tail}
    private static string? RepoPath(string tail)
    {
        var d = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && d != null; i++)
        {
            var cand = Path.Combine(d, tail);
            if (File.Exists(cand) || Directory.Exists(cand)) return cand;
            d = Path.GetDirectoryName(d.TrimEnd('/'));
        }
        return null;
    }

    private static string? PathLookup(string exe)
    {
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            if (p.Length > 0 && File.Exists(Path.Combine(p, exe))) return Path.Combine(p, exe);
        return null;
    }

    private static string RuntimeDir() => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "livepaper");
    private static string IpcSocket() => Path.Combine(RuntimeDir(), "mpv.sock");

    // ---- mpv IPC (for the frozen/reveal exact-frame grab) --------------------------------------
    private static bool MpvCmd(string json)
    {
        try
        {
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified) { SendTimeout = 500 };
            s.Connect(new System.Net.Sockets.UnixDomainSocketEndPoint(IpcSocket()));
            s.Send(System.Text.Encoding.UTF8.GetBytes(json + "\n"));
            return true;
        }
        catch { return false; }
    }
    // Dump mpvpaper's CURRENT displayed frame (the EXACT last frame, no re-decode, no seek error).
    private static bool MpvScreenshot(string png) =>
        MpvCmd(JsonSerializer.Serialize(new { command = new object[] { "screenshot-to-file", png, "video" } }));
    private static void MpvSetPause(bool p) =>
        MpvCmd(JsonSerializer.Serialize(new { command = new object[] { "set_property", "pause", p } }));

    private static bool ScalePngToRaw(string png, int w, int h, string outRaw) =>
        RunFfmpeg($"-nostdin -y -i \"{png}\" -vf {ScaleVf(w, h)} -pix_fmt rgba -f rawvideo \"{outRaw}\"") && FileNonEmpty(outRaw);

    // ---- misc ------------------------------------------------------------------------------------
    // append to the same diagnostic log the renderer writes (lp-transition.log), tagged [backend]
    private static readonly string _logPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "livepaper", "lp-transition.log");
    public static void TransLog(string msg) { try { File.AppendAllText(_logPath, $"[backend {DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { } }

    private static bool FileNonEmpty(string p) { try { return new FileInfo(p).Length > 0; } catch { return false; } }
    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
    private static void TryDeleteDir(string p) { try { Directory.Delete(p, true); } catch { } }
}
