---
paths:
  - "src/livepaper/Helpers/PlayerHelper.cs"
  - "src/livepaper/Helpers/AudioMonitor.cs"
  - "src/livepaper/Helpers/TransitionService.cs"
  - "src/native/lp-transition/**"
---

## Player

`PlayerHelper` is the single entry point for mpvpaper. Kills all existing mpvpaper processes before starting a new one.

**stdout/stderr redirection:**
- **mpvpaper** (`Launch`), **timer daemon** (`SpawnTimerDaemon`), **restart daemon** (`SpawnRestartDaemon`): `RedirectStandardOutput/Error = true`, `BeginOutputReadLine/BeginErrorReadLine` called — required to drain pipes and prevent deadlock.
- **LWE** (`SpawnLweProcesses`): `RedirectStandardOutput/Error = false` — do NOT add `BeginOutputReadLine/ErrorReadLine` here.

**IPC readiness**: after spawning mpvpaper, `Launch` polls `TryQueryTimeRemaining()` up to 40 × 50 ms (2 s total). `TryQueryTimeRemaining` splits the raw socket buffer on `\n` and tries to parse each line as JSON — mpv sometimes sends multiple responses in one read.

**Single video:**
```sh
mpvpaper -o "<mpv-options>" '*' /path/to/wallpaper.mp4
```
`'*'` targets all Wayland outputs.

**Playlist (Play All / Shuffle):**
- Shuffle **pre-applied** to path array in `ApplyPlaylist()` (not via `--shuffle`) so order is known for `playlist_observer_paths.json`
- Single video → `Launch(opts, path)` directly
- Multi-video (no scenes, or scenes with no interval) → `ApplyTimedPlaylist(intervalSeconds: 0, waitForVideoEnd: false, advanceOnVideoEnd: true)` — livepaper-owned; **no `--playlist=file` or `--loop-playlist=inf`**
- Scenes present + `AllowScenes` + interval > 0 → `ApplyTimedPlaylist(secs, waitForVideoEnd: true, advanceOnVideoEnd: true)`
- Multi-video playlists are livepaper-owned through the timed machinery (no `playlist.txt` / `--playlist=file`)

**`AppendToActivePlaylist(IReadOnlyList<string> paths)`** — injects new paths into a running session:
- Timed mode (`_timedPaths != null`): dedup-appends to `_timedPaths` in-memory. IPC `loadfile append` is handled naturally by advance-on-end pre-fetch.
- Non-timed mode: sends `loadfile append` IPC for each path.
- Called from `AppOps` (the web layer) after `AutoAddLibraryToPlaylist` adds new WE items mid-session.

## Scenes (folder-based) & live overrides

