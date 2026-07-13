using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Models;

namespace livepaper.Helpers;

public static class PlayerHelper
{
    private static Process? _current;
    private static Timer? _playlistTimer;
    private static Timer? _restartTimer;
    // RestartOnSwitchOnly: a leak-restart is due but deferred to the next wallpaper switch (so the
    // relaunch rides the natural changeover instead of a jarring mid-video restart). Lives in the tick
    // owner's memory; set when it consumes a "soft-restart" pending action, consumed by the next SwitchToFile.
    private static bool _restartPending;
    private static bool _daemonMode = false;
    private static List<string>? _timedPaths;
    private static int _timedIndex;
    private static string _timedOptions = "";
    private static bool _timedShuffle;
    private static TimeSpan _timedInterval;
    private static List<string>? _history;
    private static int _historyIndex = -1;
    private static bool _timedTimerPaused;
    private static bool _timedTimerStopped;
    private static long _timedRemainingMs;
    private static DateTime _lastTickTime;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static CancellationTokenSource? _observerCts;
    private static List<string> _currentObserverPaths = [];
    private static readonly object _lock = new();
    private static CancellationTokenSource? _daemonCts;
    private static bool _waitForVideoEnd;
    private static bool _advanceOnVideoEnd;
    private static bool _waitingForVideoEnd;
    private static double _currentSpeed = 1.0;
    private static CancellationTokenSource? _waitCts;
    private static CancellationTokenSource? _prelaunchCts;
    private static string[]? _prelaunchPidsToKill;
    private static bool _isMuted;
    private static bool _autoMuted;
    private static bool _userMuted;
    private static volatile TaskCompletionSource<bool>? _speedChangeTcs;
    public static CancellationToken DaemonToken => _daemonCts?.Token ?? CancellationToken.None;

    public static bool IsPlaying =>
        (File.Exists(IpcSocket) && MpvpaperProcs().Length > 0) ||
        IsLweRunning;

    private static bool IsLweRunning
    {
        get
        {
            try
            {
                if (!File.Exists(LwePidPath)) return false;
                return File.ReadAllLines(LwePidPath).Any(line =>
                {
                    if (!int.TryParse(line.Trim(), out int pid)) return false;
                    try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
                    catch { return false; }
                });
            }
            catch { return false; }
        }
    }

    public static bool IsTimedModeActive { get { lock (_lock) { return _timedPaths != null; } } }

    // When _waitingForVideoEnd is true, _historyIndex points to the pre-fetched next item.
    // This returns the index of the item that is actually playing.
    private static int PlayingHistoryIndex =>
        (_waitingForVideoEnd && _historyIndex > 0) ? _historyIndex - 1 : _historyIndex;

    public static void AppendToActivePlaylist(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        lock (_lock)
        {
            if (_timedPaths != null)
            {
                var existing = new HashSet<string>(_timedPaths, StringComparer.Ordinal);
                foreach (var p in paths)
                    if (existing.Add(p)) _timedPaths.Add(p);
            }
            else if (IsPlaying)
            {
                foreach (var p in paths)
                    TrySendCommand("loadfile", p, "append");
            }
        }
    }
    public static bool IsUserMuted => _userMuted;
    public static bool IsMuted => _isMuted;

