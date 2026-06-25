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

    public static int PickDuration(Config c)
    {
        int min = Math.Clamp(c.DurationMs, 50, 10000);
        if (c.DurationMaxMs > min) return _rng.Next(min, Math.Clamp(c.DurationMaxMs, min, 10000) + 1);
        return min;
    }

    // ---- the entry SwitchToFile calls ----------------------------------------------------------
    // Captures both frames per output (warmup/scene fallback), composes the shader, and spawns
    // lp-transition detached. FULL-LIVE: for a video side it also hands the renderer the actual file
    // (`--from-video`/`--to-video` + start offset) so the renderer decodes it with libmpv and BOTH
    // sides keep PLAYING through the effect. The incoming video B is played LIVE underneath the
    // opaque overlay (the caller loads it unpaused) and is revealed at teardown at a matching position.
    // Returns true if the overlay was started (caller hides its switch under it); false => instant cut.
    public static bool TryStart(string? fromPath, bool fromScene, string toPath, bool toScene, Config cfg)
    {
        try
        {
            if (!cfg.Enabled || string.IsNullOrEmpty(fromPath)) return false;
            var dir = ResolveDir(); var bin = ResolveBinary();
            if (dir == null || bin == null) return false;
            var effect = PickEffect(cfg);
            if (effect == null) return false;
            int durationMs = PickDuration(cfg);

            var monitors = MonitorDetector.DetectAsync().GetAwaiter().GetResult();
            if (monitors.Count == 0) return false;

            // per-transition scratch dir (cleaned up after the run)
            string work = Path.Combine(RuntimeDir(), "transition", DateTime.UtcNow.Ticks.ToString());
            Directory.CreateDirectory(work);

            string frag = Path.Combine(work, "effect.frag");
            File.WriteAllText(frag, ComposeFragment(dir, effect));
            string vert = Path.Combine(dir, "wrap.vert");

            double? timePos = fromScene ? null : PlayerHelper.QueryTimePos();
            double epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0; // wall clock at the A-position sample
            double? fromDur = fromScene ? null : PlayerHelper.QueryDuration();      // wrap A's advanced start (loops)
            var aset = SettingsService.Load();
            int audioVol = (aset.NoAudio || PlayerHelper.IsMuted) ? 0 : aset.Volume; // crossfade target (0 = silent, matches a muted wallpaper)
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            var args = new List<string> { "--duration-ms", durationMs.ToString(), "--vert", vert, "--frag", frag };
            foreach (var u in EffectUniforms(effect)) args.Add(u);
            // full-live: hand the renderer the actual videos so libmpv decodes BOTH sides live (the
            // per-output raws below stay as the warmup fallback / the only source for a scene side).
            // A resumes from its current playback position; B starts at 0 (it plays live underneath
            // and is revealed at teardown at a matching position — no pause/unpause handoff needed).
            // A resumes from its live position. --from-epoch lets the renderer add the wall-clock
            // elapsed since this sample, so the overlay's A lands exactly on mpvpaper-A's current
            // frame at cover (no backward jump at the start).
            if (!fromScene) {
                args.Add("--from-video"); args.Add(fromPath!);
                args.Add("--from-start"); args.Add((timePos ?? 0).ToString(inv));
                args.Add("--from-epoch"); args.Add(epoch.ToString(inv));
                if (fromDur is > 0) { args.Add("--from-duration"); args.Add(fromDur.Value.ToString(inv)); }
            }
            // B: the overlay decodes it live (held until first paint via --to-paused). mpvpaper loads
            // B PAUSED at frame 0 underneath (see PlayerHelper); at teardown the renderer seeks that B
            // to the overlay's exact position and unpauses (--mpv-unpause) → frame-accurate reveal.
            if (!toScene) { args.Add("--to-video"); args.Add(toPath); args.Add("--to-start"); args.Add("0"); args.Add("--to-paused"); args.Add("--mpv-unpause"); args.Add(IpcSocket()); }
            // audio: the overlay (one output) carries the crossfaded A→B sound at the wallpaper's volume
            // while mpvpaper is paused; 0 when muted → silent (matches the desktop).
            args.Add("--audio-volume"); args.Add(audioVol.ToString());
            string readyFile = Path.Combine(work, "ready");
            args.Add("--ready-file"); args.Add(readyFile);

            int outputs = 0;
            foreach (var m in monitors)
            {
                int w = m.Width > 0 ? m.Width : 1920, h = m.Height > 0 ? m.Height : 1080;
                string fromRaw = Path.Combine(work, $"{m.Name}.from.raw");
                string toRaw = Path.Combine(work, $"{m.Name}.to.raw");
                bool okFrom = fromScene
                    ? GrabSceneFrame(m.Name, w, h, fromRaw)
                    : ExtractVideoFrame(fromPath!, timePos ?? 0, w, h, fromRaw);
                bool okTo = toScene
                    ? RenderScenePreview(toPath, w, h, toRaw)
                    : ExtractVideoFrame(toPath, 0, w, h, toRaw);
                if (!okFrom || !okTo) continue;
                args.Add("--output"); args.Add(m.Name);
                args.Add("--from"); args.Add(fromRaw);
                args.Add("--to"); args.Add(toRaw);
                args.Add("--width"); args.Add(w.ToString());
                args.Add("--height"); args.Add(h.ToString());
                outputs++;
            }
            if (outputs == 0) { TryDeleteDir(work); return false; }

            var psi = new ProcessStartInfo(bin) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var proc = Process.Start(psi);
            if (proc == null) { TryDeleteDir(work); return false; }

            // Block until the overlay covers the screen with live frames (the renderer touches
            // readyFile only once EVERY output has a live A frame; until then it paints transparent
            // and the live wallpaper shows through). Only then does the caller switch mpvpaper to B,
            // so B never flashes during the overlay's libmpv warm-up and the teardown positions match.
            // Capped (~3.5s) so a renderer that never paints can't hang the switch.
            for (int i = 0; i < 440 && !File.Exists(readyFile) && !proc.HasExited; i++)
                System.Threading.Thread.Sleep(8);

            // best-effort cleanup of the scratch dir once the overlay is done (it uploads the raws
            // to GL textures at startup, so deleting after the run is safe).
            int grace = durationMs + 4000;
            _ = System.Threading.Tasks.Task.Run(async () => { await System.Threading.Tasks.Task.Delay(grace); TryDeleteDir(work); });
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[transition] start failed: {e.Message}");
            return false;
        }
    }

    // ---- frame capture -------------------------------------------------------------------------
    private static bool ExtractVideoFrame(string videoPath, double atSec, int w, int h, string outRaw)
    {
        if (!File.Exists(videoPath)) return false;
        return RunFfmpeg($"-nostdin -y -ss {atSec.ToString(System.Globalization.CultureInfo.InvariantCulture)} -i \"{videoPath}\" " +
                         $"-frames:v 1 -vf scale={w}:{h} -pix_fmt rgba -f rawvideo \"{outRaw}\"")
               && FileNonEmpty(outRaw);
    }

    private static bool GrabSceneFrame(string output, int w, int h, string outRaw)
    {
        // grim the live composited output (the scene is animated; this is its current frame). Any
        // app windows in the grab are hidden behind the real windows that stay on top of the BOTTOM
        // overlay, so they're never seen for in-place effects.
        string png = outRaw + ".png";
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
        bool ok = RunFfmpeg($"-nostdin -y -i \"{png}\" -vf scale={w}:{h} -pix_fmt rgba -f rawvideo \"{outRaw}\"") && FileNonEmpty(outRaw);
        TryDelete(png);
        return ok;
    }

    private static bool RenderScenePreview(string sceneFolder, int w, int h, string outRaw)
    {
        // incoming scene: use its library preview image (a clean representative still; avoids
        // launching linux-wallpaperengine just to grab a frame). ffmpeg decodes png/jpg/gif/webp.
        var preview = ScenePreview(sceneFolder);
        if (preview == null) return false;
        return RunFfmpeg($"-nostdin -y -i \"{preview}\" -frames:v 1 -vf scale={w}:{h} -pix_fmt rgba -f rawvideo \"{outRaw}\"")
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

    // ---- manifest --------------------------------------------------------------------------------
    public sealed class Effect
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public bool DefaultOn { get; set; }
        public List<EffectUniform>? Uniforms { get; set; }
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

    // ---- misc ------------------------------------------------------------------------------------
    private static bool FileNonEmpty(string p) { try { return new FileInfo(p).Length > 0; } catch { return false; } }
    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }
    private static void TryDeleteDir(string p) { try { Directory.Delete(p, true); } catch { } }
}