- **A scene's play path IS its item folder** (`workshop/<id>` / `local/<id>`) — no `.scene` marker. `IsScenePath(path)` = "the path has no media file extension" (a folder ⟺ scene). `SwitchToFile`'s scene branch launches LWE by **id** (local — LWE's native WE-dir lookup) or **dir path** (owned `workshop/` copy; derived via `LibraryStore.Locate`); `QueryCurrentSceneWorkshopId` = the folder name. Scenes have no mpv socket, so `QueryCurrentPath` returns the folder from `_history`, and `Apply` sets `_history=[path]` for single applies so a lone scene is trackable.
- **Per-item volume/speed come from the index.** `PlayerHelper`'s private `ReadVolumeOverride/ReadSpeedOverride` **delegate to `LibraryService`** (→ `LibraryStore`); effective = `override ?? global` on every path (launch, advance, transition, scene).
- **Live apply, no restart:** `ApplyOverrideLive(path)` retargets the *playing* wallpaper — mpv `set_property volume/speed` (video) or `ApplyLweVolume`→`pactl` (scene volume; scenes are 1× → speed no-op). `SetVideoScale` applies fill/fit live via mpv `panscan`. A video→video IPC switch (`TryIpcSwitchToFile`) carries the effective volume/speed as **`loadfile` per-file options** — atomic, so mpv can't reset volume to the launch default after the load (otherwise the advanced-to item played at global).

## WE-style transitions (`TransitionService` + `lp-transition`)

Animated effect between wallpaper switches, à la Wallpaper Engine. **Full-live** (video→video): BOTH
sides keep *playing* through the effect — the renderer decodes A (from its live position) and B (from
0) with libmpv, not two frozen stills. Frozen stills remain the warmup fallback + the only source for
scene sides.

**Methods + per-switch auto-fallback.** One picker (`TransitionMethod` = frozen|reveal|full-live);
`TryStart` resolves the *actual* method per switch from the A/B kinds. **Only a SCENE side is
constrained** — it can't be a live GL texture: a scene **A** → LWE `--screenshot` still (windowless; grim fallback) (`fromTex`), a scene **B**
→ revealed live underneath (transparent `toTex`). A **video** side is always decodable. So:

- **full-live** decodes each *video* side: **V→V** decode A + decode B (both textured → all effects).
- **S→V** → **reveal-a** (`--reveal-a`, transparent): the overlay decodes B **live** (plays + crossfades
  its own audio in) and reveals it over the **LIVE scene-A** underneath (transparent fromTex → compositor
  blends B over the live wallpaper). **No scene-A capture** (no grim/still → no stale frame, no baked-in
  windows). The B handoff can't copy V→V's trick (V→V plays mpvpaper-B *in sync* hidden under its OPAQUE
  cover; reveal-a's transparent cover would expose it), so: mpvpaper-B is launched **late + paused**, under
  the overlay's opaque **end-hold** (`--hold-end-ms`), **pre-buffered** near the handoff (`--start≈`
  effect+hold); at teardown the renderer **seeks it to overlay-B's exact frame** (`--mpv-seek-b`, hr-seek +
  a ~0.1s anticipation for the decode-settle) then unpauses → seam **≈ −0.03s** (a ~2-frame forward skip,
  never a repeat; measured `B-SEAM` in the renderer log vs V→V's 0.000). Reveal effects only (A is the
  transparent side). scene-A fades out via lp-audio across the effect+end-hold (no silence gap).