    // Stale-tolerant check that survives the brief gap during a timed-playlist
    // switch where mpvpaper has been killed but the next instance hasn't launched.
    public static bool IsTimedPlaylistActive()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            return state != null && state.Paths.Count > 0 && !state.TimerStopped;
        }
        catch { return false; }
    }

    private static bool IsTimedPlaylistPaused()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            return state?.TimerPaused ?? false;
        }
        catch { return false; }
    }

    // Daemon-side (no in-memory history): read the timed-state file for the item count + current path,
    // so RunRestartDaemon can apply the same scene-skip / defer-to-switch logic as the in-process timer.
    private static (int Count, string? Current) ReadTimedSnapshot()
    {
        try
        {
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return (0, null);
            int hi = state.WaitingForVideoEnd && state.HistoryIndex > 0 ? state.HistoryIndex - 1 : state.HistoryIndex;
            string? cur = state.History != null && hi >= 0 && hi < state.History.Count ? state.History[hi] : null;
            return (state.Paths.Count, cur);
        }
        catch { return (0, null); }
    }

    private static string IpcSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "mpv.sock");

    // Control socket for a running full-live transition overlay (lp-transition binds it). Lets a keybind
    // during the effect reach the overlay's OWN audio (it decodes A/B itself; mpvpaper isn't audible then).
    private static string OverlayCtlSocket => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(),
        "livepaper", "lp-transition.ctl");

    // Best-effort live command (mute/volume) to a transition overlay if one is listening (DGRAM, no-op
    // otherwise). So toggle-mute / volume± mid-effect take hold immediately instead of post-teardown.
    private static void SendOverlayCtl(string cmd)
    {
        try
        {
            if (!File.Exists(OverlayCtlSocket)) return;
            using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
                System.Net.Sockets.SocketType.Dgram, System.Net.Sockets.ProtocolType.Unspecified);
            s.SendTo(System.Text.Encoding.UTF8.GetBytes(cmd), new System.Net.Sockets.UnixDomainSocketEndPoint(OverlayCtlSocket));
        }
        catch { }
    }

    // ── lp-audio: libpulse helper for gapless scene-audio crossfades ───────────────────────────────
    // A persistent process holding a PulseAudio context that applies per-PID sink-input volume/mute
    // IN-PROCESS on every stream new/change event — so a stream PA (re)creates at the wrong volume
    // (default-on-create / LWE connect-disconnect reset) is corrected in ~1ms, which shelling `pactl`
    // (~10-40ms) can't. Driven over stdin: "set <pidcsv> <vol> <mute>", "clear", "quit".
    private static Process? _lpAudio;
    private static readonly object _lpAudioLock = new();

    private static string? ResolveLpAudioBin()
    {
        var env = Environment.GetEnvironmentVariable("LP_AUDIO_BIN");
        if (env != null && File.Exists(env)) return env;
        foreach (var c in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "lp-audio"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "native", "lp-audio", "lp-audio"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "native", "lp-audio", "lp-audio") })
            try { var f = Path.GetFullPath(c); if (File.Exists(f)) return f; } catch { }
        return null;
    }

    // Send one command to the persistent lp-audio (spawned lazily, respawned if dead). No-op if the
    // helper isn't built (→ scene audio just isn't crossfaded, falls back to the plain switch).
    private static void AudioCtl(string line)
    {
        try
        {
            lock (_lpAudioLock)
            {
                if (_lpAudio == null || _lpAudio.HasExited)
                {
                    var bin = ResolveLpAudioBin();
                    if (bin == null) { TransitionService.TransLog($"AudioCtl: NO lp-audio binary found"); return; }
                    var psi = new ProcessStartInfo(bin) { UseShellExecute = false, RedirectStandardInput = true };
                    _lpAudio = Process.Start(psi);
                }
                _lpAudio?.StandardInput.WriteLine(line);
                _lpAudio?.StandardInput.Flush();
            }
        }
        catch { try { lock (_lpAudioLock) { _lpAudio = null; } } catch { } }
    }
    private static string PidCsv(IEnumerable<int> pids) => string.Join(",", pids);

    // PulseAudio stream-restore continuously saves the linux-wallpaperengine app volume, so a transient
    // crossfade value (e.g. 25% mid-fade, or 0% at fade-out) gets remembered and poisons the NEXT fresh
    // scene launch (first playlist item / --restore / --random) — which has no crossfade to set the right
    // level → it comes up silent/quiet. LWE forces its own application.name and pipewire ignores
    // module-stream-restore.id, so we can't change the restore key. Instead, correct reactively: hold the
    // just-launched scene at `target` via lp-audio (event-driven → overrides the restored value the instant
    // the stream appears) for ~holdMs, then release so manual volume control governs the steady state.
    private static void CorrectSceneVolume(HashSet<int> beforePids, int target, bool muted, int holdMs)
    {
        _ = Task.Run(async () =>
        {
            int m = muted ? 1 : 0, steps = Math.Max(4, holdMs / 250);
            AudioCtl("clear");
            for (int k = 0; k < steps; k++)
            {
                var np = GetLweProcPids(); np.ExceptWith(beforePids);   // the newly-launched scene's pids
                if (np.Count > 0) AudioCtl($"set {PidCsv(np)} {target} {m}");
                try { await Task.Delay(250); } catch { break; }
            }
            AudioCtl("clear");
        });
    }

    private static string TimedStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timed_state.json");

    private static string PlaylistObserverPathsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "livepaper", "playlist_observer_paths.json");

    private static string TimerDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "timer.pid");

    private static string RestartDaemonPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "restart.pid");

    private static string LwePidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "lwe.pid");

    private static string GuiTimerPidPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "gui_timer.pid");

    private static string UserMuteStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "user_mute.state");

    private static string PendingActionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "livepaper", "pending_action.txt");

    public static void KillTimerDaemon()
    {
        try
        {
            if (!File.Exists(TimerDaemonPidPath)) return;
            var pidText = File.ReadAllText(TimerDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(TimerDaemonPidPath);
        }
        catch { }
    }

    public static void WriteGuiTimerPid()
    {
        try
        {
            var path = GuiTimerPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void ClearGuiTimerPid()
    {
        try { File.Delete(GuiTimerPidPath); } catch { }
    }

    private static bool IsGuiTimerAlive()
    {
        try
        {
            if (!File.Exists(GuiTimerPidPath)) return false;
            var pidText = File.ReadAllText(GuiTimerPidPath).Trim();
            if (!int.TryParse(pidText, out int pid)) return false;
            using var _ = System.Diagnostics.Process.GetProcessById(pid);
            return true;
        }
        catch { return false; }
    }

    public static void SpawnTimerDaemon()
    {
        // Defensive guard: if a GUI is alive it owns the in-process timer.
        // Spawning a daemon would create two competing owners of mpvpaper.
        if (IsGuiTimerAlive()) return;

        FlushTimedState(); // persist current remaining time before handing off
        KillTimerDaemon();
        try
        {
            var selfArgs = GetSelfInvocationArgs();
            if (selfArgs.Count == 0) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in selfArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--timer-daemon");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
    }

    // Build the argv prefix needed to re-invoke this same livepaper process
    // (without args).
    //   - Self-contained (AppImage, install.sh): the apphost binary and
    //     entry assembly share a name (livepaper / livepaper.dll). The
    //     apphost runs its bundled dll automatically; we just spawn it.
    //   - Framework-dependent (AUR PKGBUILD): the process executable is the
    //     dotnet host. We need to pass the entry assembly path so the
    //     spawned host knows what to run.
    public static List<string> GetSelfInvocationArgs()
    {
        var args = new List<string>();
        var processPath = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(processPath)) return args;
        args.Add(processPath);

        var entryAsm = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrEmpty(entryAsm) || !entryAsm.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return args;

        // Apphost vs dotnet host: in a self-contained build the apphost is
        // named after the assembly (livepaper ↔ livepaper.dll), so the names
        // match and we don't need a separate dll arg. Framework-dependent
        // builds run under `dotnet` whose name differs.
        var procStem = Path.GetFileNameWithoutExtension(processPath);
        var asmStem = Path.GetFileNameWithoutExtension(entryAsm);
        if (!string.Equals(procStem, asmStem, StringComparison.OrdinalIgnoreCase))
            args.Add(entryAsm);

        return args;
    }

    public static void WriteTimerDaemonPid()
    {
        try
        {
            var path = TimerDaemonPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    public static void DeleteTimerDaemonPid()
    {
        try { File.Delete(TimerDaemonPidPath); } catch { }
    }

    public static void KillRestartDaemon()
    {
        try
        {
            if (!File.Exists(RestartDaemonPidPath)) return;
            var pidText = File.ReadAllText(RestartDaemonPidPath).Trim();
            if (int.TryParse(pidText, out int pid))
            {
                try { System.Diagnostics.Process.GetProcessById(pid).Kill(); } catch { }
            }
            File.Delete(RestartDaemonPidPath);
        }
        catch { }
    }

    private static void WriteRestartDaemonPid()
    {
        try
        {
            var path = RestartDaemonPidPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Environment.ProcessId.ToString());
        }
        catch { }
    }

    private static void DeleteRestartDaemonPid()
    {
        try { File.Delete(RestartDaemonPidPath); } catch { }
    }

    public static void SpawnRestartDaemon()
    {
        // Mirror the timer-daemon guard: if the GUI is alive it owns the
        // in-process restart timer, so don't spawn a competing daemon.
        if (IsGuiTimerAlive()) return;
        if (SettingsService.Load().RestartIntervalSeconds <= 0) return;

        KillRestartDaemon();
        try
        {
            var selfArgs = GetSelfInvocationArgs();
            if (selfArgs.Count == 0) return;
            var psi = new System.Diagnostics.ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in selfArgs) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("--restart-daemon");
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc != null)
            {
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
            }
        }
        catch { }
    }

    // Start or restart the in-process restart timer based on current settings.
    // No-op in daemon processes (they use their own loop or pending actions).
    public static void UpdateRestartTimer()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        if (_daemonMode) return;
        var settings = SettingsService.Load();
        if (settings.RestartIntervalSeconds <= 0) return;
        int intervalMs = settings.RestartIntervalSeconds * 1000;
        _restartTimer = new Timer(_ => RestartCurrent(), null,
            TimeSpan.FromMilliseconds(intervalMs),
            TimeSpan.FromMilliseconds(intervalMs));
    }

    private static void StopRestartTimer()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
    }

    // In-process restart: re-launch the current wallpaper cold to free memory.
    // For timed playlist sessions, delegates via pending action so the tick
    // owner (which holds the in-memory path list) handles it.
    private static void RestartCurrent()
    {
        var settings = SettingsService.Load();
        lock (_lock)
        {
            if (_timedPaths != null && !_timedTimerStopped)
            {
                if (_timedTimerPaused) return;
                // Scene playing → mpvpaper isn't running; the restart is moot. Skip (this is what "zeroes"
                // the restart timer while a scene plays — firing does nothing).
                var cur = _history != null && PlayingHistoryIndex >= 0 && PlayingHistoryIndex < _history.Count
                    ? _history[PlayingHistoryIndex] : null;
                if (cur != null && IsScenePath(cur)) return;
                // RestartOnSwitchOnly + a real playlist (a switch is coming) → defer: the next changeover
                // becomes the relaunch. Otherwise (toggle off, or playlist-of-1 = no switch) → jarring restart now.
                if (settings.RestartOnSwitchOnly && _timedPaths.Count > 1)
                    WritePendingAction("soft-restart");
                else
                    WritePendingAction("restart");
                return;
            }
            // Guard against the race where Stop() runs while this callback
            // was waiting on the lock: don't relaunch if nothing is playing.
            if (!IsPlaying) return;
            var session = settings.LastSession;
            if (session == null || session.Paths.Count == 0) return;
            DoColdRestart(session, settings); // lone video → default (jarring) restart
        }
    }

    // Restart without advancing: used by the timed playlist tick when it
    // consumes a "restart" pending action.
    private static void RestartCurrentAndLaunch()
    {
        if (_history == null || _historyIndex < 0 || _historyIndex >= _history.Count) return;
        var current = _history[_historyIndex];
        KillCurrentProcess();
        _current = Launch(_timedOptions, current);
        // deliberately does not touch _timedRemainingMs — the countdown continues unaffected
        SaveTimedState();
    }

    // Kill and cold-start mpvpaper with the given session. Must be called under _lock.
    private static void DoColdRestart(LastSession session, AppSettings settings)
    {
        if (session.IsPlaylist && session.Paths.Count > 1)
        {
            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "livepaper");
            Directory.CreateDirectory(cacheDir);
            var playlistPath = Path.Combine(cacheDir, "playlist.txt");
            File.WriteAllLines(playlistPath, session.Paths.Take(session.Paths.Count - 1));
            var shuffleFlag = session.Shuffle ? " --shuffle" : "";
            var options = $"{settings.BuildMpvPlaylistOptions()} --playlist={playlistPath} --loop-playlist=inf{shuffleFlag}";
            KillCurrentProcess();
            _current = Launch(options, session.Paths[session.Paths.Count - 1]);
        }
        else
        {
            KillCurrentProcess();
            _current = Launch(settings.BuildMpvOptions(), session.Paths[0]);
        }
    }

    // Entry point for the --restart-daemon CLI flag. Blocks indefinitely,
    // restarting mpvpaper every RestartIntervalSeconds. Killed when the GUI opens.
    public static void RunRestartDaemon()
    {
        _daemonMode = true;
        WriteRestartDaemonPid();
        try
        {
            while (true)
            {
                var settings = SettingsService.Load();
                if (settings.RestartIntervalSeconds <= 0) return;
                int intervalMs = settings.RestartIntervalSeconds * 1000;
                Thread.Sleep(intervalMs);
                settings = SettingsService.Load();
                if (IsTimedPlaylistActive() && !IsTimedPlaylistPaused())
                {
                    var (count, cur) = ReadTimedSnapshot();
                    if (cur != null && IsScenePath(cur)) { /* scene → skip (zeroes the restart timer) */ }
                    else if (settings.RestartOnSwitchOnly && count > 1) WritePendingAction("soft-restart");
                    else WritePendingAction("restart");
                }
                else if (IsPlaying)
                {
                    var session = settings.LastSession;
                    if (session != null && session.Paths.Count > 0)
                        lock (_lock) { DoColdRestart(session, settings); }
                }
            }
        }
        finally
        {
            DeleteRestartDaemonPid();
        }
    }

    public static void FlushTimedState()
    {
        lock (_lock) { SaveTimedState(); }
    }

    public static Action? OnTimedPlaylistStopped;
    public static Action<string>? OnSceneCrashed;
    public static Action<string?>? OnWallpaperChanged;

    private record TimedState(
        List<string> Paths, int Index,
        string Options, bool Shuffle, int IntervalSeconds,
        List<string> History, int HistoryIndex,
        bool TimerPaused = false, bool TimerStopped = false, long RemainingMs = 0,
        bool WaitForVideoEnd = false, bool AdvanceOnVideoEnd = false, bool WaitingForVideoEnd = false);

    private static void SaveTimedState()
    {
        if (_timedPaths == null || _history == null) return;
        try
        {
            var state = new TimedState(
                _timedPaths, _timedIndex,
                _timedOptions, _timedShuffle, (int)_timedInterval.TotalSeconds,
                _history, _historyIndex,
                _timedTimerPaused, _timedTimerStopped, _timedRemainingMs,
                _waitForVideoEnd, _advanceOnVideoEnd, _waitingForVideoEnd);
            var path = TimedStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    // Lighter than LoadTimedState: only syncs flags that external mutators
    // can set (TimerStopped/TimerPaused). The owner's _timedRemainingMs and
    // history stay authoritative in-memory, so we don't need to save state
    // every tick.
    private static void RefreshSignals()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null) return;
            _timedTimerStopped = state.TimerStopped;
            _timedTimerPaused = state.TimerPaused;
        }
        catch { }
        // Sync user mute state from file so CLI toggle-mute updates reach the running daemon.
        try
        {
            bool fileMuted = File.Exists(UserMuteStatePath);
            if (fileMuted != _userMuted)
            {
                _userMuted = fileMuted;
                _isMuted = fileMuted || _autoMuted;
            }
        }
        catch { }
    }

    public static void LoadUserMuteState()
    {
        try { _userMuted = File.Exists(UserMuteStatePath); _isMuted = _userMuted || _autoMuted; }
        catch { }
    }

    // Atomic write: a separate file means the timer owner's state file is
    // never clobbered by external mutators.
    private static void WritePendingAction(string action)
    {
        try
        {
            var path = PendingActionPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, action);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private static string? ConsumePendingAction()
    {
        try
        {
            if (!File.Exists(PendingActionPath)) return null;
            var action = File.ReadAllText(PendingActionPath).Trim();
            File.Delete(PendingActionPath);
            return string.IsNullOrEmpty(action) ? null : action;
        }
        catch { return null; }
    }

    private static bool LoadTimedState()
    {
        try
        {
            if (!File.Exists(TimedStatePath)) return false;
            var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
            if (state == null || state.Paths.Count == 0) return false;
            _timedPaths = state.Paths;
            _timedIndex = state.Index;
            _timedOptions = state.Options;
            _timedShuffle = state.Shuffle;
            _timedInterval = TimeSpan.FromSeconds(state.IntervalSeconds);
            _history = state.History;
            _historyIndex = state.HistoryIndex;
            _timedTimerPaused = state.TimerPaused;
            _timedTimerStopped = state.TimerStopped;
            _timedRemainingMs = state.RemainingMs > 0 ? state.RemainingMs : (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = state.WaitForVideoEnd;
            _advanceOnVideoEnd = state.AdvanceOnVideoEnd;
            _waitingForVideoEnd = state.WaitingForVideoEnd;
            // Restore speed for tick speed factor (video-time vs wall-clock). Per-video
            // override picked up via mpv IPC SetSpeed on next transition.
            _currentSpeed = SettingsService.Load().Speed;
            return true;
        }
        catch { return false; }
    }

    public static void Apply(string videoPath, string mpvOptions)
    {
        lock (_lock)
        {
            if (IsScenePath(videoPath))
            {
                var s = SettingsService.Load();
                if (!s.AllowScenes)
                    throw new InvalidOperationException("Enable \"Allow scene support\" in Settings to play scenes");
                if (!IsLweAvailable())
                    throw new InvalidOperationException("linux-wallpaperengine not found in PATH — install it to use scenes");
            }
            TeardownTimer();
            ClearTimedStateFile();
            SwitchToFile(videoPath, mpvOptions);
            // Track the single item so QueryCurrentPath works for scenes (no mpv socket to query) and a
            // live override can identify what's playing. _timedPaths stays null → no timed-playlist logic.
            _history = [videoPath];
            _historyIndex = 0;
        }
        UpdateRestartTimer();
    }

    public static void ApplyPlaylist(IReadOnlyList<string> videoPaths, string mpvOptions, bool shuffle = false, int intervalSeconds = 0)
    {
        if (videoPaths.Count == 0) return;
        lock (_lock)
        {
            _advanceOnVideoEnd = false;
            KillAll();
            ClearTimedStateFile();
            ClearPlaylistObserverPaths();

            var paths = shuffle
                ? videoPaths.OrderBy(_ => Guid.NewGuid()).ToArray()
                : videoPaths.ToArray();

            var settings = SettingsService.Load();
            if (settings.AllowScenes && IsLweAvailable() && paths.Any(IsScenePath))
            {
                var secs = intervalSeconds > 0 ? intervalSeconds : settings.GlobalIntervalSeconds;
                if (secs > 0)
                {
                    ApplyTimedPlaylist(paths, mpvOptions, shuffle, secs, waitForVideoEnd: true, advanceOnVideoEnd: true);
                    return;
                }
            }

            paths = paths.Where(p => !IsScenePath(p)).ToArray();
            if (paths.Length == 0) return;

            if (paths.Length == 1)
            {
                _current = Launch(mpvOptions, paths[0]);
                OnWallpaperChanged?.Invoke(paths[0]);
            }
            else
            {
                // Livepaper-owned: full playlist control, mid-session injection, no mpv playlist file
                ApplyTimedPlaylist(paths, mpvOptions, shuffle, intervalSeconds: 0, waitForVideoEnd: false, advanceOnVideoEnd: true);
                return;
            }
        }
        UpdateRestartTimer();
    }

    public static void ApplyTimedPlaylist(IReadOnlyList<string> paths, string mpvOptions, bool shuffle, int intervalSeconds, bool waitForVideoEnd = false, bool advanceOnVideoEnd = false)
    {
        lock (_lock)
        {
            TeardownTimer();
            if (paths.Count == 0) { KillCurrentProcess(); return; }

            var ordered = new List<string>(paths); // caller is responsible for initial order; shuffle flag only controls cycle-end reshuffle

            _timedPaths = ordered;
            _timedIndex = 0;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedTimerPaused = false;
            _timedTimerStopped = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
            _waitForVideoEnd = waitForVideoEnd;
            _advanceOnVideoEnd = advanceOnVideoEnd;
            // Pick up per-video speed override for the first item so tick scales interval
            // correctly from the first tick rather than after the next transition's SetSpeed.
            // Scenes always play at 1× (tick uses 1.0 factor anyway), so override irrelevant for them.
            _currentSpeed = IsScenePath(ordered[0])
                ? SettingsService.Load().Speed
                : (ReadSpeedOverride(ordered[0]) ?? SettingsService.Load().Speed);
            _history = [ordered[0]];
            _historyIndex = 0;
            SwitchToFile(ordered[0], mpvOptions);

            if (_advanceOnVideoEnd && !IsScenePath(ordered[0]) && ordered.Count > 1)
            {
                var next = AdvanceToNext();
                if (next != null)
                {
                    ArmVideoEndWait(next);
                }
            }

            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
                StartTimedTimer();
        }
        UpdateRestartTimer();
    }

    // Re-arms DoVideoEndWait after state is loaded from disk (daemon resume/restore).
    // Handles three cases:
    //   (a) Current item is a scene → clear wait flag, no rearm. Timer ticks normally.
    //   (b) Saved _waitingForVideoEnd=true (wait was in flight for either advance-on-end pre-fetch
    //       or wait-for-video-end after timer fire) → rearm DoVideoEndWait on the prefetched next.
    //   (c) Saved _waitingForVideoEnd=false + _advanceOnVideoEnd=true → set up new pre-fetch wait.
    // Must be called inside _lock.
    private static void RearmAdvanceOnVideoEnd()
    {
        if (_timedPaths == null || _timedPaths.Count <= 1 || _history == null) return;

        // Currently playing item: when _waitingForVideoEnd was saved true, _historyIndex is
        // pre-fetched ahead so the actual playing item is at index-1.
        string? current;
        if (_waitingForVideoEnd && _historyIndex > 0 && _historyIndex < _history.Count)
            current = _history[_historyIndex - 1];
        else if (_historyIndex >= 0 && _historyIndex < _history.Count)
            current = _history[_historyIndex];
        else
            current = null;

        // Scenes have no natural video-end signal — clear wait flag so the timer ticks.
        // Without this, a stale _waitingForVideoEnd=true would freeze the timer forever.
        if (current != null && IsScenePath(current))
        {
            _waitingForVideoEnd = false;
            _waitCts = null;
            return;
        }

        // (b) A wait was in flight in the GUI — re-attach to the pre-fetched next item.
        // Covers both advance-on-end (pre-fetched at launch) and wait-for-video-end (pre-fetched
        // when the timer fired). Current is a video (scene case bailed above), so prevIsVideo: true.
        if (_waitingForVideoEnd && _historyIndex >= 0 && _historyIndex < _history.Count)
        {
            ArmVideoEndWait(_history[_historyIndex]);
            return;
        }

        // (c) Advance-on-end mode mid-video without an in-flight wait — set one up.
        if (_advanceOnVideoEnd)
        {
            var next = AdvanceToNext();
            if (next == null) return;
            ArmVideoEndWait(next);
        }
    }

    public static bool RestoreTimedPlaylist()
    {
        bool ok;
        lock (_lock)
        {
            ok = false;
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;

            // When WaitingForVideoEnd=true the saved _historyIndex is pre-fetched one step ahead;
            // restore to the actually-playing item (one step back).
            var playingHistIdx = PlayingHistoryIndex;
            SwitchToFile(_history[playingHistIdx], _timedOptions);
            SaveTimedState();

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            RearmAdvanceOnVideoEnd();
            ok = true;
        }
        if (ok) UpdateRestartTimer();
        return ok;
    }

    public static bool ResumeTimedTimer()
    {
        bool ok;
        lock (_lock)
        {
            ok = false;
            if (!LoadTimedState()) return false;
            if (_timedPaths == null || _history == null || _timedPaths.Count == 0) return false;

            _timedTimerStopped = false;
            _timedTimerPaused = false;
            // _timedRemainingMs is restored from state — preserves the countdown

            if (_timedPaths.Count > 1 && _timedInterval.TotalSeconds > 0)
                StartTimedTimer();

            RearmAdvanceOnVideoEnd();
            ok = true;
        }
        if (ok) UpdateRestartTimer();
        return ok;
    }

    private static void StartTimedTimer()
    {
        _daemonCts?.Dispose();
        _daemonCts = new CancellationTokenSource();
        _lastTickTime = DateTime.UtcNow;
        ConsumePendingAction(); // discard any stale pending action from a prior session

        _playlistTimer = new Timer(_ => Tick(), null, TickInterval, Timeout.InfiniteTimeSpan);
    }

    private static void Tick()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (long)(now - _lastTickTime).TotalMilliseconds;
            _lastTickTime = now;

            RefreshSignals();
            if (_timedPaths == null) return;

            // If the current scene crashed, signal and advance immediately.
            // Skip while _waitingForVideoEnd: in advance-on-end mode history is pre-fetched
            // one step ahead, so _history[_historyIndex] may point to a scene that isn't
            // playing yet — checking it here would be a false positive.
            if (!_waitingForVideoEnd &&
                _history != null && _historyIndex >= 0 && _historyIndex < _history.Count &&
                IsScenePath(_history[_historyIndex]) && !IsSkippedPath(_history[_historyIndex]) && !IsLweRunning)
            {
                TransitionService.TransLog($"TICK scene-crash advance: idx={_historyIndex} item={System.IO.Path.GetFileName(_history[_historyIndex])} IsLweRunning=false");
                OnSceneCrashed?.Invoke(_history[_historyIndex]);
                _timedRemainingMs = 0;
                // Kill orphaned LWE immediately — the advance may go through DoVideoEndWait
                // (waitForVideoEnd=true) which doesn't call SwitchToFile right away.
                _prelaunchCts?.Cancel();
                _prelaunchCts = null;
                if (_prelaunchPidsToKill != null) { KillPids(_prelaunchPidsToKill); _prelaunchPidsToKill = null; }
            }

            if (_timedTimerStopped)
            {
                // Cover the race where CLI's Stop ran during our kill→launch
                // gap: the new mpvpaper we launched after CLI's KillAll would
                // otherwise survive forever.
                KillCurrentProcess();
                _timedPaths = null;
                _history = null;
                _historyIndex = -1;
                _playlistTimer?.Dispose();
                _playlistTimer = null;
                _daemonCts?.Cancel();
                OnTimedPlaylistStopped?.Invoke();
                return;
            }

            if (_timedTimerPaused)
            {
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            var pending = ConsumePendingAction();
            if (pending != null)
            {
                DispatchPendingAction(pending);
                _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                return;
            }

            // Freeze countdown only when BOTH waitForVideoEnd=true and a waiter is active.
            // In advance-on-end-only combined mode (_waitForVideoEnd=false), the interval still
            // counts so it can fire as a fallback if the video runs long.
            // _timedInterval > Zero distinguishes combined/timed mode from pure advance-on-end
            // (where _timedInterval = Zero and the countdown is irrelevant).
            if ((!_waitingForVideoEnd || !_waitForVideoEnd) && (!_advanceOnVideoEnd || _timedInterval > TimeSpan.Zero || IsLweRunning))
            {
                // Scale by playback speed so the timer counts video-time, not wall-clock.
                // At 2x speed the interval expires in half the wall-clock time, which is
                // correct: 15 real minutes = 30 minutes of video content. Scenes run at 1x.
                var speedFactor = IsLweRunning ? 1.0 : _currentSpeed;
                _timedRemainingMs -= (long)(elapsedMs * speedFactor);
            }

            // Video→scene LEAD (pure timed mode): a scene needs SceneTransitionDelayMs to load behind
            // the cover, so fire the switch that many ms EARLY — the live video keeps playing under the
            // overlay during the load and the scene reveals right at the interval boundary (mirrors the
            // video-end lead in DoVideoEndWait). Only V→S with a transition enabled; the reset-to-full
            // in LaunchAndReset prevents a re-fire. Skipped for advance-on-end / wait modes (those lead
            // via DoVideoEndWait) and for intervals shorter than the lead.
            // Gate on the wait MODE flags (_waitForVideoEnd / _advanceOnVideoEnd), not just the transient
            // _waitingForVideoEnd — between switches that transient is false, so without this the lead
            // fired off the INTERVAL in wait-for-video-end mode (premature V→S) instead of off the video end.
            if (!_advanceOnVideoEnd && !_waitForVideoEnd && !_waitingForVideoEnd && _timedInterval > TimeSpan.Zero)
            {
                int leadMs = SettingsService.Load().SceneTransitionDelayMs;
                if (leadMs > 0 && _timedRemainingMs <= leadMs && _timedRemainingMs > 0
                    && _timedInterval.TotalMilliseconds > leadMs)
                {
                    var cur = _history != null && _historyIndex >= 0 && _historyIndex < _history.Count ? _history[_historyIndex] : null;
                    var nxt = PeekNextTimed();
                    if (cur != null && !IsScenePath(cur) && nxt != null && IsScenePath(nxt)
                        && SettingsService.Load().AllowScenes && TransitionService.CurrentConfig().Enabled)
                    {
                        AdvanceAndLaunch();
                        _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
                        return;
                    }
                }
            }

            // _timedInterval > Zero prevents spurious expiry in pure advance-on-end mode
            // (where _timedInterval = Zero and _timedRemainingMs starts at 0).
            if (_timedRemainingMs <= 0 && _timedInterval > TimeSpan.Zero)
            {
                if (_waitingForVideoEnd && !_waitForVideoEnd)
                {
                    // Combined mode: interval fired before the video ended.
                    // AdvanceAndLaunch handles the pre-fetched _historyIndex + cancels the waiter.
                    AdvanceAndLaunch();
                }
                else if (!_waitingForVideoEnd)
                {
                    if (_waitForVideoEnd)
                    {
                        var next = AdvanceToNext();
                        if (next != null)
                        {
                            ArmVideoEndWait(next, prevIsVideo: false);
                        }
                        else
                        {
                            AdvanceAndLaunch();
                        }
                    }
                    else
                    {
                        AdvanceAndLaunch();
                    }
                }
                // _waitingForVideoEnd && _waitForVideoEnd: DoVideoEndWait handles the advance
            }

            _playlistTimer?.Change(TickInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private static void LaunchAndReset(string path)
    {
        SwitchToFile(path, SettingsService.Load().BuildMpvOptions());
        PostSwitch(path);
    }

    // Switch to the next wallpaper.
    //
    // Video→video: IPC loadfile replace (seamless, no flash). Falls back to
    // cold-start (kill old → launch new) if mpvpaper is not alive.
    //
    // Any transition involving a scene: pre-launch — the new process starts
    // first, and the old one is killed only after the new one signals
    // readiness (mpvpaper: AV: line; LWE: SceneTransitionDelayMs).
    private static void SwitchToFile(string path, string mpvOptions)
    {
        // Cancel any in-flight pre-launch transition from a previous switch.
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;
        // If the cancelled task had deferred LWE pids to kill, do it now so
        // rapid skipping doesn't accumulate orphan processes.
        if (_prelaunchPidsToKill != null)
        {
            KillPids(_prelaunchPidsToKill);
            _prelaunchPidsToKill = null;
        }

        bool nextIsScene = IsScenePath(path);
        bool prevIsScene = IsLweRunning;

        // ── WE-style transition (freeze A → animate to B's first frame) ───────
        // v1 fires only for video→video (seamless: B loads paused under the opaque overlay, the
        // renderer unpauses it at teardown). Scene-involved switches keep the existing pre-launch.
        // Captured here while A is still playing; the overlay then hides the loadfile underneath.
        // Only when the seamless IPC swap below will actually run. If the cold path would run (mpv
        // dead / deferred leak-restart), B takes ~2s to appear and would gap before the brief overlay
        // ends — so skip the transition and just instant-cut.
        bool transitionPauseB = false; // method-dependent: reveal=false (B live), frozen/full-live=true (B paused, renderer unpauses)
        bool mpvAliveForTransition = File.Exists(IpcSocket) && MpvpaperProcs().Length > 0;
        if (!nextIsScene && !prevIsScene && IsPlaying && mpvAliveForTransition && !_restartPending)
        {
            var tcfg = TransitionService.CurrentConfig();
            if (tcfg.Enabled)
            {
                var fromPath = QueryCurrentPath();
                // The overlay covers (frozen A / live decode) before we switch B underneath; whether B
                // is loaded paused or live depends on the chosen method (TryStart resolves it + reports
                // pauseB). reveal = B plays live underneath; frozen/full-live = B paused, renderer unpauses.
                if (!string.IsNullOrEmpty(fromPath) && fromPath != path)
                    TransitionService.TryStart(fromPath, false, path, false, tcfg, out transitionPauseB);
            }
        }

        // ── Transition involving a scene ──────────────────────────────────────
        if (nextIsScene || prevIsScene)
        {
            // a scene swap tears mpvpaper down (→ leak reset) and "zeroes" the restart timer — any
            // deferred restart is satisfied by the teardown; clear it + reset the in-process countdown.
            if (_restartPending) { _restartPending = false; if (!_daemonMode) UpdateRestartTimer(); }

            // ── WE-style transition for a scene-involved switch ───────────────────────────────────
            // Cover A first (grim still if A is a scene, ffmpeg/live-decode if video), THEN the
            // crossover below launches B UNDERNEATH the opaque overlay (hidden) → the renderer reveals
            // it. This hides the LWE launch flash / the mpvpaper↔LWE swap. TryStart resolves the method
            // (auto-fallback: full-live A=scene→reveal, frozen B=scene→reveal, V→S full-live→reveal-b)
            // and blocks until the overlay covers, so B is launched only once A is hidden.
            bool scenePauseB = false; // frozen S→V: launch mpvpaper-B paused at frame0; renderer unpauses at teardown
            bool transitionActive = false; // did the visual transition actually fire → gate the audio crossfade to match
            if (IsPlaying)
            {
                var tcfg = TransitionService.CurrentConfig();
                // QueryCurrentPath() reads the OLD video from mpv, but for a scene A the playlist history
                // has already advanced to the target → it returns `path`. grim captures the on-screen
                // scene regardless of its path, so use a sentinel for a scene A and only enforce the
                // "different file" guard (skip same-wallpaper switches) for a video A.
                var fromPath = prevIsScene ? "scene" : QueryCurrentPath();
                if (tcfg.Enabled && !string.IsNullOrEmpty(fromPath) && (prevIsScene || fromPath != path))
                {
                    TransitionService.TryStart(fromPath, prevIsScene, path, nextIsScene, tcfg, out scenePauseB);
                    transitionActive = true;
                }
            }

            if (nextIsScene)
            {
                var settings = SettingsService.Load();
                if (!settings.AllowScenes || !IsLweAvailable())
                {
                    KillCurrentProcess();
                    return;
                }
                // Folder-based scene: the item folder IS `path`. Launch a subscribed item by id (LWE's
                // native lookup in the WE dir); an owned copy by directory path (copied scenes always
                // launched from a path — SpawnLweProcesses' last arg accepts id OR dir).
                var loc = LibraryStore.Locate(path);
                string lweArg = loc?.Source == LibraryStore.Local ? loc.Value.Key : path;

                // Capture old processes before launching new ones.
                var oldMpvProcs = MpvpaperProcs();
                var oldLwePids = ReadCurrentLwePids();
                // Snapshot the OUTGOING scene's PROCESS PIDs (real, from pgrep) before launching B. The PID
                // is stable per LWE process even when LWE re-registers its client / re-creates its stream,
                // so it reliably separates old from new (client.id and sink-input # churn; the PID doesn't),
                // and pgrep — unlike the client map — never transiently drops a process mid-registration.
                var oldPids = GetLweProcPids();

                int targetVol = ReadVolumeOverride(path) ?? settings.Volume;
                var newPids = SpawnLweProcesses(lweArg, settings, targetVol);
                OnWallpaperChanged?.Invoke(path);

                var capturedLwePids = oldLwePids;
                _prelaunchPidsToKill = capturedLwePids;
                var cts = _prelaunchCts = new CancellationTokenSource();
                var capturedMpv = oldMpvProcs;
                bool oldIsMpv = capturedMpv.Length > 0;       // V→S: fade the outgoing VIDEO via mpv IPC; S→S: fade old LWE by client.id
                bool hasOld = oldIsMpv || prevIsScene;         // false = FIRST wallpaper (nothing to crossfade) → no fade-in
                bool muted0 = _isMuted || AudioMonitor.IsMuted || settings.NoAudio;
                int fadeMs = Math.Clamp(TransitionService.CurrentConfig().DurationMs, 200, 8000);
                int coverMs = Math.Clamp(settings.SceneTransitionDelayMs, 0, 8000);
                int oldMuteI = muted0 ? 1 : 0;
                _ = Task.Run(async () =>
                {
                    // the new scene's PID(s) = LWE processes that appeared after the launch (≠ the old set)
                    HashSet<int> NewPids() { var p = GetLweProcPids(); p.ExceptWith(oldPids); return p; }
                    // lp-audio holds these targets event-driven (instant on any stream create/reset). We only
                    // push the ramp values over stdin; the helper keeps them glued across LWE's re-inits.
                    string oldCsv = PidCsv(oldPids);
                    var newScenePids = new HashSet<int>();
                    try
                    {
                        // Crossfade ONLY when a visual transition is active. Transitions off → instant cut:
                        // new is already launched at target, just wait the load delay, then reap (no lp-audio).
                        if (transitionActive && newPids.Length > 0)
                        {
                            // Wipe any stale lp-audio groups FIRST — a prior transition's `clear` tail can be
                            // cancelled by this advance, and Linux REUSES PIDs, so a lingering "[dead pid]→0"
                            // group can pin a freshly-launched scene at 0 forever. Clearing at the START of
                            // every transition guarantees a clean slate (no accumulation, no PID-reuse poison).
                            AudioCtl("clear");
                            // PHASE 1 — cover: new muted+0, old held at target (lp-audio applies on stream events).
                            if (!oldIsMpv && oldPids.Count > 0) AudioCtl($"set {oldCsv} {targetVol} {oldMuteI}");
                            var coverUntil = DateTime.UtcNow.AddMilliseconds(hasOld ? coverMs : 0);
                            do
                            {
                                newScenePids = NewPids();
                                if (newScenePids.Count > 0) AudioCtl($"set {PidCsv(newScenePids)} 0 1"); // muted, 0
                                if (DateTime.UtcNow >= coverUntil && newScenePids.Count >= 1) break;
                                await Task.Delay(50, cts.Token);
                            } while (DateTime.UtcNow < coverUntil || newScenePids.Count < 1);
                            string newCsv = PidCsv(newScenePids);
                            TransitionService.TransLog($"scene xfade(lp-audio): new={newScenePids.Count} old={oldPids.Count} oldIsMpv={oldIsMpv} target={targetVol} muted={muted0} hasOld={hasOld} fadeMs={fadeMs}");

                            // PHASE 2 — reveal: ramp new 0→target (unmuted) + old target→0. No fade on first.
                            if (!hasOld) { AudioCtl($"set {newCsv} {targetVol} {oldMuteI}"); }
                            else
                            {
                                int steps = Math.Clamp(fadeMs / 120, 5, 12);
                                for (int s = 1; s <= steps && !cts.IsCancellationRequested; s++)
                                {
                                    double f = (double)s / steps;
                                    int nv = (int)Math.Round(targetVol * f), ov = (int)Math.Round(targetVol * (1 - f));
                                    if (newScenePids.Count > 0) AudioCtl($"set {newCsv} {nv} {oldMuteI}");
                                    if (oldIsMpv) SendCommand("set_property", "volume", ov);
                                    else if (oldPids.Count > 0) AudioCtl($"set {oldCsv} {ov} {oldMuteI}");
                                    await Task.Delay(Math.Max(1, fadeMs / steps), cts.Token);
                                }
                                if (newScenePids.Count > 0) AudioCtl($"set {newCsv} {targetVol} {oldMuteI}");
                                if (!oldIsMpv && oldPids.Count > 0) AudioCtl($"set {oldCsv} 0 {oldMuteI}");
                            }
                        }
                        else
                        {
                            // No crossfade (transitions off / first wallpaper). No fade sets the level, and
                            // stream-restore may have poisoned the launch volume → correct the new scene to
                            // target via lp-audio (event-driven, survives the reap re-init) while we wait the
                            // load delay before reaping the old.
                            CorrectSceneVolume(oldPids, targetVol, muted0, 3000);
                            await Task.Delay(SettingsService.Load().SceneTransitionDelayMs, cts.Token);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                    // old faded to 0 → reap it (gated on the fade, not a fixed delay)
                    _prelaunchPidsToKill = null;
                    foreach (var proc in capturedMpv)
                        using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
                    if (capturedMpv.Length > 0)
                    {
                        lock (_lock) { _current = null; }
                        var sock = IpcSocket;
                        try { if (File.Exists(sock)) File.Delete(sock); } catch { }
                    }
                    KillPids(capturedLwePids);

                    // The reap re-inits the NEW stream → reset; lp-audio holds it (it keeps the group set,
                    // applying on the 'change' event in ~1ms). Re-assert target, hold ~3s, then release so
                    // normal volume control (ApplyLweVolume) governs steady state. (Crossfade only.)
                    if (transitionActive && newScenePids.Count > 0)
                    {
                        AudioCtl($"set {PidCsv(newScenePids)} {targetVol} {oldMuteI}");
                        try { await Task.Delay(3000, cts.Token); } catch { }
                        AudioCtl("clear");
                    }
                });
            }
            else // scene→video (= S→V). transitions on → LIVE-COMPOSITE (reveal-a): the scene-A stays
                 // LIVE underneath, the overlay decodes+composites B over it AND plays B's audio. So
                 // mpvpaper-B must NOT be at BACKGROUND during the effect (it would cover the live A) —
                 // it's launched LATE, under the overlay's opaque end-hold, then the old scene is reaped.
            {
                var oldLwePids = ReadCurrentLwePids();
                var oldPids = GetLweProcPids();   // outgoing scene's real process pids (stable across re-init)
                KillMpvPaperOnly(); // safety: clear any stray mpvpaper + socket

                var settings = SettingsService.Load();
                int targetVol = ReadVolumeOverride(path) ?? settings.Volume;
                double spd = ReadSpeedOverride(path) ?? settings.Speed;
                bool muted0 = _isMuted || AudioMonitor.IsMuted || settings.NoAudio;
                int oldMuteI = muted0 ? 1 : 0;
                int fadeMs = Math.Clamp(TransitionService.CurrentConfig().DurationMs, 200, 8000);
                string oldCsv = PidCsv(oldPids);
                var cts = _prelaunchCts = new CancellationTokenSource();
                var capturedPids = oldLwePids;

                // Launch mpvpaper-B at `vol`. `paused` → load it PAUSED at frame 0 (reveal-a): the renderer
                // unpauses it (--mpv-unpause) at teardown so it hands off frame0→frame0 with the overlay-B
                // (also paused at frame0) — NO seek, exactly like V→V (B never advances → nothing to repeat).
                // Wires _current/speed/mute, returns its AV:-ready signal.
                TaskCompletionSource<bool> LaunchVideoB(int vol, bool paused = false, double startSec = 0)
                {
                    var rt = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var lo = BakeNormalization(BakeSpeedOverride(BakeVolumeOverride(settings.BuildMpvOptions(), path), path), path);
                    if (_isMuted && !lo.Contains("--no-audio")) lo += " --mute=yes";
                    lo += $" --volume={vol}";
                    if (paused && !lo.Contains("--pause")) lo += " --pause=yes";
                    if (startSec > 0.2) lo += $" --start={startSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}";  // pre-buffer near the handoff
                    _current = Launch(lo, path, rt);
                    _currentSpeed = spd;
                    Task.Run(() => { SetSpeed(spd); if (_isMuted) SendCommand("set_property", "mute", true); });
                    OnWallpaperChanged?.Invoke(path);
                    return rt;
                }

                if (!transitionActive)
                {
                    // instant cut: launch B at target now, hold the old scene the load delay, then reap.
                    LaunchVideoB(targetVol);
                    _ = Task.Run(async () =>
                    {
                        try { await Task.Delay(settings.SceneTransitionDelayMs, cts.Token); } catch { return; }
                        if (!cts.IsCancellationRequested) { KillPids(capturedPids); try { if (File.Exists(LwePidPath)) File.Delete(LwePidPath); } catch { } }
                    });
                    return;
                }

                _ = Task.Run(async () =>
                {
                    // reveal-a: the overlay (already covering, spawned by TryStart) reveals B's frozen frame0
                    // over the live scene-A. We fade the live scene-A OUT via lp-audio over the effect, then —
                    // once the effect completes and the overlay holds opaque-B (--hold-end-ms) — launch
                    // mpvpaper-B UNDER that cover PAUSED at frame0; the renderer unpauses it (--mpv-unpause)
                    // at teardown → frame0→frame0 handoff with the overlay-B, no seek, no repeat.
                    try
                    {
                        AudioCtl("clear");
                        if (oldPids.Count > 0) AudioCtl($"set {oldCsv} {targetVol} {oldMuteI}");
                        // Fade scene-A out over the effect PLUS the end-hold (≈ when B's audio starts at
                        // teardown) so it tails out instead of going silent mid-hold; launch mpvpaper-B
                        // PAUSED at frame0 the moment the EFFECT completes (overlay opaque → B hidden under
                        // it), so it's decoded + ready when the renderer unpauses it at teardown.
                        const int holdMs = 1300;                 // matches --hold-end-ms in TransitionService
                        int totalMs = fadeMs + holdMs;
                        // pre-buffer mpvpaper-B near the TEARDOWN position so the renderer's exact seek is
                        // tiny/fast (≈ effect + hold − overlay warmup), wrapped for looping videos.
                        double bDur = ProbeDurationSec(path);
                        double preBuf = totalMs / 1000.0 - 0.3;
                        if (bDur > 0.5) preBuf %= bDur;
                        if (preBuf < 0) preBuf = 0;
                        int steps = Math.Clamp(totalMs / 120, 6, 16);
                        TaskCompletionSource<bool>? rt = null;
                        for (int s = 1; s <= steps && !cts.IsCancellationRequested; s++)
                        {
                            double el = (double)s / steps * totalMs;
                            double f = Math.Min(1.0, el / totalMs);
                            if (oldPids.Count > 0) AudioCtl($"set {oldCsv} {(int)Math.Round(targetVol * (1 - f))} {oldMuteI}");
                            if (rt == null && el >= fadeMs) rt = LaunchVideoB(targetVol, paused: true, startSec: preBuf);
                            await Task.Delay(Math.Max(1, totalMs / steps), cts.Token);
                        }
                        if (oldPids.Count > 0) AudioCtl($"set {oldCsv} 0 {oldMuteI}");
                        if (cts.IsCancellationRequested) return;
                        rt ??= LaunchVideoB(targetVol, paused: true, startSec: preBuf);
                        try { using var lk = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); lk.CancelAfter(1500); await rt.Task.WaitAsync(lk.Token); } catch { }
                    }
                    catch (OperationCanceledException) { return; }
                    catch { }
                    if (!cts.Token.IsCancellationRequested)
                    {
                        KillPids(capturedPids);   // reap the old scene; the overlay tears down → mpvpaper-B shows
                        try { if (File.Exists(LwePidPath)) File.Delete(LwePidPath); } catch { }
                    }
                    AudioCtl("clear");
                });
            }
            return;
        }

        // ── Video→video: existing seamless approach ───────────────────────────
        // _restartPending → force the cold-start branch (skip the seamless IPC swap) so THIS changeover
        // relaunches mpvpaper (the deferred leak-restart, masked by the natural switch). Then clear it.
        bool mpvAlive = File.Exists(IpcSocket) && MpvpaperProcs().Length > 0;
        // Compute the effective volume/speed BEFORE the switch so they can ride INTO `loadfile` as
        // per-file options. A post-loadfile `set_property volume` races mpv's own apply of the launch
        // default (--volume) and gets clobbered → the advanced-to item played at the global, not its
        // override. Passing them as loadfile options applies them atomically with the load (no race).
        var cfg = SettingsService.Load();
        int effVol = ReadVolumeOverride(path) ?? cfg.Volume;
        double effSpd = ReadSpeedOverride(path) ?? cfg.Speed;
        if (mpvAlive && !_restartPending && TryIpcSwitchToFile(path, effVol, effSpd, pauseAtStart: transitionPauseB))
        {
            _currentSpeed = effSpd; // keep the timed-tick speed factor in sync (cold path sets it below)
            OnWallpaperChanged?.Invoke(path);
            if (_isMuted) Task.Run(() => SendCommand("set_property", "mute", true));
        }
        else
        {
            KillCurrentProcess();
            var opts = BakeNormalization(BakeSpeedOverride(BakeVolumeOverride(mpvOptions, path), path), path);
            if (_isMuted && !opts.Contains("--no-audio")) opts += " --mute=yes";
            _current = Launch(opts, path);
            // Keep tick speed factor in sync with the freshly launched video (mpv IPC path
            // already updates this via SetSpeed; cold-start has no SetSpeed call).
            _currentSpeed = ReadSpeedOverride(path) ?? SettingsService.Load().Speed;
            // this cold relaunch satisfied any deferred leak-restart; reset the in-process countdown
            if (_restartPending) { _restartPending = false; if (!_daemonMode) UpdateRestartTimer(); }
            OnWallpaperChanged?.Invoke(path);
        }
        OnWallpaperChanged?.Invoke(path);
    }

    private static bool TryIpcSwitchToFile(string path, int volume, double speed, bool pauseAtStart = false)
    {
        // Read AppSettings.Loop directly so the loop state is explicit rather
        // than parsed out of the kill+launch options string. Other launch-only
        // options (hwdec, cache, demuxer) can't be toggled mid-session and only
        // take effect on next cold start.
        bool loopFile = SettingsService.Load().Loop;
        // Per-file options applied atomically with the load (mpv ≥ 0.38: loadfile <url> <flags> <index> <opts>).
        // Carries the effective volume/speed (+ mute) so the new file starts at them — no post-load reset race.
        var fileOpts = $"volume={volume},speed={speed.ToString("G", System.Globalization.CultureInfo.InvariantCulture)}";
        if (_isMuted) fileOpts += ",mute=yes";
        // Transition handoff: load B paused at frame 0 (hidden under the opaque overlay); the
        // lp-transition renderer unpauses it (--mpv-unpause) the instant it tears down → seamless.
        if (pauseAtStart) fileOpts += ",pause=yes";
        // Between-video loudness normalization for the incoming file (or "" to clear) — set the `af`
        // before loadfile so the new file plays normalized; the volume in fileOpts stacks on top.
        TrySendCommand("set", "af", NormalizeAf(path) ?? "");
        return TrySendCommand("set", "loop-file", loopFile ? "inf" : "no")
            && TrySendCommand("set", "loop-playlist", "no")
            && TrySendCommand("playlist-clear")
            && TrySendCommand("loadfile", path, "replace", 0, fileOpts);
    }

    // Bool-returning variant of SendCommand for callers that need to know
    // whether the IPC succeeded so they can fall back to a fresh launch.
    private static bool TrySendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return false;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string? QueryCurrentPath()
    {
        // For scenes, the mpv socket isn't connected. Fall back to the in-memory history.
        if (IsLweRunning)
        {
            lock (_lock)
            {
                if (_history != null && _historyIndex >= 0 && _historyIndex < _history.Count)
                    return _history[_historyIndex];
            }
        }
        return TryQueryCurrentPath();
    }

    public static string? QueryCurrentSceneWorkshopId()
    {
        if (!IsLweRunning) return null;
        lock (_lock)
        {
            if (_history == null || _historyIndex < 0 || _historyIndex >= _history.Count) return null;
            var path = _history[_historyIndex];
            if (!IsScenePath(path)) return null;
            // folder-based: the scene folder's NAME is the workshop id (workshop/<id> or local/<id>)
            return Path.GetFileName(path.TrimEnd('/', '\\'));
        }
    }

    private static string? TryQueryCurrentPath()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "path" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            foreach (var line in Encoding.UTF8.GetString(buf, 0, n).Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                        return data.GetString();
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    // Switch from timed-interval mode to advance-on-end without restarting mpvpaper.
    // Tears down the timer, appends remaining paths to mpv's in-memory playlist, and
    // sets loop-playlist so mpv advances naturally when each file ends.
    public static void SwitchFromTimedToAdvanceOnEnd(IReadOnlyList<string> allPaths, bool shuffle)
    {
        lock (_lock)
        {
            if (_timedPaths == null || _history == null) return;

            // Cancel any in-flight pre-fetch wait
            bool wasWaiting = _waitingForVideoEnd;
            _waitCts?.Cancel();
            _waitCts = null;
            _waitingForVideoEnd = false;

            // Un-advance history index if pre-fetch was in progress
            if (wasWaiting && _historyIndex > 0)
                _historyIndex--;

            _advanceOnVideoEnd = true;
            _waitForVideoEnd = false;
            _timedInterval = TimeSpan.Zero;
            _timedShuffle = shuffle;
            // _timedRemainingMs preserved: scenes still use the countdown;
            // video paths arm DoVideoEndWait which holds the tick via _waitingForVideoEnd.

            // Re-arm DoVideoEndWait for the next item
            string? currentPath = (_historyIndex >= 0 && _historyIndex < _history.Count)
                ? _history[_historyIndex] : null;
            if (currentPath != null && !IsScenePath(currentPath) && _timedPaths.Count > 1)
            {
                var next = AdvanceToNext();
                if (next != null)
                    ArmVideoEndWait(next);
            }

            SaveTimedState();
        }
    }

    // Switch from advance-on-end mode to timed-interval without restarting mpvpaper.
    // Converts mpv to single-file loop mode, then starts the livepaper timer.
    public static void SwitchFromAdvanceOnEndToTimed(IReadOnlyList<string> allPaths, string mpvOptions, bool shuffle, int intervalSeconds, bool waitForVideoEnd)
    {
        lock (_lock)
        {
            if (allPaths.Count == 0) return;

            // Cancel any in-flight DoVideoEndWait (may have set loop-file=no + loadfile append)
            _waitCts?.Cancel();
            _waitCts = null;
            _waitingForVideoEnd = false;

            var currentPath = TryQueryCurrentPath();

            // Restore single-file loop mode and clear any pre-appended next item
            TrySendCommand("set", "loop-file", "inf");
            TrySendCommand("playlist-clear");
            TrySendCommand("set", "loop-playlist", "no");

            var ordered = new List<string>(allPaths);
            _timedPaths = ordered;
            _timedOptions = mpvOptions;
            _timedShuffle = shuffle;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            _timedTimerPaused = false;
            _timedTimerStopped = false;
            var timePos = TryQueryTimePos();
            long fullMs = (long)_timedInterval.TotalMilliseconds;
            long elapsedMs = timePos.HasValue ? (long)(timePos.Value * 1000) : 0;
            bool instantAdvance = elapsedMs >= fullMs;
            _timedRemainingMs = instantAdvance ? fullMs : fullMs - elapsedMs;
            _waitForVideoEnd = waitForVideoEnd;
            _advanceOnVideoEnd = false;

            var startPath = currentPath ?? ordered[0];
            var idx = ordered.IndexOf(startPath);
            if (idx < 0) idx = 0;
            _timedIndex = idx;
            _history = [startPath];
            _historyIndex = 0;

            SaveTimedState();

            if (ordered.Count > 1 && intervalSeconds > 0)
            {
                StartTimedTimer();
                if (instantAdvance) AdvanceAndLaunch();
            }
        }
    }

    // Reorders the active playlist in real time without restarting mpvpaper or resetting the timer countdown.
    // Sequential: positions the current wallpaper at index 0, then appends the rest in original order
    //             starting from current+1 (wrapping). Shuffled: same but randomises the tail.
    public static void ReorderPlaylist(IReadOnlyList<string> originalPaths, bool isTimedPlaylist, bool shuffle)
    {
        lock (_lock)
        {
            if (originalPaths.Count == 0) return;

            // Mixed advance-on-end playlists use the timed machinery even when the
            // caller passes isTimedPlaylist=false (it only knows about native-mpv mode).
            if (!isTimedPlaylist && _timedPaths != null)
                isTimedPlaylist = true;

            if (isTimedPlaylist)
            {
                if (_timedPaths == null && !LoadTimedState()) return;

                // In advance-on-end mode, _historyIndex is pre-fetched one step ahead of the
                // playing item. Use the actual playing item as the reorder anchor.
                bool preIsFetched = _waitingForVideoEnd
                    && _history != null && _historyIndex > 0 && _historyIndex < _history.Count;
                int playingHistIdx = preIsFetched ? _historyIndex - 1 : _historyIndex;
                var currentPath = (_history != null && playingHistIdx >= 0 && playingHistIdx < _history.Count)
                    ? _history[playingHistIdx]
                    : null;

                int currentIdx = -1;
                for (int i = 0; i < originalPaths.Count; i++)
                    if (originalPaths[i] == currentPath) { currentIdx = i; break; }

                var rest = new List<string>();
                if (currentIdx >= 0)
                {
                    for (int i = currentIdx + 1; i < originalPaths.Count; i++) rest.Add(originalPaths[i]);
                    for (int i = 0; i < currentIdx; i++) rest.Add(originalPaths[i]);
                }
                else
                {
                    rest.AddRange(originalPaths);
                }

                if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

                List<string> newPaths;
                int newTimedIndex;
                if (currentIdx >= 0 && currentPath != null)
                {
                    // Current is still in the playlist — place it at index 0 so next advance
                    // increments to index 1 (first of rest).
                    newPaths = new List<string>([currentPath]);
                    newPaths.AddRange(rest);
                    newTimedIndex = 0;
                }
                else
                {
                    // Current was removed — let it finish but don't replay it.
                    // Setting _timedIndex to the last slot makes the next advance wrap to 0.
                    newPaths = rest;
                    newTimedIndex = newPaths.Count - 1;
                }

                _timedShuffle = shuffle;
                _timedPaths = newPaths;
                _timedIndex = newTimedIndex;
                _history = currentPath != null ? [currentPath] : [newPaths[0]];
                _historyIndex = 0;
                // Intentionally preserve _timedRemainingMs — don't reset the countdown

                // In advance-on-end mode the in-flight DoVideoEndWait holds a reference to the
                // old path list. Cancel it and re-chain from the (now playing) current item.
                if (_advanceOnVideoEnd)
                {
                    if (_waitingForVideoEnd)
                    {
                        _waitCts?.Cancel();
                        _waitCts = null;
                        _waitingForVideoEnd = false;
                        TrySendCommand("set", "loop-file", "inf");
                    }
                    var playingPath = _history != null && _historyIndex >= 0 && _historyIndex < _history!.Count
                        ? _history[_historyIndex]
                        : null;
                    if (playingPath != null && !IsScenePath(playingPath) && newPaths.Count > 1)
                    {
                        var nextPath = AdvanceToNext();
                        if (nextPath != null)
                            ArmVideoEndWait(nextPath);
                    }
                }

                SaveTimedState();
            }
            else
            {
                // Advance-on-end: rebuild mpv's playlist from the current position
                var currentPath = TryQueryCurrentPath();
                if (currentPath == null) return; // IPC not ready — skip to avoid corrupting the queue

                int currentIdx = -1;
                for (int i = 0; i < originalPaths.Count; i++)
                    if (originalPaths[i] == currentPath) { currentIdx = i; break; }

                var rest = new List<string>();
                if (currentIdx >= 0)
                {
                    for (int i = currentIdx + 1; i < originalPaths.Count; i++) rest.Add(originalPaths[i]);
                    for (int i = 0; i < currentIdx; i++) rest.Add(originalPaths[i]);
                }
                else
                {
                    rest.AddRange(originalPaths); // current was removed; all new paths (none duplicate current)
                }

                if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

                TrySendCommand("playlist-clear");
                foreach (var p in rest)
                    TrySendCommand("loadfile", p, "append");
                TrySendCommand("set", "loop-playlist", "inf");

                // Restart observer with the new playlist order so playlist-pos events
                // map to the correct cards after reorder
                var newObserverPaths = new List<string> { currentPath };
                newObserverPaths.AddRange(rest);
                StartPlaylistObserver(newObserverPaths);
                try { File.WriteAllText(PlaylistObserverPathsPath, JsonSerializer.Serialize(newObserverPaths)); } catch { }
            }
        }
    }

    // Syncs the advance-on-end (mpv-native) playlist to the new path list.
    // For pure additions without shuffle, just appends the new items to mpv's existing
    // queue (preserving the current playback order). Everything else does a full rebuild.
    public static void SyncAdvanceOnEndPlaylist(IReadOnlyList<string> oldPaths, IReadOnlyList<string> newPaths, bool shuffle)
    {
        lock (_lock)
        {
            if (newPaths.Count == 0) return;

            // Mixed advance-on-end playlists use the timed machinery — IPC playlist ops are useless here.
            if (_timedPaths != null)
            {
                ReorderPlaylist(newPaths, true, shuffle);
                return;
            }

            var currentPath = TryQueryCurrentPath();
            if (currentPath == null) return;

            var added = newPaths.Except(oldPaths).ToHashSet();
            var removed = oldPaths.Except(newPaths).ToHashSet();

            // Add-only without shuffle: append new items to the END of mpv's current queue.
            // This preserves the existing playback order (items that were next stay next)
            // and slots new items in AFTER the full current cycle.
            if (!shuffle && added.Count > 0 && removed.Count == 0)
            {
                // Verify the non-added items are in the same order (no reorder happened)
                var existingInNew = newPaths.Where(p => !added.Contains(p)).ToList();
                var existingInOld = oldPaths.Where(p => !added.Contains(p)).ToList();
                if (existingInNew.SequenceEqual(existingInOld))
                {
                    var appendedPaths = newPaths.Where(p => added.Contains(p)).ToList();
                    foreach (var p in appendedPaths)
                        TrySendCommand("loadfile", p, "append");
                    // Extend observer paths with appended items
                    var extendedPaths = _currentObserverPaths.Concat(appendedPaths).ToList();
                    StartPlaylistObserver(extendedPaths);
                    try { File.WriteAllText(PlaylistObserverPathsPath, JsonSerializer.Serialize(extendedPaths)); } catch { }
                    return;
                }
            }

            // Full rebuild for removes, reorders, or adds-with-shuffle
            int currentIdx = -1;
            for (int i = 0; i < newPaths.Count; i++)
                if (newPaths[i] == currentPath) { currentIdx = i; break; }

            var rest = new List<string>();
            if (currentIdx >= 0)
            {
                for (int i = currentIdx + 1; i < newPaths.Count; i++) rest.Add(newPaths[i]);
                for (int i = 0; i < currentIdx; i++) rest.Add(newPaths[i]);
            }
            else
            {
                // Current was removed. Map its old position into newPaths so the item that
                // "replaced" it positionally plays next instead of restarting from index 0.
                int oldCurrentIdx = -1;
                for (int i = 0; i < oldPaths.Count; i++)
                    if (oldPaths[i] == currentPath) { oldCurrentIdx = i; break; }

                int startIdx = oldCurrentIdx >= 0
                    ? Math.Min(oldCurrentIdx, newPaths.Count - 1)
                    : 0;

                for (int i = startIdx; i < newPaths.Count; i++) rest.Add(newPaths[i]);
                for (int i = 0; i < startIdx; i++) rest.Add(newPaths[i]);
            }

            if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();

            TrySendCommand("playlist-clear");
            foreach (var p in rest)
                TrySendCommand("loadfile", p, "append");
            TrySendCommand("set", "loop-playlist", "inf");

            // Restart observer with the new playlist order
            var newObserverPaths = new List<string> { currentPath };
            newObserverPaths.AddRange(rest);
            StartPlaylistObserver(newObserverPaths);
            try { File.WriteAllText(PlaylistObserverPathsPath, JsonSerializer.Serialize(newObserverPaths)); } catch { }
        }
    }

    // Public accessor for the web UI (sync the in-app wallpaper preview to live mpv position).
    public static double? QueryTimePos() => TryQueryTimePos();

    // Current video's total length (for the transition: wrap A's advanced start on looping videos).
    public static double? QueryDuration() => TryQueryProperty("duration");

    private static double? TryQueryProperty(string prop)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", prop } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            foreach (var line in Encoding.UTF8.GetString(buf, 0, n).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                        return data.GetDouble();
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    private static double? TryQueryTimePos()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "time-pos" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            using var doc = JsonDocument.Parse(buf.AsMemory(0, n));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                return data.GetDouble();
            return null;
        }
        catch { return null; }
    }

    private static double? TryQueryTimeRemaining()
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return null;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            // playtime-remaining = time-remaining / speed (wall-clock seconds, not video seconds).
            // Using time-remaining here would oversleep at speed > 1, causing B to loop multiple times
            // before we transition to the next file.
            var cmd = JsonSerializer.Serialize(new { command = new object[] { "get_property", "playtime-remaining" } });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
            var buf = new byte[4096];
            int n = socket.Receive(buf);
            var str = Encoding.UTF8.GetString(buf, 0, n);
            foreach (var line in str.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Number)
                        return data.GetDouble();
                }
                catch { }
            }
            return null;
        }
        catch { return null; }
    }

    // prevIsVideo: caller asserts the currently-playing item is a video (not a scene).
    // Used when spawning a chain immediately after SwitchToFile(video) so prevIsScene is
    // correctly false even when the async LWE kill hasn't completed yet.
    private static async Task DoVideoEndWait(string next, CancellationToken ct, bool prevIsVideo = false)
    {
        // For video→video: disable loop on current, append next to mpv's playlist so
        // the transition happens inside mpv (no blank gap), then restore loop after.
        // Falls back to an immediate SwitchToFile for scene transitions or when IPC is unavailable.
        bool nextIsScene = IsScenePath(next);
        bool prevIsScene = !prevIsVideo && IsLweRunning;
        TransitionService.TransLog($"DoVideoEndWait: next={System.IO.Path.GetFileName(next)} nextIsScene={nextIsScene} prevIsVideo={prevIsVideo} IsLweRunning={IsLweRunning} → prevIsScene={prevIsScene}");

        // In advance-on-end mode PostSwitch re-arms us the instant the overlay fired the switch — but
        // the IPC loadfile-B hasn't settled yet, so mpv still reports the OUTGOING file's near-end
        // playtime-remaining. Reading that now computes sleepMs<=0 → we'd fire a spurious SECOND switch
        // immediately (rapid A→B→A, landing on the wrong item). Wait for the transition's effect+teardown
        // to finish (B actually playing) before sampling. (No-op when no overlay is in flight.)
        while (TransitionService.InProgress && !ct.IsCancellationRequested)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        if (ct.IsCancellationRequested) return;

        if (!nextIsScene && !prevIsScene)
        {
            // In advance-on-end mode the first DoVideoEndWait fires immediately after launch;
            // wait up to 5s for the mpv socket before querying.
            double? remaining = TryQueryTimeRemaining();
            if (remaining == null && _advanceOnVideoEnd)
            {
                for (int i = 0; i < 50 && remaining == null && !ct.IsCancellationRequested; i++)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    remaining = TryQueryTimeRemaining();
                }
            }
            // transitions ON → fire the full-live overlay V→V transition, leading by the transition
            // wall-time (effect + warmup) so it COMPLETES at the video's end: A plays to its end with no
            // loop, then the seamless full-live handoff to mpvpaper-B — same effect as a manual /next,
            // just timed to the video's end. (Transitions OFF keeps the mpv-internal gapless append below.)
            var tcfgVV = TransitionService.CurrentConfig();
            if (tcfgVV.Enabled)
            {
                if (remaining != null)
                {
                    try
                    {
                        while (true)
                        {
                            _speedChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                            var rem2 = TryQueryTimeRemaining();
                            if (rem2 == null) break;
                            // Lead = effect duration + warmup estimate + slack. The lead is NOT the accuracy
                            // mechanism — it only needs to fire EARLY: the renderer gates the effect start on
                            // A's DECODED remaining (--align-a-end, requested below) and spends any early
                            // slack at p=0 showing live A (invisible), so the effect completes at A's EOF to
                            // within a frame regardless of warmup variance. Firing LATE is the only failure
                            // (gate opens instantly; A holds its last frame briefly — never rewinds, it runs
                            // loop-file=no), hence generous slack on top of the measured cover latency.
                            int coverMs = Math.Clamp((TransitionService.LastCoverMs > 0 ? TransitionService.LastCoverMs : 1300) + 1200, 1500, 6000);
                            // re-read the duration each iteration (not the arm-time capture) so a transition-
                            // duration change during the possibly-minutes-long wait applies live to this lead
                            int durNowMs = TransitionService.CurrentConfig().DurationMs;
                            int leadMs = Math.Clamp(durNowMs, 200, 20000) + coverMs;
                            var sleepMs = Math.Max(0, (int)(rem2.Value * 1000) - leadMs);
                            if (sleepMs <= 0) break;
                            var delayTask = Task.Delay(sleepMs, ct);
                            var done = await Task.WhenAny(delayTask, _speedChangeTcs.Task).ConfigureAwait(false);
                            if (ct.IsCancellationRequested) return;
                            if (done == delayTask) { try { await delayTask; } catch (OperationCanceledException) { return; } break; }
                            try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                        }
                    }
                    finally { _speedChangeTcs = null; }
                }
                lock (_lock)
                {
                    if (!_waitingForVideoEnd || ct.IsCancellationRequested) return;
                    _waitingForVideoEnd = false;
                    _waitCts = null;
                    // this switch is a video-END advance → the renderer must align p=1 to A's EOF
                    TransitionService.RequestAlignAEnd();
                    SwitchToFile(next, SettingsService.Load().BuildMpvOptions());
                    PostSwitch(next);
                }
                return;
            }
            if (remaining != null)
            {
                TrySendCommand("set", "loop-file", "no");
                TrySendCommand("loadfile", next, "append");

                try
                {
                    while (true)
                    {
                        _speedChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var rem2 = TryQueryTimeRemaining();
                        if (rem2 == null) break;
                        // Sleep for rem2 only — the +300ms buffer lives outside the loop so
                        // speed/volume can fire between them (at A's end, not 300ms after).
                        var sleepMs = Math.Max(0, (int)(rem2.Value * 1000));
                        var delayTask = Task.Delay(sleepMs, ct);
                        var done = await Task.WhenAny(delayTask, _speedChangeTcs.Task).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        if (done == delayTask)
                        {
                            try { await delayTask; }
                            catch (OperationCanceledException) { return; }
                            break;
                        }
                        // Speed changed — wait for mpv to process the IPC command before re-querying.
                        // Without this, playtime-remaining still reflects the old speed and we compute
                        // a sleep that's too short, causing the lock block to fire while A is still
                        // playing (loop-file=inf lands on A, playlist-clear removes B, A replays).
                        try { await Task.Delay(100, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                finally { _speedChangeTcs = null; }

                // A has just ended. Apply B's speed/volume now — before the 300ms buffer —
                // so B inherits the correct values from its first frame rather than from A.
                // Fresh reads cover mid-sleep changes to global or per-video overrides.
                // SetSpeed/SetVolume must be called outside _lock (they acquire it internally).
                {
                    var transSettings = SettingsService.Load();
                    var transVol = ReadVolumeOverride(next) ?? transSettings.Volume;
                    var transSpeed = ReadSpeedOverride(next) ?? transSettings.Speed;
                    SetVolume(transVol);
                    SetSpeed(transSpeed);
                }

                // 300ms buffer: let mpv complete the A→B playlist advance before the lock
                // block sends loop-file=inf (arriving during the transition re-loops A).
                try { await Task.Delay(300, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                lock (_lock)
                {
                    if (!_waitingForVideoEnd || ct.IsCancellationRequested) return;
                    _waitingForVideoEnd = false;
                    _waitCts = null;

                    var settings = SettingsService.Load();
                    TrySendCommand("set", "loop-file", settings.Loop ? "inf" : "no");
                    TrySendCommand("playlist-clear");

                    OnWallpaperChanged?.Invoke(next);
                    PostSwitch(next);
                }
                return;
            }
        }

        // Video→scene: launch LWE SceneTransitionDelayMs before the video ends so the
        // scene is already rendering when mpvpaper is killed (matches normal transition behaviour).
        if (nextIsScene && !prevIsScene)
        {
            var remaining = TryQueryTimeRemaining();
            // Same socket-not-ready retry as the video→video path: in advance-on-end mode
            // this task may fire immediately after SwitchToFile(video) before mpv is up.
            if (remaining == null && _advanceOnVideoEnd)
            {
                for (int i = 0; i < 50 && remaining == null && !ct.IsCancellationRequested; i++)
                {
                    try { await Task.Delay(100, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    remaining = TryQueryTimeRemaining();
                    // Socket is up but no file is playing (e.g. a short video ended before we
                    // got here due to the transition buffer). Switch to the scene immediately.
                    // Require at least 30 retries (3s) before breaking — the socket can appear
                    // within ~100ms of launch while playtime-remaining is still null because the
                    // file hasn't finished loading yet (Launch's readyTcs waits up to 2s for the
                    // first valid reading). Breaking too early causes a premature scene switch
                    // that makes the video appear to play for only a fraction of a second.
                    if (remaining == null && File.Exists(IpcSocket) && i >= 30) break;
                }
            }
            if (remaining != null)
            {
                try
                {
                    while (true)
                    {
                        _speedChangeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var rem2 = TryQueryTimeRemaining();
                        if (rem2 == null) break; // video already ended, launch scene now
                        // Lead by the FULL transition wall-time so it COMPLETES at the video's natural end
                        // (the video plays to its end + the scene takes over exactly then → no loop mid-
                        // transition). V→S transition = reveal-hold (SceneTransitionDelayMs) + effect duration;
                        // when no transition is active, just the scene-load lead (SceneTransitionDelayMs).
                        var sceneLoadMs = SettingsService.Load().SceneTransitionDelayMs;
                        var tcfg = TransitionService.CurrentConfig();
                        int delayMs = tcfg.Enabled
                            ? sceneLoadMs + Math.Clamp(tcfg.DurationMs, 200, 8000)
                            : sceneLoadMs;
                        var sleepMs = Math.Max(0, (int)(rem2.Value * 1000) - delayMs);
                        if (sleepMs <= 0) break;
                        var delayTask = Task.Delay(sleepMs, ct);
                        var done = await Task.WhenAny(delayTask, _speedChangeTcs.Task).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) return;
                        if (done == delayTask)
                        {
                            try { await delayTask; }
                            catch (OperationCanceledException) { return; }
                            break;
                        }
                        // Speed changed — wait for mpv to process the IPC command before re-querying.
                        try { await Task.Delay(100, ct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                    }
                }
                finally { _speedChangeTcs = null; }
            }
        }

        // Scene→video and scene→scene: remaining is null (LWE has no mpv socket) — switch immediately.
        lock (_lock)
        {
            if (!_waitingForVideoEnd || ct.IsCancellationRequested) return;
            _waitingForVideoEnd = false;
            _waitCts = null;
            SwitchToFile(next, SettingsService.Load().BuildMpvOptions());
            PostSwitch(next);
        }
    }

    private static void CancelVideoEndWait()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        // Restore loop so the current video doesn't advance to the queued file
        // in the gap between cancel and the following SwitchToFile call.
        // TryIpcSwitchToFile will set the correct value immediately after.
        TrySendCommand("set", "loop-file", "inf");
    }

    private static void ArmVideoEndWait(string next, bool prevIsVideo = true)
    {
        var cts = _waitCts = new CancellationTokenSource();
        _waitingForVideoEnd = true;
        Task.Run(() => DoVideoEndWait(next, cts.Token, prevIsVideo));
    }

    // After switching to a new wallpaper: arm the next video-end pre-fetch (advance-on-end)
    // or reset the countdown (timed interval). Always saves state.
    private static void PostSwitch(string path)
    {
        if (_advanceOnVideoEnd && !IsScenePath(path))
        {
            var next = AdvanceToNext();
            if (next != null) ArmVideoEndWait(next);
            // In combined mode (advance-on-end + real interval), also reset the fallback countdown
            // so the interval applies fresh to the newly started video.
            if (_timedInterval > TimeSpan.Zero)
                _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
        }
        else
            _timedRemainingMs = (long)_timedInterval.TotalMilliseconds;
        SaveTimedState();
    }

    private static void AdvanceAndLaunch()
    {
        // When _waitingForVideoEnd is true (both advance-on-end and wait-for-video-end
        // modes), _historyIndex is already pre-fetched one step ahead of the
        // currently-playing item. Use it directly; calling AdvanceToNext() would skip it.
        bool preIsFetched = _waitingForVideoEnd
            && _history != null && _historyIndex >= 0 && _historyIndex < _history.Count;
        CancelVideoEndWait();
        string? next = preIsFetched ? _history![_historyIndex] : AdvanceToNext();
        if (next != null) LaunchAndReset(next);
    }

    private static void StepBackAndLaunch()
    {
        // When _waitingForVideoEnd is true, _historyIndex is pre-fetched one step ahead.
        // A single decrement undoes the pre-fetch; a second reaches the actual previous.
        bool preIsFetched = _waitingForVideoEnd;
        CancelVideoEndWait();
        if (_history == null || _historyIndex <= 0) return;
        _historyIndex--;                          // undo pre-fetch (or go to prev in normal mode)
        if (preIsFetched && _historyIndex > 0)
            _historyIndex--;                      // go one further to the actual previous item
        LaunchAndReset(_history[_historyIndex]);
    }

    private static void RandomAndLaunch()
    {
        if (_timedPaths == null || _timedPaths.Count == 0) return;
        // In advance-on-end mode _historyIndex is pre-fetched; use the item before it
        // as "current" so we don't accidentally exclude the pending-next item.
        int playingIdx = PlayingHistoryIndex;
        var current = _history != null && playingIdx >= 0 && playingIdx < _history.Count
            ? _history[playingIdx]
            : null;
        CancelVideoEndWait();
        var pick = PickRandomExcluding(_timedPaths, current);
        if (_history != null)
        {
            _history.Add(pick);
            if (_history.Count > 100) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        LaunchAndReset(pick);
    }

    // Uniform pick from `pool` excluding `exclude`. No retries: shifts the
    // chosen index past the excluded one to keep the distribution flat.
    private static string PickRandomExcluding(IReadOnlyList<string> pool, string? exclude)
    {
        if (pool.Count == 1 || exclude == null) return pool[Random.Shared.Next(pool.Count)];
        int excludeIdx = -1;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i] == exclude) { excludeIdx = i; break; }
        if (excludeIdx < 0) return pool[Random.Shared.Next(pool.Count)];
        int pick = Random.Shared.Next(pool.Count - 1);
        if (pick >= excludeIdx) pick++;
        return pool[pick];
    }

    private static void DispatchPendingAction(string action)
    {
        switch (action)
        {
            case "next": AdvanceAndLaunch(); break;
            case "prev": StepBackAndLaunch(); break;
            case "random": RandomAndLaunch(); break;
            case "restart": RestartCurrentAndLaunch(); break;
            // defer: don't restart now — the next changeover's SwitchToFile becomes a cold relaunch (masked)
            case "soft-restart": _restartPending = true; break;
        }
    }

    public static void NextWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("next");
            return;
        }
        // Single-wallpaper sessions (single, random) — step the library.
        // mpv-native playlist mode falls through to playlist-next.
        if (TryStepLibrary(forward: true)) return;
        SendCommand("playlist-next");
    }

    public static void PreviousWallpaper()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("prev");
            return;
        }
        if (TryStepLibrary(forward: false)) return;
        SendCommand("playlist-prev");
    }

    // For single-wallpaper sessions (no playlist context), `next`/`prev`
    // steps through the library alphabetically. Wraps at the ends. Returns
    // false for mpv-native playlist sessions so the caller can fall through
    // to mpv's own `playlist-next`/`playlist-prev`.
    private static bool TryStepLibrary(bool forward)
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null) return false;
        if (session.IsTimedPlaylist || session.IsPlaylist) return false;
        if (session.Paths.Count == 0) return false;

        // Use LoadAll's native order so stepping mirrors the UI grid order
        // exactly (whatever filesystem order GUI displays).
        var library = LibraryService.LoadAll();
        if (library.Count == 0) return false;

        var current = session.Paths[0];
        int currentIdx = library.FindIndex(i => i.VideoPath == current);
        int newIdx = currentIdx < 0
            ? 0
            : forward
                ? (currentIdx + 1) % library.Count
                : (currentIdx - 1 + library.Count) % library.Count;

        var pickPath = library[newIdx].VideoPath;
        Apply(pickPath, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { Paths = [pickPath] };
        SettingsService.Save(settings);
        return true;
    }

    public static void UpdateTimedSettings(bool shuffle, int intervalSeconds, bool waitForVideoEnd = false, bool advanceOnVideoEnd = false)
    {
        lock (_lock)
        {
            if (_timedPaths == null && !LoadTimedState()) return;
            _timedShuffle = shuffle;
            var oldIntervalMs = (long)_timedInterval.TotalMilliseconds;
            _timedInterval = TimeSpan.FromSeconds(intervalSeconds);
            var newIntervalMs = (long)_timedInterval.TotalMilliseconds;
            var elapsed = Math.Max(0L, oldIntervalMs - _timedRemainingMs);
            _timedRemainingMs = elapsed >= newIntervalMs ? newIntervalMs : newIntervalMs - elapsed;

            bool advanceChanged = _advanceOnVideoEnd != advanceOnVideoEnd;
            bool waitChanged = _waitForVideoEnd != waitForVideoEnd;
            _waitForVideoEnd = waitForVideoEnd;

            // If advance-on-end is toggling, or waitForVideoEnd toggles while a
            // DoVideoEndWait is already in-flight, cancel and re-arm as needed.
            if (advanceChanged || (waitChanged && _waitingForVideoEnd))
            {
                // _historyIndex is pre-fetched one step ahead when _waitingForVideoEnd is true.
                // Step back to the actual playing item so AdvanceToNext() re-fetches correctly.
                if (_waitingForVideoEnd && _historyIndex > 0)
                    _historyIndex--;
                CancelVideoEndWait();
                _advanceOnVideoEnd = advanceOnVideoEnd;

                if (advanceOnVideoEnd && !IsLweRunning && _timedPaths!.Count > 1)
                {
                    var next = AdvanceToNext();
                    if (next != null)
                        ArmVideoEndWait(next);
                }
            }
            else
            {
                _advanceOnVideoEnd = advanceOnVideoEnd;
            }

            // Crossing into timed mode (interval 0→positive, e.g. pure advance-on-end → timed) needs the
            // interval timer started — it wasn't running while interval was 0. (Positive→0 leaves the
            // timer ticking harmlessly; the `_timedInterval > Zero` guard in Tick suppresses the advance.)
            if (_timedPaths!.Count > 1 && intervalSeconds > 0 && _playlistTimer == null)
                StartTimedTimer();

            SaveTimedState();
        }
    }

    // Live-apply a rotation change (interval / wait-for-video-end / advance-on-end) to the RUNNING timed
    // playlist with no replay — preserves the running shuffle + the proportional countdown. No-op when no
    // timed session is active (UpdateTimedSettings early-returns). Wired from /settings (global rotation,
    // when the session follows globals) + /playlist/state (per-playlist rotation, when it overrides).
    public static void ApplyRotationLive(int intervalSeconds, bool waitForVideoEnd, bool advanceOnVideoEnd)
        => UpdateTimedSettings(_timedShuffle, intervalSeconds, waitForVideoEnd, advanceOnVideoEnd);

    public static void Stop()
    {
        lock (_lock)
        {
            KillAll();
            SignalTimerStop();
            ClearPlaylistObserverPaths();
        }
        KillRestartDaemon();
        // any in-flight transition died with its players — a stale InProgress marker would delay the
        // NEXT session's first video-end sample (late transition on a short first clip)
        TransitionService.ClearInProgress();
    }

    // Re-apply the last saved session: timed playlist, mpv-native playlist,
    // or single video. For timed playlists, delegates to a detached daemon.
    public static void Restore()
    {
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null) return;

        if (settings.AutoMute)
            AudioMonitor.SpawnDetachedMonitor();

        if (session.IsTimedPlaylist && session.Paths.Count > 0)
            SpawnTimerDaemon();
        else if (session.IsPlaylist && session.Paths.Count > 0)
        {
            ApplyPlaylist(session.Paths, settings.BuildMpvPlaylistOptions(), session.Shuffle);
            SpawnTimerDaemon();
        }
        else if (session.Paths.Count > 0)
            Apply(session.Paths[0], settings.BuildMpvOptions());

        SpawnRestartDaemon();
    }

    // Pick a random video and apply it as a single wallpaper. If a timed
    // playlist is active, hands off to the timer owner via a pending action
    // so the daemon picks from its in-memory paths and resets the countdown.
    // Otherwise picks from the full library as a one-shot.
    public static void ApplyRandom()
    {
        if (IsTimedPlaylistActive())
        {
            WritePendingAction("random");
            return;
        }

        var settings = SettingsService.Load();
        var pool = LibraryService.LoadAll().Select(i => i.VideoPath).ToList();
        if (pool.Count == 0) return;

        var current = settings.LastSession?.Paths.FirstOrDefault();
        var pick = PickRandomExcluding(pool, current);
        Apply(pick, settings.BuildMpvOptions());
        settings.LastSession = new LastSession { IsRandom = true, Paths = [pick] };
        SettingsService.Save(settings);
    }

    // Owns the timed-playlist tick loop in a detached process. Resumes an
    // already-running session if possible; otherwise restarts it. Blocks
    // until the timer is signalled to stop.
    public static void RunTimerDaemon()
    {
        _daemonMode = true;
        var settings = SettingsService.Load();
        var session = settings.LastSession;
        if (session == null || session.Paths.Count == 0) return;

        // Live state takes precedence over LastSession flags. ApplyPlaylist can upgrade to
        // ApplyTimedPlaylist mid-call (scenes + AllowScenes) while the caller still saves
        // LastSession.IsPlaylist=true with AdvanceOnVideoEnd=true. Without this check the
        // daemon would attach a playlist observer to a timed playback and the timer would
        // never fire → scene stuck forever.
        bool timedActive = IsTimedPlaylistActive();

        if (session.IsTimedPlaylist || timedActive)
        {
            bool started = IsPlaying && ResumeTimedTimer();
            if (!started)
            {
                if (settings.ResumeFromLast && RestoreTimedPlaylist()) { }
                else if (session.IsTimedPlaylist)
                {
                    var paths = session.Shuffle
                        ? session.Paths.OrderBy(_ => Guid.NewGuid()).ToList()
                        : session.Paths;
                    ApplyTimedPlaylist(paths, settings.BuildMpvOptions(), session.Shuffle, session.TimedIntervalSeconds, session.WaitForVideoEnd, session.AdvanceOnVideoEnd);
                }
                // For the upgrade case (LastSession.IsPlaylist + timedActive) without resume,
                // there's no recorded TimedIntervalSeconds to reconstruct from — bail.
            }
        }
        else if (session.IsPlaylist)
        {
            // Reconnect the playlist-pos observer to the already-running mpvpaper
            // so per-video volume/speed overrides keep firing after GUI close.
            // Use the saved observer paths (post-shuffle order) if available.
            List<string> observerPaths = session.Paths;
            try
            {
                if (File.Exists(PlaylistObserverPathsPath))
                    observerPaths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PlaylistObserverPathsPath)) ?? session.Paths;
            }
            catch { }
            _daemonCts?.Dispose();
            _daemonCts = new CancellationTokenSource();
            StartPlaylistObserver(observerPaths);
        }
        else return;

        WriteTimerDaemonPid();
        try { DaemonToken.WaitHandle.WaitOne(); }
        finally { DeleteTimerDaemonPid(); }
    }

    public static void TogglePause()
    {
        // During a transition the visible/audible wallpaper IS the overlay (it decodes A/B itself;
        // mpvpaper-B is parked at frame 0 for the handoff). Toggle the overlay's freeze instead of
        // cycling mpvpaper — the renderer owns the pause state (stateless here → can't desync).
        if (Process.GetProcessesByName("lp-transition").Length > 0)
        {
            SendOverlayCtl("pause");
            return;
        }
        lock (_lock)
        {
            SendCommand("cycle", "pause");
            try
            {
                if (!File.Exists(TimedStatePath)) return;
                var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
                if (state == null) return;
                var updated = state with { TimerPaused = !state.TimerPaused, TimerStopped = false };
                var tmp = TimedStatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(updated));
                File.Move(tmp, TimedStatePath, overwrite: true);
            }
            catch { }
        }
    }

    public static void SendCommand(params object[] args)
    {
        var socketPath = IpcSocket;
        if (!File.Exists(socketPath)) return;
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 500;
            socket.ReceiveTimeout = 500;
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            var cmd = JsonSerializer.Serialize(new { command = args });
            socket.Send(Encoding.UTF8.GetBytes(cmd + "\n"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlayerHelper] SendCommand failed: {ex.Message}");
        }
    }

    // Automute-driven mute. Updates _autoMuted always; blocked from sending unmute if user has explicitly muted.
    public static void SetMute(bool mute)
    {
        _autoMuted = mute;
        _isMuted = mute || _userMuted;
        if (!mute && _userMuted) return;
        SendCommand("set_property", "mute", _isMuted);
        SendOverlayCtl(_isMuted ? "mute 1" : "mute 0");
        if (IsLweRunning) ApplyLweMute(_isMuted);
    }

    // User-initiated mute (keybind, UI toggle). Always applies; sets _userMuted so automute can't undo it.
    public static void SetUserMute(bool mute)
    {
        _userMuted = mute;
        _isMuted = mute || _autoMuted;
        try
        {
            var path = UserMuteStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (mute) File.WriteAllText(path, "true");
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        SendCommand("set_property", "mute", _isMuted);
        SendOverlayCtl(_isMuted ? "mute 1" : "mute 0");
        if (IsLweRunning) ApplyLweMute(_isMuted);
    }

    public static void SetVolume(int volume)
    {
        SendCommand("set_property", "volume", (double)volume);
        SendOverlayCtl($"vol {volume}");
        if (IsLweRunning) ApplyLweVolume(volume);
    }

    public static void SetSpeed(double speed)
    {
        SendCommand("set_property", "speed", speed);
        lock (_lock) { _currentSpeed = speed; }
        _speedChangeTcs?.TrySetResult(true);
    }

    // Apply a per-wallpaper override LIVE to the currently-playing wallpaper, seamlessly (no restart):
    // mpv set_property for video volume/speed, LWE for scene volume (scenes are 1× → speed no-op). No-op
    // unless `path` is what's playing. Effective = override ?? global, so clearing an override (index key
    // dropped because it == global) snaps the running wallpaper back to the global value. SetSpeed keeps
    // the timed-playlist cache (_currentSpeed) in sync, so later ticks/advances use the new value too.
    public static void ApplyOverrideLive(string path)
    {
        var cur = QueryCurrentPath();
        if (cur == null) return;
        try { if (Path.GetFullPath(cur) != Path.GetFullPath(path)) return; } catch { return; }
        var s = SettingsService.Load();
        SetVolume(LibraryService.ReadVolumeOverride(path) ?? s.Volume);
        if (!IsLweRunning) SetSpeed(LibraryService.ReadSpeedOverride(path) ?? s.Speed);
    }

    public static void SetLoop(bool loop) =>
        TrySendCommand("set", "loop-file", loop ? "inf" : "no");

    public static void SetPlaylistShuffle(bool shuffle) =>
        TrySendCommand(shuffle ? "playlist-shuffle" : "playlist-unshuffle");

    public static void SetVideoScale(string scale)
    {
        double panscan = scale == "fill" ? 1.0 : 0.0;
        SendCommand("set_property", "panscan", panscan);
    }

    // Apply CHANGED playback settings LIVE to the currently-playing wallpaper — no stop/replay/relaunch.
    // Diffs prev→cur so we only poke mpv/LWE for what actually changed (needlessly re-applying vf/hwdec/
    // cache re-inits the decoder → a visible stall). Volume/Speed/VideoScale/Normalization/AutoMute are
    // applied by the /settings handler; this covers Loop, NoAudio, VideoFps, DisableCache, Demuxer*, HwDec
    // (video, via mpv IPC properties) + NoAudio (scene, via LWE mute). All of these were formerly
    // launch-only (needed a stop/replay to take effect).
    public static void ApplyPlaybackSettingsLive(AppSettings prev, AppSettings cur, string? playingPath)
    {
        bool scene = IsLweRunning;
        bool mpvAlive = File.Exists(IpcSocket) && MpvpaperProcs().Length > 0;

        // NoAudio = mute the whole wallpaper's audio. Applies to a scene (LWE) OR a video (mpv).
        if (prev.NoAudio != cur.NoAudio)
        {
            if (scene)
            {
                // Scenes launch with the audio device OPEN (primary at --volume 0 when NoAudio, never
                // --silent) so this toggles LIVE. Hold the new mute/volume via lp-audio (event-driven →
                // survives LWE's constant stream RE-CREATION, which resets a one-shot pactl — the whole
                // reason lp-audio exists). Muting keeps the hold (persists until the next switch's
                // AudioCtl("clear")); unmuting holds briefly then releases so steady-state / later volume
                // changes aren't pinned. pactl too, as the lp-audio-absent fallback.
                bool m = cur.NoAudio || _isMuted || AudioMonitor.IsMuted;
                int vol = (playingPath != null ? ReadVolumeOverride(playingPath) : null) ?? cur.Volume;
                var pids = GetLweProcPids();
                if (pids.Count > 0)
                {
                    AudioCtl("clear");
                    AudioCtl($"set {PidCsv(pids)} {vol} {(m ? 1 : 0)}");
                    if (!m) Task.Run(async () => { await Task.Delay(700); AudioCtl("clear"); });
                }
                ApplyLweMute(m);
                if (!m) ApplyLweVolume(vol);
            }
            else if (mpvAlive)
            {
                // mpv revives/kills the audio track live: `aid auto` re-inits the ao even after a
                // --no-audio launch (verified). Off→on restores the effective volume + any active mute.
                TrySendCommand("set", "aid", cur.NoAudio ? "no" : "auto");
                if (!cur.NoAudio)
                {
                    SetVolume((playingPath != null ? ReadVolumeOverride(playingPath) : null) ?? cur.Volume);
                    if (_isMuted || AudioMonitor.IsMuted) SendCommand("set_property", "mute", true);
                }
            }
        }

        if (!mpvAlive) return; // the rest are video-only mpv decode/buffer properties

        // Loop: match the displayed video's loop-file. Only for a SINGLE (non-timed) video — the timed
        // machinery owns loop-file (loadfile-append during advance-waits, per-item loop while displayed),
        // so re-asserting it under a running playlist would break the advance / stop the clip early.
        if (prev.Loop != cur.Loop && !_waitingForVideoEnd && !IsTimedPlaylistActive())
            SetLoop(cur.Loop);
        if (prev.VideoFps != cur.VideoFps)
            TrySendCommand("set", "vf", cur.VideoFps > 0 ? $"fps={cur.VideoFps}" : "");
        if (prev.DisableCache != cur.DisableCache)
            TrySendCommand("set", "cache", cur.DisableCache ? "no" : "auto");
        if (prev.DemuxerMaxBytes != cur.DemuxerMaxBytes)
            TrySendCommand("set", "demuxer-max-bytes", $"{cur.DemuxerMaxBytes}MiB");
        if (prev.DemuxerMaxBackBytes != cur.DemuxerMaxBackBytes)
            TrySendCommand("set", "demuxer-max-back-bytes", $"{cur.DemuxerMaxBackBytes}MiB");
        if (prev.HwDec != cur.HwDec)
            TrySendCommand("set", "hwdec", string.IsNullOrWhiteSpace(cur.HwDec) ? "no" : cur.HwDec);
    }

    private static void StartPlaylistObserver(IReadOnlyList<string> videoPaths)
    {
        _observerCts?.Cancel();
        _observerCts?.Dispose();
        var cts = _observerCts = new CancellationTokenSource();
        var paths = videoPaths.ToArray();
        _currentObserverPaths = [.. paths];
        Task.Run(() => ObservePathAsync(paths, cts.Token));
    }

    private static void StopPlaylistObserver()
    {
        _observerCts?.Cancel();
        _observerCts?.Dispose();
        _observerCts = null;
    }

    private static async Task ObservePathAsync(string[] videoPaths, CancellationToken ct)
    {
        var socketPath = IpcSocket;
        for (int i = 0; i < 50 && !File.Exists(socketPath) && !ct.IsCancellationRequested; i++)
        {
            try { await Task.Delay(100, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        if (ct.IsCancellationRequested || !File.Exists(socketPath)) return;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
            using var ns = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(ns, Encoding.UTF8);
            using var writer = new StreamWriter(ns, Encoding.UTF8) { AutoFlush = true };
            using var reg = ct.Register(() => { try { socket.Close(); } catch { } });

            await writer.WriteLineAsync("{\"command\":[\"observe_property\",1,\"playlist-pos\"]}").ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("event", out var ev) || ev.GetString() != "property-change") continue;
                    if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Number) continue;
                    var pos = data.GetInt32();
                    if (pos >= 0 && pos < videoPaths.Length)
                    {
                        ApplyOverridesForPath(videoPaths[pos]);
                        OnWallpaperChanged?.Invoke(videoPaths[pos]);
                    }
                }
                catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static void ApplyOverridesForPath(string path)
    {
        var settings = SettingsService.Load();
        var vol = ReadVolumeOverride(path) ?? settings.Volume;
        var spd = ReadSpeedOverride(path) ?? settings.Speed;
        SetVolume(vol);
        SetSpeed(spd);
    }


    // Adjust volume by `delta` (clamped 0-100). Updates the persisted setting
    // so subsequent launches and the GUI slider reflect the change, and also
    // pushes to the running mpv via IPC for an immediate effect.
    public static void AdjustVolume(int delta)
    {
        var settings = SettingsService.Load();
        int newVolume = Math.Clamp(settings.Volume + delta, 0, 100);
        if (newVolume == settings.Volume) return;
        settings.Volume = newVolume;
        SettingsService.Save(settings);
        SetVolume(newVolume);
    }

    // Returns the next wallpaper path, extending history if needed.
    // The item the next timed advance WOULD pick, without advancing (for the video→scene lead). Within
    // a cycle the order is fixed (shuffle reshuffles only at the cycle end), so the next is deterministic
    // except at the shuffle wrap → null there (no early lead, falls back to the boundary advance).
    private static string? PeekNextTimed()
    {
        if (_history != null && _historyIndex < _history.Count - 1) return _history[_historyIndex + 1];
        var p = _timedPaths;
        if (p == null || p.Count == 0) return null;
        int ni = _timedIndex + 1;
        if (ni < p.Count) return p[ni];
        return _timedShuffle ? null : p[0];
    }

    private static string? AdvanceToNext()
    {
        if (_history != null && _historyIndex < _history.Count - 1)
        {
            _historyIndex++;
            return _history[_historyIndex];
        }

        var p = _timedPaths;
        if (p == null) return null;

        for (int attempt = 0; attempt < p.Count; attempt++)
        {
            _timedIndex++;
            if (_timedIndex >= p.Count)
            {
                if (_timedShuffle)
                {
                    var last = p[p.Count - 1];
                    List<string> reshuffled;
                    do { reshuffled = p.OrderBy(_ => Guid.NewGuid()).ToList(); }
                    while (p.Count > 1 && reshuffled[0] == last);
                    _timedPaths = p = reshuffled;
                }
                _timedIndex = 0;
            }

            var path = p[_timedIndex];
            if (IsSkippedPath(path)) continue;

            if (_history != null)
            {
                _history.Add(path);
                if (_history.Count > 100) _history.RemoveAt(0);
                _historyIndex = _history.Count - 1;
            }
            return path;
        }

        return null;
    }

    private static Process? Launch(string mpvOptions, string file, TaskCompletionSource<bool>? readyTcs = null)
    {
        var socketPath = IpcSocket;
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        if (File.Exists(socketPath)) File.Delete(socketPath);

        var options = $"{mpvOptions} --input-ipc-server={socketPath}";
        var psi = new ProcessStartInfo("setsid")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("mpvpaper");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(options);
        psi.ArgumentList.Add("*");
        psi.ArgumentList.Add(file);
        var process = Process.Start(psi);
        process?.BeginOutputReadLine();
        process?.BeginErrorReadLine();
        if (readyTcs != null)
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    await Task.Delay(50).ConfigureAwait(false);
                    if (TryQueryTimeRemaining() != null)
                    {
                        readyTcs.TrySetResult(true);
                        return;
                    }
                }
            });
        }
        ReapExcessMpvpaper(2); // cap orphaned/excess mpvpaper (see method) → never OOM the GPU
        return process;
    }

    // mpvpaper's process NAME varies by packaging — on NixOS it's ".mpvpaper-wrapped" (a nix
    // wrapper), NOT "mpvpaper", so Process.GetProcessesByName("mpvpaper") returns EMPTY there →
    // every kill / is-alive check silently no-ops → mpvpaper piles up → VRAM OOM → GPU crash storm.
    // (This was THE leak on NixOS.) Match by SUBSTRING so wrapped names on any distro are caught.
    private static Process[] MpvpaperProcs() =>
        Process.GetProcesses()
            .Where(p => { try { return p.ProcessName.Contains("mpvpaper"); } catch { return false; } })
            .ToArray();

    // Cap mpvpaper instances. mpvpaper has a frame-buffer VRAM leak and is spawned DETACHED
    // (setsid, above) so instances OUTLIVE the backend — orphans from a dead/reloaded/crashed
    // backend (or a transition reap-failure) accumulate → VRAM OOM → every GL/Vulkan app SIGSEGVs
    // (the crash storm: kitty/dms/noctalia/clipboard). Keep the `keep` NEWEST (a full-live
    // transition legitimately runs A+B = 2); kill the older excess. Runs at the single spawn
    // chokepoint (Launch) → self-heals on every switch/restart, bounding mpvpaper at ≤keep.
    private static void ReapExcessMpvpaper(int keep = 2)
    {
        try
        {
            var procs = MpvpaperProcs()
                .OrderByDescending(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } })
                .ToList();
            for (int i = 0; i < procs.Count; i++)
                using (procs[i])
                    if (i >= keep) { try { procs[i].Kill(entireProcessTree: true); } catch { } }
        }
        catch { }
    }

    private static void KillCurrentProcess()
    {
        StopPlaylistObserver();
        foreach (var proc in MpvpaperProcs())
        {
            using (proc)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        // Kill any running transition overlay — it's a separate process (lp-transition) that decodes
        // A/B itself, so without this a Stop/Apply during a transition leaves the effect playing.
        // The video→video transition uses the IPC switch path (no KillCurrentProcess), so this never
        // kills an in-progress transition's own overlay — only Stop / Apply / cold-launch / scene do.
        foreach (var proc in Process.GetProcessesByName("lp-transition"))
            using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
        KillLweProcess();
        _current = null;
        var socketPath = IpcSocket;
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    private static void KillLweProcess()
    {
        try
        {
            if (File.Exists(LwePidPath))
            {
                foreach (var line in File.ReadAllLines(LwePidPath))
                {
                    if (!int.TryParse(line.Trim(), out int pid)) continue;
                    try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); }
                    catch { }
                }
                File.Delete(LwePidPath);
            }
        }
        catch { }
        // Same wrapped/truncated-name issue as mpvpaper: "linux-wallpaperengine" exceeds Linux's
        // 15-char comm limit (ProcessName = comm), so on Nix it truncates to ".linux-wallpape"
        // (no trailing 'r') — GetProcessesByName / Contains("wallpaper") both MISS it. Match the
        // truncation-safe substring "wallpape" (present in ".linux-wallpape" AND "linux-wallpaper";
        // no false hit — livepaper/mpvpaper/hyprpaper lack it). PID-file path above is primary.
        foreach (var proc in Process.GetProcesses()
                     .Where(p => { try { return p.ProcessName.Contains("wallpape"); } catch { return false; } }))
            try { proc.Kill(entireProcessTree: true); } catch { }
    }

    private static void ClearTimedStateFile()
    {
        try { File.Delete(TimedStatePath); } catch { }
    }

    private static void ClearPlaylistObserverPaths()
    {
        try { File.Delete(PlaylistObserverPathsPath); } catch { }
    }

    private static void SignalTimerStop()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(TimedStatePath)) return;
                var state = JsonSerializer.Deserialize<TimedState>(File.ReadAllText(TimedStatePath));
                if (state == null) return;
                var updated = state with { TimerStopped = true };
                var tmp = TimedStatePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(updated));
                File.Move(tmp, TimedStatePath, overwrite: true);
            }
            catch { }
        }
    }

    // State-only teardown (timer state, history, pending action). Does NOT
    // touch mpvpaper — callers that want to start a new session can
    // IPC-switch the existing mpvpaper instead of killing it.
    private static void TeardownTimer()
    {
        _waitCts?.Cancel();
        _waitCts = null;
        _waitingForVideoEnd = false;
        _advanceOnVideoEnd = false;
        _prelaunchCts?.Cancel();
        _prelaunchCts = null;
        if (_prelaunchPidsToKill != null) { KillPids(_prelaunchPidsToKill); _prelaunchPidsToKill = null; }
        _playlistTimer?.Dispose();
        _playlistTimer = null;
        StopRestartTimer();
        _restartPending = false; // don't carry a deferred restart into the next session
        _timedPaths = null;
        _history = null;
        _historyIndex = -1;
        _timedTimerPaused = false;
        _timedTimerStopped = false;
        _timedRemainingMs = 0;
        ConsumePendingAction();
    }

    private static void KillAll()
    {
        TeardownTimer();
        KillCurrentProcess();
    }

    public static bool IsLweAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("which")
            {
                Arguments = "linux-wallpaperengine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    // A scene's library path is its item FOLDER (workshop/<id> or local/<id> — a numeric-id dir, no
    // file extension); a video/image path always ends in a media extension. So "no extension" ⟺ scene.
    public static bool IsScenePath(string path) =>
        !string.IsNullOrEmpty(path) && Path.GetExtension(path).Length == 0;

    private static bool IsSkippedPath(string path)
    {
        if (!IsScenePath(path)) return false;
        var s = SettingsService.Load();
        return !s.AllowScenes || !IsLweAvailable();
    }

    // Spawns one linux-wallpaperengine process per monitor, writes new PIDs to
    // LwePidPath, and returns the new PID strings. Does NOT kill any existing
    // LWE processes — callers are responsible for killing old ones.
    // audioVolumePercent: the LWE --volume to launch at. A scene-transition launch passes 0 so LWE
    // starts SILENT (no blast before the crossfade's pactl ramp can clamp the sink-input — LWE plays
    // at its launch volume the instant its stream appears, ~0.5s before pactl can list/control it).
    // Per-output cache path for LWE's own --screenshot dump (the scene's framebuffer, written at
    // launch). The transition uses it for scene-A instead of grim → no compositor windows in the cover.
    internal static string SceneShotPath(string output) => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "livepaper", $"lpscene-{output.Replace('/', '_').Replace(' ', '_')}.png");

    private static string[] SpawnLweProcesses(string workshopId, AppSettings settings, int audioVolumePercent = 100)
    {
        var pids = new List<string>();
        bool anyPrimary = settings.LweMonitors.Any(m => m.IsPrimary);

        foreach (var monitor in settings.LweMonitors)
        {
            if (string.IsNullOrWhiteSpace(monitor.Name)) continue;
            var psi = new ProcessStartInfo("setsid")
            {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
            };
            psi.ArgumentList.Add("linux-wallpaperengine");
            psi.ArgumentList.Add("--noautomute");
            psi.ArgumentList.Add("--screen-root");
            psi.ArgumentList.Add(monitor.Name);

            bool hasAudio = !anyPrimary || monitor.IsPrimary;
            if (!hasAudio)
                psi.ArgumentList.Add("--silent"); // secondary monitors never carry scene audio (launch-only)
            else
            {
                // Always OPEN the audio device on the primary — even when NoAudio — so NoAudio toggles
                // LIVE (--silent would give no sink-input to unmute → a relaunch). NoAudio → launch at
                // volume 0 (silent from frame 0, no lp-audio/pactl dependency); ApplyLweVolume brings it
                // back with no relaunch.
                psi.ArgumentList.Add("--volume");
                psi.ArgumentList.Add((settings.NoAudio ? 0 : audioVolumePercent).ToString());
            }

            if (monitor.Fps > 0)
            {
                psi.ArgumentList.Add("--fps");
                psi.ArgumentList.Add(monitor.Fps.ToString());
            }
            psi.ArgumentList.Add("--no-fullscreen-pause");
            // match the video VideoScale so scenes frame like videos (fill = cover, fit = letterbox)
            psi.ArgumentList.Add("--scaling");
            psi.ArgumentList.Add((settings.VideoScale ?? "fill").Equals("fit", StringComparison.OrdinalIgnoreCase) ? "fit" : "fill");
            // self-dump a windowless frame (~0.5s after start) → the transition's scene-A cover uses it
            // instead of grim (which composites whatever windows are open over the wallpaper).
            try { Directory.CreateDirectory(Path.GetDirectoryName(SceneShotPath(monitor.Name))!); } catch { }
            psi.ArgumentList.Add("--screenshot");
            psi.ArgumentList.Add(SceneShotPath(monitor.Name));
            psi.ArgumentList.Add("--screenshot-delay");
            psi.ArgumentList.Add("30");
            psi.ArgumentList.Add(workshopId);

            var proc = Process.Start(psi);
            if (proc != null)
            {
                pids.Add(proc.Id.ToString());
            }
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LwePidPath)!);
            File.WriteAllLines(LwePidPath, pids);
        }
        catch { }
        return [.. pids];
    }

    // ffprobe a video's duration (seconds), 0 on failure — wraps the reveal-a pre-buffer seek for loops.
    private static double ProbeDurationSec(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe")
            {
                ArgumentList = { "-v", "error", "-show_entries", "format=duration", "-of", "default=nw=1:nk=1", path },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return double.TryParse(o, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        }
        catch { return 0; }
    }

    private static int LaunchScene(string workshopId, AppSettings settings)
    {
        KillLweProcess();
        var before = GetLweProcPids();
        int n = SpawnLweProcesses(workshopId, settings).Length;
        // direct (non-transition) scene launch — hold the new scene at its target (muted when NoAudio /
        // user / auto mute) via lp-audio, countering the stream-restore poison. NoAudio → target 0 (the
        // device is open, just silent) so NoAudio can be toggled back on LIVE.
        CorrectSceneVolume(before, settings.NoAudio ? 0 : settings.Volume,
            settings.NoAudio || _isMuted || AudioMonitor.IsMuted, 3000);
        return n;
    }

    private static string[] ReadCurrentLwePids()
    {
        try { return File.Exists(LwePidPath) ? File.ReadAllLines(LwePidPath) : []; }
        catch { return []; }
    }

    private static void KillPids(string[] pids)
    {
        foreach (var pidStr in pids)
        {
            if (!int.TryParse(pidStr.Trim(), out int pid)) continue;
            try { using var p = Process.GetProcessById(pid); p.Kill(entireProcessTree: true); } catch { }
        }
    }

    // Kills only mpvpaper processes without touching LWE — used in pre-launch
    // transitions where LWE is being kept alive until the new process is ready.
    private static void KillMpvPaperOnly()
    {
        foreach (var proc in MpvpaperProcs())
            using (proc) { try { proc.Kill(entireProcessTree: true); } catch { } }
        _current = null;
        var socketPath = IpcSocket;
        try { if (File.Exists(socketPath)) File.Delete(socketPath); } catch { }
    }

    internal static HashSet<string> GetLweClientObjectIds()
    {
        var psi = new ProcessStartInfo("pactl")
        {
            Arguments = "list clients",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();

        var ids = new HashSet<string>();
        foreach (var block in output.Split("Client #", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!block.Contains("application.process.binary = \"linux-wallpaperengine\"")) continue;
            foreach (var line in block.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("object.id = \""))
                {
                    ids.Add(t.Substring("object.id = \"".Length).TrimEnd('"'));
                    break;
                }
            }
        }
        return ids;
    }

    // Live LWE process PIDs straight from the process table (pgrep). Unlike the client→pid map this
    // never transiently misses a process whose PA client is mid-(re)registration, so it's the reliable
    // basis for the old-vs-new scene split in the crossfade (the client map can briefly drop a client).
    private static HashSet<int> GetLweProcPids()
    {
        var set = new HashSet<int>();
        try
        {
            var psi = new ProcessStartInfo("pgrep")
            {
                ArgumentList = { "-f", "linux-wallpaperengine" },
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(line.Trim(), out int p)) set.Add(p);
        }
        catch { }
        return set;
    }

    private static List<int> GetLweSinkInputIds()
    {
        try
        {
            var lweClientIds = GetLweClientObjectIds();
            if (lweClientIds.Count == 0) return new List<int>();

            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = "list sink-inputs",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();

            var ids = new List<int>();
            foreach (var block in output.Split("Sink Input #", StringSplitOptions.RemoveEmptyEntries))
            {
                string? clientId = null;
                bool isBuffer = false;
                foreach (var line in block.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith("client.id = \""))
                        clientId = t.Substring("client.id = \"".Length).TrimEnd('"');
                    else if (t.StartsWith("media.name = \"") && t.Contains("buffer://"))
                        isBuffer = true;
                }
                if (clientId == null || !lweClientIds.Contains(clientId) || isBuffer) continue;
                var firstLine = block.Split('\n')[0].Trim();
                if (int.TryParse(firstLine, out int id))
                    ids.Add(id);
            }
            return ids;
        }
        catch { return new List<int>(); }
    }

    private static void ApplyLweVolume(int volume)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-volume {id} {volume}%");
    }

    private static void ApplyLweMute(bool mute)
    {
        foreach (var id in GetLweSinkInputIds())
            RunPactl($"set-sink-input-mute {id} {(mute ? 1 : 0)}");
    }

    private static void RunPactl(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("pactl")
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
        }
        catch { }
    }

    // Per-item overrides live in the source index (LibraryStore). Delegate to LibraryService so EVERY
    // playback path (initial launch, playlist advance, transitions, scene launch, option baking) reads the
    // same source of truth the UI/endpoints write.
    private static int? ReadVolumeOverride(string path) => LibraryService.ReadVolumeOverride(path);

    private static double? ReadSpeedOverride(string path) => LibraryService.ReadSpeedOverride(path);

    private static string BakeVolumeOverride(string options, string path)
    {
        var vol = ReadVolumeOverride(path);
        if (!vol.HasValue) return options;
        if (options.Contains("--no-audio")) return options;
        const string prefix = "--volume=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && char.IsDigit(options[end])) end++;
            return options[..idx] + prefix + vol.Value + options[end..];
        }
        return options + $" {prefix}{vol.Value}";
    }

    private static string BakeSpeedOverride(string options, string path)
    {
        var speed = ReadSpeedOverride(path);
        if (!speed.HasValue) return options;
        string val = speed.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture);
        const string prefix = "--speed=";
        int idx = options.IndexOf(prefix, StringComparison.Ordinal);
        if (idx >= 0)
        {
            int end = idx + prefix.Length;
            while (end < options.Length && (char.IsDigit(options[end]) || options[end] == '.')) end++;
            return options[..idx] + prefix + val + options[end..];
        }
        return options + $" {prefix}{val}";
    }

    // The loudnorm audio filter for an item's between-video normalization, or null when it shouldn't
    // apply (normalization off / muted / scene / not yet measured). When enabled but unmeasured, kicks
    // off a background measure so it's ready next time (never blocks the switch). Applied as an mpv `af`,
    // so the per-item/global `volume` (which mpv applies AFTER the filter chain) stacks on top.
    private static string? NormalizeAf(string path)
    {
        var s = SettingsService.Load();
        if (!s.NormalizeAudio || s.NoAudio || IsScenePath(path)) return null;
        var af = LibraryService.LoudnormAf(path, s.NormalizeTargetLufs);
        if (af == null) { _ = LibraryService.EnsureLoudnessAsync(path); return null; } // measure for next time
        return af;
    }

    // Append the normalization filter to a launch options string (mpvpaper -o "…"). last --af wins.
    private static string BakeNormalization(string options, string path)
    {
        var af = NormalizeAf(path);
        return af == null ? options : options + $" --af={af}";
    }

    // Live-apply normalization to the currently-playing video (toggle/retune takes effect without a
    // switch). null/scene/no-socket → no-op. Unmeasured → clears the filter + kicks a background measure.
    public static void ApplyNormalizationLive(string? path)
    {
        if (path == null || IsScenePath(path) || !File.Exists(IpcSocket)) return;
        TrySendCommand("set", "af", NormalizeAf(path) ?? "");
    }
}
