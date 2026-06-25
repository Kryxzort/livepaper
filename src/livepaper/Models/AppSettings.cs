using System;
using System.Collections.Generic;
using System.Linq;

namespace livepaper.Models;

public class AppSettings
{
    public bool Loop { get; set; } = true;
    public bool NoAudio { get; set; } = true;
    public bool DisableCache { get; set; } = false;
    public int DemuxerMaxBytes { get; set; } = 20;
    public int DemuxerMaxBackBytes { get; set; } = 5;
    public string HwDec { get; set; } = "auto";
    public string VideoScale { get; set; } = "fill";
    public int VideoFps { get; set; } = 0; // cap mpv video playback fps (0 = native). Scenes (LWE) unaffected.
    private int _volume = 100;
    public int Volume
    {
        get => _volume;
        set => _volume = Math.Clamp(value, 0, 100);
    }
    private double _speed = 1.0;
    public double Speed
    {
        get => _speed;
        set => _speed = Math.Clamp(value, 0.1, 4.0);
    }
    public string WallpaperEnginePath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/workshop/content/431960");
    public bool WeCopyFiles { get; set; } = false;
    public bool ResumeFromLast { get; set; } = true;
    public bool AllowScenes { get; set; } = false;
    public bool LweSilent { get; set; } = false;
    public int LweVolume { get; set; } = 100;
    public List<LweMonitorSettings> LweMonitors { get; set; } = [];
    public int SceneTransitionDelayMs { get; set; } = 1000;
    public bool AutoMute { get; set; } = false;
    public int AutoMuteDelayMs { get; set; } = 200;
    public int AutoUnmuteDelayMs { get; set; } = 2000;
    public double AutoMuteThresholdDb { get; set; } = -70.0;
    public bool AutoMuteOnlyIfMprisActive { get; set; } = false;
    public string ThumbnailAspect { get; set; } = "1:1";
    public string CardSize { get; set; } = "Medium";
    public bool AutoPlayGifs { get; set; } = false;
    public bool AdvancedSettings { get; set; } = false;      // UI: reveal power-user settings rows
    public bool WallpaperBgAllTabs { get; set; } = false;    // UI: play the wallpaper behind Browse/Library too
    public bool DebugMode { get; set; } = false;             // UI: enable the lpdbg bridge + metrics
    public bool DebugOverlay { get; set; } = true;           // UI: show the on-screen debug HUD (when DebugMode on)
    public bool RestartOnSwitchOnly { get; set; } = false;   // defer the mpvpaper leak-restart to the next playlist changeover
    public bool ReplaceDirectWithWorkshop { get; set; } = false; // swap a direct-DL copy for a WE-dir symlink/copy when it appears
    public int LibrarySortIndex { get; set; } = 5;
    public int GlobalIntervalSeconds { get; set; } = 1800;
    public bool GlobalAdvanceOnVideoEnd { get; set; } = true;
    public string Theme { get; set; } = "Catppuccin Mocha";
    private int _restartIntervalSeconds = 600;
    public int RestartIntervalSeconds
    {
        get => _restartIntervalSeconds;
        set => _restartIntervalSeconds = Math.Clamp(value, 0, 3600);
    }
    public bool GlobalWaitForVideoEnd { get; set; } = false;
    public bool PlaylistWaitForVideoEnd { get; set; } = false;

    // Global transition fallbacks (used when a playlist's OverrideGlobalSettings is off). See
    // PlaylistSettings.Transition* + TransitionService. Empty/!Enabled => instant cut.
    public bool GlobalTransitionEnabled { get; set; } = false;
    public List<string> GlobalTransitionEffectIds { get; set; } = [];
    public int GlobalTransitionDurationMs { get; set; } = 600;
    public int GlobalTransitionDurationMaxMs { get; set; } = 0;
    public bool GlobalTransitionShuffle { get; set; } = true;
    public bool AutoAddLibraryToPlaylist { get; set; } = false;
    public bool AutoImportWallpaperEngine { get; set; } = false;
    public bool IsPlaylistCollapsed { get; set; } = false;
    // "subscribe" (real Steam subscription via community endpoint) or "steamcmd" (direct download).
    public string WorkshopAcquireMode { get; set; } = "subscribe";
    // QR sign-in: refresh token (~1yr) + steamid; access token (~24h) cached + auto-minted.
    public string SteamRefreshToken { get; set; } = "";
    public ulong SteamId { get; set; } = 0;
    public string SteamAccessToken { get; set; } = "";
    public string SteamAccountName { get; set; } = "";
    // Manual cookie paste (fallback / advanced; used only when no refresh token is stored).
    public string SteamLoginSecure { get; set; } = "";
    public string SteamCmdPath { get; set; } = "";
    public string SteamUsername { get; set; } = "";
    public LastSession? LastSession { get; set; }

