---
paths:
  - "src/livepaper/Models/AppSettings.cs"
  - "src/livepaper/Models/LastSession.cs"
  - "src/livepaper/Models/CustomPlaylist.cs"
---

## Settings & Session Persistence

`AppSettings` (JSON at `~/.config/livepaper/settings.json`):
- Playback: `Loop`, `NoAudio`, `DisableCache` (default **false**), `Volume` (0–100), `Speed` (0.1–4.0, default 1.0). Per-item volume/speed overrides live in the library `index.json`, not here (see `library.md`).
- Memory: `DemuxerMaxBytes`, `DemuxerMaxBackBytes` (int, MiB)
- `HwDec`: `"auto"` | `"nvdec"` | `"vaapi"` | `"no"`
- `VideoScale`: `"fill"` (panscan=1.0) | `"fit"` (panscan=0.0); `VideoFps` (0 = native, else caps video playback fps — scenes/LWE unaffected). VideoScale applies **live** (mpv `panscan` via IPC, incl. on advance). **All playback settings apply live now** (`PlayerHelper.ApplyPlaybackSettingsLive`, diffed): `Loop` (loop-file, single video only — the timed machinery owns it under a playlist), `NoAudio` (mpv `aid` / scene LWE mute), `VideoFps` (mpv `vf=fps`), `DisableCache` (`cache`), `DemuxerMax*Bytes`, `HwDec`. Only `LweMonitors` (per-monitor fps/primary) is genuinely launch-only (LWE has no live reconfigure).
- Auto-mute: `AutoMute` (false), `AutoMuteDelayMs` (200), `AutoUnmuteDelayMs` (2000), `AutoMuteThresholdDb` (-70.0), `AutoMuteOnlyIfMprisActive` (false)
- Global rotation: `GlobalIntervalSeconds` (1800), `GlobalAdvanceOnVideoEnd` (false), `GlobalWaitForVideoEnd` (false)
- Restart: `RestartIntervalSeconds` (default 600, min 5, max 3600, clamped in setter; 0 = off); `RestartOnSwitchOnly` (false) — defer the mpvpaper leak-restart to the next playlist changeover (advanced)
- Wallpaper Engine: `WallpaperEnginePath`, `WeCopyFiles`, `ResumeFromLast`, `AllowScenes` (false), `AutoImportWallpaperEngine` (false), `ReplaceDirectWithWorkshop` (false) — swap a direct-DL `workshop/` copy for a `local/` WE-dir symlink when it appears
- Library automation: `AutoAddLibraryToPlaylist` (false) — auto-add new library items to active playlist strip
- LWE: `LweSilent` (false), `LweVolume` (100), `LweMonitors` (`{Name, Fps, IsPrimary}[]`), `SceneTransitionDelayMs` (1000)
- UI: `ThumbnailAspect` ("Default"), `CardSize` ("Medium"), `LibrarySortIndex` (5 = newest first), `Theme` ("Catppuccin Mocha")
- Advanced/UI toggles: `AdvancedSettings` (false — reveal power-user rows), `WallpaperBgAllTabs` (false — play the live wallpaper behind Browse/Library too), `DebugMode` (false — `lpdbg` bridge + metrics), `DebugOverlay` (true — on-screen debug HUD when DebugMode is on)
- `LastSession`: tracks last applied mode for `--restore`

`CustomPlaylist` (JSON at `~/.config/livepaper/playlist_state.json` for in-progress; named files in `~/.local/share/livepaper/playlists/`):
- `VideoPaths`, `Name` (nullable; only set after Save or Load)
- `Settings.Order` (Sequential/Shuffle), `Settings.OverrideGlobalSettings`, `Settings.IntervalSeconds`, `Settings.AdvanceOnVideoEnd`, `Settings.WaitForVideoEnd`
- `IntervalSeconds`/`AdvanceOnVideoEnd`/`WaitForVideoEnd` only used when `OverrideGlobalSettings` is true.

`LastSession` model fields:
- `IsPlaylist` — Play All session; `IsTimedPlaylist` — timed playlist; `IsRandom` — `--random` session
- `Paths` — path(s) used; `Shuffle` — shuffle was on
- `TimedIntervalSeconds`, `WaitForVideoEnd`, `AdvanceOnVideoEnd`, `OverrideGlobalSettings` — restores exact interval/mode rather than current globals