- A scene **B** → **reveal-b** (`--reveal-b`, transparent): **V→S** decode-A + reveal scene-B; **S→S**
  still-A + reveal scene-B (reveal effects only — B isn't textured).
- **frozen** + B=scene → **reveal** (a still-B can't match a continuously-animating scene → jump).
  frozen + video-B = both stills, all effects, B hands off paused frame0→play.
- **reveal** = still-A + live B revealed (reveal effects only).
- **effect gate**: when B ends up live-*revealed* (reveal / reveal-b / S→S) the effect must be
  reveal-capable (`IsRevealEffect`/`RevealEffects`); a warp there → **re-pick a reveal effect**
  (`PickRevealEffect`) so B keeps PLAYING (never freeze B on frame0); only a video-B with NO reveal
  effect enabled falls back to frozen. (full-live with a decoded B has no gate — all effects work.)

- **Assets** (`transitions/`, shared with the UI): `glsl/<id>.glsl` (gl-transitions bodies — `vec4 transition(vec2 uv)`), `manifest.json` (`id,name,category,defaultOn,author,license,uniforms[]`), `wrap.vert`/`wrap.frag.template`. Generated by `scripts/generate_transitions.py`. See `transitions/README.md`.
- **Renderer** (`src/native/lp-transition`, C): per-output `wlr-layer-shell` **BOTTOM** surface, opaque, input-transparent, EGL/GLES3. **Full-live:** `--from-video`/`--to-video` (+ `--from-start`/`--to-start`) → a **libmpv render-context per side per surface** decodes into an FBO texture each frame (sampled as `fromTex`/`toTex`); `--from`/`--to` **raw RGBA** stills (ffmpeg-normalized → no image lib) are the warmup fallback until the first decoded frame + the only source for a scene side. Effect runs over `--duration-ms` at display-refresh fps (composites at refresh even though video updates at its own rate — `BLOCK_FOR_TARGET_TIME=0`). **libmpv gotchas:** must set `vo=libmpv` (else mpv spins a real Vulkan VO → NVIDIA crash) and free `mpv_render_context` with the GL context current. Links `mpv` (pkg-config). Built by `install.sh` (no-ops if the wayland/EGL/mpv toolchain is absent).
- **`TransitionService`**: `Effective(settings, playlistSettings)` = override-or-global (mirrors `AppOps.Eff*`); `CurrentConfig()` reads `PlaylistService.LoadCurrentState()` + settings; `PickEffect` (shuffle, no-immediate-repeat, or sequential) + `PickDuration` (single or `Max>Min` range). `TryStart(from, fromScene, to, toScene, cfg)` captures both frames **per output** (video → ffmpeg @ mpv `time-pos`/0; scene-from → LWE's **`--screenshot` dump** `~/.cache/livepaper/lpscene-<output>.png` (the GPU framebuffer, **no compositor windows**) written at scene launch via `--screenshot … --screenshot-delay 30`, **grim only as fallback** when the dump is missing — grim composites whatever windows are open; scene-to → the item's library preview image), composes the shader, spawns `lp-transition` detached. Resolves `transitions/` + the binary via `LP_TRANSITIONS_DIR`/`LP_TRANSITION_BIN` (set by `install.sh`), else a repo-relative walk (dev).
- **Video-END alignment (`--align-a-end`, sub-frame)**: a timed video-end V→V advance (`DoVideoEndWait`
  → `TransitionService.RequestAlignAEnd()`, one-shot flag consumed by `TryStart`) makes the renderer
  end the effect exactly at A's EOF. It holds at p=0 (opaque live A — looks exactly like the wallpaper)
  and **anchors on a time-pos TICK**: at the instant A's time-pos advances, its true position is exact
  (a raw threshold check would be ±1 SOURCE frame, 17–42ms) → `go_time = (EOF_wall − duration −
  reveal_hold)`, an exact wall-clock target that can land between display frames; p's clamp holds 0
  until it, then p=1 lands at EOF (log `A-END: wall-err`; measured **+1.4–3.4ms** at 100/1000/10000ms
  + real wallpapers — under ½ display frame @144Hz, the physical floor). Re-anchored every tick until
  start. CLOSED-LOOP: the backend lead (`durationNow + LastCoverMs + slack`, duration re-read per
  iteration → live) only needs to fire EARLY — the gate spends the slack invisibly. Fired LATE (cover
  blowout) → immediate start. **Loop semantics mirror the desktop** (`--from-loop`/`--to-loop` =
  `settings.Loop`, exactly what mpvpaper runs with — timed / wait-for-video-end sessions included):
  a side **shorter than the effect** keeps LOOPING through it when its context loops (that's its
  normal on-screen look), and **freezes on its last frame** (`keep-open=yes`) when Loop is off —
  never rewinds a non-looping clip, never goes black. The aligned advance additionally runtime-pins A
  `loop-file=no` for a clip ≥ effect (at the GATE, not init — during warmup the visible mpvpaper-A
  still loops; a pre-pinned overlay-A would jump at cover) so a late fire ends in a freeze, not a
  rewind. Loop-off short B: overlay-B freezes at its last frame and the mpvpaper-B pre-seek clamps
  just short of EOF → seam 0.000 (verified).
  A target further than `A_HOLD_MAX` (8s — e.g. A wrapped during warmup after a very late fire) →
  start unaligned rather than hold a whole pass. The teardown pre-seek of mpvpaper-B is media-time =
  `duration × B-speed`, **wrapped by B's duration** (a raw `seek duration` past a short B's EOF clamps
  → up to a full clip of seam); the `B-SEAM` log is wrap-aware. Full-live V→V only (reveal/frozen
  freeze A at the cover — a still can't rewind). Overlay-B + mpvpaper-B stay paused through the gate
  so the handoff position stays = duration. `PlayerHelper.Stop()` calls
  `TransitionService.ClearInProgress()` — a stale InProgress marker would delay the next session's
  first video-end sample.
- **Hook = `SwitchToFile`** (the single switch chokepoint → every entry: apply, next/prev/random, timed advance, keybind `--action`). **Fires for video→video AND scene-involved switches.** Full-live handoff (video→video): B is loaded **unpaused** (`TryIpcSwitchToFile` with no `pauseAtStart`) so it plays LIVE under the opaque overlay and is revealed at teardown at a matching position (the overlay's own B decoder and mpvpaper's B both run from 0 → match within ~1 frame). Gated on the IPC path actually running (`mpvAlive && !_restartPending`) so a cold launch never gaps under the overlay. **Scene-involved switches**: the overlay covers (grim still / live-A) **inside the existing pre-launch crossover** — the crossover launches B (LWE / mpvpaper) UNDERNEATH (the overlay sits at layer **BOTTOM/level 1**, above LWE+mpvpaper at **BACKGROUND/level 0**, by level not creation order), so the LWE launch flash / player swap is hidden; `--reveal-hold-ms` (reuses `SceneTransitionDelayMs`) holds the cover while the slow LWE scene-B renders, then reveals. `fromPath` is a sentinel for scene-A (the LWE `--screenshot` dump / grim captures the on-screen scene regardless of path; the playlist history has already advanced so `QueryCurrentPath` returns the target). Frozen S→V launches mpvpaper-B **paused** (`--pause=yes` + the renderer's `--mpv-unpause`) → still-B frame0 hands off to live-B with no jump. `--scaling fill|fit` matches `VideoScale`.
- **Audio crossfade.** V→V full-live crossfades in the renderer (its two libmpv decoders, `audio_volume`). Scene-involved switches crossfade in the **backend** (mode-independent), synced 50/50 over the transition duration: OLD ramps X→0, NEW ramps 0→X, crossing at the midpoint; OLD is reaped when it hits 0 (replaces the fixed `SceneTransitionDelayMs` kill). The **video** side is faded via mpv `set_property volume` (new launched `--volume=0`). The **scene** side is faded by **`lp-audio`** — a small persistent libpulse helper (`src/native/lp-audio`, `LP_AUDIO_BIN`, built by `install.sh`): the backend pushes `set <pidcsv> <vol> <mute>` lines (`PlayerHelper.AudioCtl` → its stdin), and lp-audio applies the volume to that scene's sink-inputs **in-process on every PulseAudio sink-input new/change event** (~1ms). This is mandatory because shelling `pactl` per stream (~10-40ms) can't beat PA's default-volume-on-stream-create + the connect/disconnect volume resets LWE triggers → audible flashes. lp-audio maps **sink-input → client → process PID** (`application.process.id`; PID is stable across LWE's client/stream re-creation, unlike sink-input # / client.id) and matches against the backend's old/new **PID sets** (`GetLweProcPids`, snapshotted before/after launch). **It MUST skip re-applying a stream already at the wanted vol/mute** — else setting volume fires a `change` event → re-apply → infinite loop that storms+wedges PulseAudio (this was the root of both the residual flashes and recurring server hangs). During the cover the OLD is held at target + NEW muted+0; after the reap the held group keeps NEW at target (event-driven, no end dip); `AudioCtl("clear")` releases ~3s later so steady-state `ApplyLweVolume` resumes. No fade-in on the first wallpaper (`hasOld`). lp-audio's client→pid cache **evicts on PA `remove` + FIFO-recycles when full** — else it fills with dead LWE clients (LWE churns ~4/transition) and new scenes stop resolving → stuck at 0. **stream-restore poison:** PulseAudio continuously saves the (single, `application.name`-keyed) LWE app volume, so a transient fade value gets remembered and poisons the NEXT *fresh* launch (first item / restore / random — no crossfade to correct it). `CorrectSceneVolume` counters it: on a non-transition scene launch, hold the new scene at target via lp-audio (event-driven → overrides the restored value as the stream appears) ~3s, then release. (LWE forces its own `application.name` + pipewire ignores `module-stream-restore.id`, so the restore key can't be changed — correction is reactive.) Absent helper → scene audio just doesn't crossfade (plain switch). **Gap:** V→V *reveal/frozen* is a single mpvpaper `loadfile` (hard A→B switch) → no overlap crossfade there (V→V's real mode is full-live, which does).
- **Config**: `PlaylistSettings.Transition*` (per-playlist, gated by `OverrideGlobalSettings`) + `AppSettings.GlobalTransition*` (fallback). UI: `TransitionPicker` (live WebGL previews via `/transitions/frag|vert|preview`) opened from the playlist-settings modal + the global Settings "Rotation" section.

## Mute: user vs auto

**`SetMute(bool)`** — auto-mute path. If `!mute && (_userMuted || UserMuteStatePath exists)`, returns without unmuting.

**`SetUserMute(bool)`** — user action path. Writes/deletes `UserMuteStatePath`, updates `_userMuted`. `toggle-mute` CLI action uses this, not `cycle mute` IPC.

**`AudioMonitor.IsMuted`** — exposed so `PlayerHelper` can read mute state on LWE launch.

**`PlayerHelper.IsMuted`** — `public bool IsMuted => _isMuted` used by `Program.cs` for toggle-mute 3-way logic.

**Scene→video mute**: `SwitchToFile` bakes `--mute=yes` into `launchOpts` if `_isMuted` is true. Always read `_isMuted` fresh inside the task — never capture into a `bool` local before `Task.Run`, value goes stale.

**LWE orphan prevention**: `_prelaunchPidsToKill` (`private static string[]? _prelaunchPidsToKill`) holds LWE pids from an in-flight scene launch cancelled before the process started. `SwitchToFile` checks and kills these at entry. Also cleared in `TeardownTimer` and crash-detection block.

## Auto-Mute (`AudioMonitor`)

`AudioMonitor.Start/Stop` called by the `/settings` handler (`Web/ServerHost.cs`) when an AutoMute field changes; started on `--serve` startup, `Stop()`ped (+ handed to a detached `--monitor`) on `ApplicationStopping`. Three concurrent tasks:

1. **`WatchStreamsAsync`** — runs `pactl subscribe`; maintains `ConcurrentDictionary<uint, CancellationTokenSource>` per-stream. On `'new'`: starts `parec`, verifies non-mpv after 100ms in background. On `'remove'`: cancels immediately. Initial reconciliation on startup. Filters mpv streams (`application.process.binary = "mpv"` / `application.name = "mpv"`) and corked streams.

2. **`MonitorStreamAsync`** — `parec --monitor-stream=<id> --format=float32le --channels=1 --rate=8000 --raw`; reads 160-sample chunks (20ms), computes peak dBFS. `Interlocked.Increment/Decrement` on `_aboveThresholdCount`. `finally` always decrements if above threshold when cancelled.

3. **`WatchMuteAsync`** — polls `_aboveThresholdCount` every 20ms. Counts consecutive ms above/below threshold; fires `PlayerHelper.SetMute` once delay exceeded.

**Critical invariant**: always `--monitor-stream=<id>`. Never `@DEFAULT_MONITOR@` — captures livepaper's own audio → oscillation (mute → silence → unmute → audio detected → mute...).

**LWE stream filter**: linux-wallpaperengine's audio registers as `application.process.binary = "linux-wallpaperengine"` (also `application.name = "linux-wallpaperengine"`). `GetNonMpvStreamIdsAsync()` + `GetLweSinkInputIds()` match on the binary — auto-mute **skips** it (else LWE mutes itself) and live scene-volume **targets** it (`ApplyLweVolume` via `pactl`).

**`_aboveThresholdCount` reset**: `Interlocked.Exchange(..., 0)` at top of `WatchStreamsAsync` (not in `Stop()`), so `finally` blocks from previous run can decrement safely without racing against new monitors.

**Daemon persistence**: on close with AutoMute enabled, `SpawnDetachedMonitor()` launches `livepaper --monitor` via `setsid`, writes PID to `~/.config/livepaper/monitor.pid`. On next open, `KillDetachedMonitor()` kills it before app's own `AudioMonitor` starts.

## Restart Daemon

mpvpaper has a frame-buffer memory leak (~1.3 MB/s). `--restart-daemon` periodically kills and relaunches mpvpaper to work around it. Interval configured via `RestartIntervalSeconds` (default 600s, min 5, max 3600) in Settings → Playback.

**In-process (GUI open)**: `UpdateRestartTimer()` starts a `System.Threading.Timer`. No-op when `_daemonMode = true` so the timer daemon doesn't start a competing in-process timer. Timer stops in `TeardownTimer()` (called by `Stop()` and before each new session). Restarted via `UpdateRestartTimer()` after every `Apply`/`ApplyPlaylist`/`ApplyTimedPlaylist`/`ResumeTimedTimer`/`RestoreTimedPlaylist`.

**Detached daemon (GUI closed)**: `SpawnRestartDaemon()` launches `livepaper --restart-daemon` via `setsid`, writes PID to `restart.pid`. Bails if `IsGuiTimerAlive()` (mirrors timer-daemon guard). Killed on GUI open, by `Stop()`, and by `--kill`.

**Restart disabled**: `RestartIntervalSeconds <= 0` → all restart paths (`SpawnRestartDaemon`, `UpdateRestartTimer`, daemon loop) bail immediately with no-op. Restart is fully off when interval is 0.

**Timed playlist coordination**: `RestartCurrent()` writes `"restart"` to `pending_action.txt` instead of cold-restarting directly. The tick owner dispatches to `RestartCurrentAndLaunch()` which kills and relaunches the current wallpaper without advancing or resetting the countdown. Skipped when `_timedTimerPaused` is true (daemon also checks `IsTimedPlaylistPaused()` from the state file) to avoid queuing a restart that fires immediately on unpause.

**Race guard**: `RestartCurrent()` checks `IsPlaying` inside `_lock` before calling `DoColdRestart` — prevents relaunch if `Stop()` ran while the callback was waiting on the lock.

## Centralized Helpers

**`ArmVideoEndWait(string next, bool prevIsVideo = true)`** — canonical way to start a `DoVideoEndWait` task. Sets `_waitingForVideoEnd = true`, creates and assigns `_waitCts`, spawns task. Always use this — never inline the 3-line pattern.

**`PostSwitch(string path)`** — called after every wallpaper switch in timed machinery. If `_advanceOnVideoEnd && !IsScenePath(path)`: calls `AdvanceToNext()` and `ArmVideoEndWait`. Else: resets `_timedRemainingMs` to full interval. Always calls `SaveTimedState()`. Used in `LaunchAndReset` and both `DoVideoEndWait` completion paths.

**`PlayingHistoryIndex`** (private property) — when `_waitingForVideoEnd = true`, `_historyIndex` is pre-fetched one step ahead; actual playing item is at `_historyIndex - 1`. Property returns `_historyIndex - 1` when `_waitingForVideoEnd && _historyIndex > 0`, else `_historyIndex`. Use this everywhere "current playing index" is needed.

## Mode Switches (mid-session, no mpvpaper restart)

**`SwitchFromTimedToAdvanceOnEnd`**: cancels `_waitCts`, un-advances `_historyIndex` if pre-fetch was in flight, sets `_advanceOnVideoEnd = true` / `_timedInterval = Zero`, preserves `_timedRemainingMs`, re-arms `DoVideoEndWait`. Does NOT restart mpvpaper.

**`SwitchFromAdvanceOnEndToTimed`**: cancels `_waitCts`, clears `_waitingForVideoEnd`, converts mpv to single-file loop (`loop-file=inf`), starts the livepaper timer.

**`UpdateTimedSettings`**: called when interval/mode settings change mid-session. Preserves proportional remaining: `elapsed = max(0, oldIntervalMs - _timedRemainingMs)`, `newRemaining = max(tickInterval, newIntervalMs - elapsed)`. Never restarts current wallpaper.

**App close**: the Electron shell (`app/shell/main.js`, `window-all-closed` / `before-quit`) kills `livepaper --serve`. The backend's ASP.NET `app.Lifetime.ApplicationStopping` hook (`Web/ServerHost.cs`) is the close handler: it `AudioMonitor.Stop()`s and hands mute back to a detached `--monitor` when AutoMute + playing.