    public string BuildMpvOptions()
    {
        var parts = new List<string>();
        if (Loop) parts.Add("loop");
        if (NoAudio) parts.Add("--no-audio");
        else parts.Add($"--volume={Volume}");
        if (Speed != 1.0) parts.Add($"--speed={Speed.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        if (DisableCache) parts.Add("--cache=no");
        if (DemuxerMaxBytes > 0) parts.Add($"--demuxer-max-bytes={DemuxerMaxBytes}MiB");
        if (DemuxerMaxBackBytes > 0) parts.Add($"--demuxer-max-back-bytes={DemuxerMaxBackBytes}MiB");
        if (!string.IsNullOrWhiteSpace(HwDec)) parts.Add($"--hwdec={HwDec}");
        if (VideoFps > 0) parts.Add($"--vf=fps={VideoFps}"); // cap playback fps (video only)
        if (VideoScale == "fill") parts.Add("--panscan=1.0");
        parts.Add("--image-display-duration=inf");
        return string.Join(" ", parts);
    }

    // For playlist mode: omit per-file loop so mpv advances to the next entry.
    public string BuildMpvPlaylistOptions()
    {
        var parts = new List<string>();
        if (NoAudio) parts.Add("--no-audio");
        else parts.Add($"--volume={Volume}");
        if (Speed != 1.0) parts.Add($"--speed={Speed.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}");
        if (DisableCache) parts.Add("--cache=no");
        if (DemuxerMaxBytes > 0) parts.Add($"--demuxer-max-bytes={DemuxerMaxBytes}MiB");
        if (DemuxerMaxBackBytes > 0) parts.Add($"--demuxer-max-back-bytes={DemuxerMaxBackBytes}MiB");
        if (!string.IsNullOrWhiteSpace(HwDec)) parts.Add($"--hwdec={HwDec}");
        if (VideoFps > 0) parts.Add($"--vf=fps={VideoFps}"); // cap playback fps (video only)
        if (VideoScale == "fill") parts.Add("--panscan=1.0");
        parts.Add("--image-display-duration=10");
        return string.Join(" ", parts);
    }

    // Live preview of the linux-wallpaperengine (scene) command, one line per configured monitor.
    // Mirrors PlayerHelper.SpawnLweProcesses exactly. Uses a <monitor> placeholder when none set.
    public string BuildLweOptions()
    {
        var monitors = LweMonitors.Where(m => !string.IsNullOrWhiteSpace(m.Name)).ToList();
        bool anyPrimary = monitors.Any(m => m.IsPrimary);
        var src = monitors.Count > 0
            ? monitors
            : new List<LweMonitorSettings> { new() { Name = "<monitor>", Fps = 60, IsPrimary = true } };
        var lines = new List<string>();
        foreach (var m in src)
        {
            var parts = new List<string> { "linux-wallpaperengine", "--noautomute", "--screen-root", m.Name };
            bool hasAudio = !anyPrimary || m.IsPrimary;
            if (NoAudio || !hasAudio) parts.Add("--silent");
            else { parts.Add("--volume"); parts.Add("100"); }
            if (m.Fps > 0) { parts.Add("--fps"); parts.Add(m.Fps.ToString()); }
            parts.Add("--no-fullscreen-pause");
            lines.Add(string.Join(" ", parts));
        }
        return string.Join("\n", lines);
    }

    public static AppSettings Default() => new();
}