**`LastSession` upkeep:** the `/settings` POST **preserves** `LastSession` from disk (the UI re-POSTs a cached settings blob that omits it — clobbering would lose it). `LastSession` is (re)written by `AppOps` on `apply`/`random`/`next`/`PlayPlaylist`; `--restore` replays whatever it last held.

## Timed Playlist

`PlayerHelper.ApplyTimedPlaylist`: `System.Threading.Timer` ticks every 100ms. Decrement uses `DateTime.UtcNow` deltas (no jitter drift). On shuffle, `AdvanceToNext` re-randomizes at end of each cycle with do/while guarantee new cycle's first ≠ old cycle's last. State written to `~/.config/livepaper/timed_state.json` only on significant events (not every tick) — in-memory `_timedRemainingMs` is authoritative.

**Tick state sync**: each tick calls `RefreshSignals()` (re-reads only `TimerStopped`/`TimerPaused`). Owner's `_timedRemainingMs` / history untouched.

**`TimedState` record** fields: `TimerStopped`, `TimerPaused`, `AdvanceOnVideoEnd`, `WaitingForVideoEnd`. On restore, `LoadTimedState` restores `_advanceOnVideoEnd`/`_waitingForVideoEnd`, then `RearmAdvanceOnVideoEnd()` re-arms `DoVideoEndWait`. When `_waitingForVideoEnd=true`, `_historyIndex` is pre-fetched one step ahead — `RestoreTimedPlaylist` adjusts back by one before `SwitchToFile`, then calls `RearmAdvanceOnVideoEnd()`.

**Pending-action IPC** (`~/.config/livepaper/pending_action.txt`): atomic write+rename. Content: `next` / `prev` / `random` / `restart`. Timer owner's tick consumes (read+delete) → `AdvanceAndLaunch` / `StepBackAndLaunch` / `RandomAndLaunch` / `RestartCurrentAndLaunch`. `restart` cold-restarts current wallpaper without advancing and without resetting the countdown. All others call `LaunchAndReset` → kills mpvpaper, launches new, resets `_timedRemainingMs` to full interval.

**Stop/pause signals**:
- `Stop()` → `KillAll()` + `SignalTimerStop()`; daemon's stopped-branch also calls `KillCurrentProcess()` (covers kill→launch race)
- `TogglePause()` → `cycle pause` IPC + flips `TimerPaused`; tick freezes decrement while paused
- `NextWallpaper()`/`PreviousWallpaper()`/`ApplyRandom()` → write `pending_action.txt` if timed playlist active; else mpv `playlist-next`/`playlist-prev` IPC or one-shot random

**Timer ownership**: at most one process owns the timer.
- Daemon writes `timer.pid`; GUI writes `gui_timer.pid` on startup, deletes on close.
- `KillTimerDaemon()` kills orphan daemon (called on GUI startup and inside `SpawnTimerDaemon`).
- `SpawnTimerDaemon()` bails if `IsGuiTimerAlive()`.
- Window-close clears `gui_timer.pid` *before* calling `SpawnTimerDaemon` (enables handoff).

**`IsTimedPlaylistActive()`**: state-file check (`Paths.Count > 0 && !TimerStopped`). Survives brief kill→launch gap.

**`DoVideoEndWait` retry threshold**: 30 iterations (3 s) before breaking on `null` remaining. 500 ms threshold caused premature scene switches.

## WE Auto-Import

`LibraryService.SyncWallpaperEngine(workshopPath, allowScenes, weCopyFiles)` — headless scan of the WE workshop dir. Creates `local/<id>` symlinks + index entries; returns newly-added paths (a video file, or the scene **folder** — scenes are folders now, no `.scene` marker). Called:
- `--serve` startup: `RunHeadlessAutoSync()` off-thread in `ServerHost` (then broadcasts the `library-synced` WS event)
- `--restore`: `RunHeadlessAutoSync()` in `Program.cs` before `PlayerHelper.Restore()`

After sync, if `AutoAddLibraryToPlaylist` is true: `AddCardToPlaylist` for each new item, then `AppendToActivePlaylist` for mid-session injection, and `LastSession.Paths` updated + saved so next `--restore` includes them.
