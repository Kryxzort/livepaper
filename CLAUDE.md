# CLAUDE.md

**livepaper** — Linux live-wallpaper manager for Wayland. An **Electron + React** UI over a **headless C# backend**: the same `livepaper` binary runs the CLI, the background daemons, and a loopback web API (`--serve`) that the UI talks to. Wallpapers are applied via [mpvpaper](https://github.com/GhostNaN/mpvpaper) (videos) and [linux-wallpaperengine](https://github.com/Almamu/linux-wallpaperengine) (WE scenes).

## System Dependencies

- `mpvpaper`, `mpv` — video wallpaper player
- `.NET 10 SDK` — build the backend; `Node.js` / `npm` — build the React UI + run the Electron shell
- Wayland compositor (Hyprland, Sway, GNOME on Wayland)
- `pactl` / `parec` (libpulse) — Auto-Mute stream detection (+ live LWE scene volume via `pactl`)
- `ffmpeg` — thumbnail extraction (Import) + frozen-frame grabs + transition frame capture (→ raw RGBA)
- `wl-clipboard` — `wl-copy` for keybind Copy buttons (renderer uses `navigator.clipboard`, falls back to `wl-copy`)
- `linux-wallpaperengine` *(optional)* — WE **scene** support (Settings → "Allow scene support")
- `grim` — captures the live scene frame for transitions
- *(build-time, optional)* `cc` + `wayland-scanner` + wayland/EGL/GLES dev headers — build `lp-transition` (transition renderer); absent → transitions no-op (instant cut)

## Common Commands

```bash
bash scripts/dev.sh                              # dev: Vite HMR (:5173) + dotnet watch --serve (:5174) as systemd user units
livepaper                                        # open the app (GUI) — bare command launches the Electron shell
livepaper-ui                                     # explicit GUI launcher (Electron)
dotnet run --project src/livepaper -- --serve    # backend only (headless loopback API)
cd app/ui && npm run dev                          # React UI only, in a browser (append ?api=<backend-port>)
dotnet build src/livepaper                        # build backend (no solution file at root)
cd app/ui && npm run build                        # build the UI bundle (dist/)
bash scripts/install.sh                           # build UI + publish backend, install livepaper + livepaper-ui
electron app/shell/probe.js http://127.0.0.1:<port>   # headless UI self-test (offscreen, counts rendered elements)
```

> **Note:** `app/shell` has its own Electron (decoupled from `demos/`, which is the old framework bake-off — kept, untracked). The CDP debug window (`LP_DEBUG=1`, port `:9222`, `node app/shell/cdp.js "<expr>"`) is for read-only inspection.

## CLI Flags

`livepaper` with **no args opens the GUI**; any flag runs headless:

- `--serve` *(internal default for the backend)* — headless web API on `127.0.0.1:<random>`, port written to `~/.config/livepaper/serve.port`. The Electron shell spawns this with an explicit `--serve`.
- `--restore` — re-applies last session without a UI. Timed playlists: `setsid`-spawns `--timer-daemon` and returns.
- `--random` — picks a random library wallpaper, applies, exits. Saves the pick so `--restore` replays it.
- `--kill` — stops playback, exits.
- `--monitor` *(internal)* — starts `AudioMonitor`, blocks. Spawned detached when the app closes with AutoMute on.
- `--timer-daemon` *(internal)* — owns the timed-playlist tick loop, blocks. Spawned by `--restore` (timed) and on close.
- `--restart-daemon` *(internal)* — periodically relaunches mpvpaper (frame-buffer leak workaround). Blocks. Spawned by `--restore`/on-close when playing; killed by `--kill` and on app open.
- `--action=<action>` — sends a command to the running session, exits:
  - `toggle-mute`, `toggle-pause`, `stop`, `play`, `toggle-play`
  - `next-wallpaper`, `previous-wallpaper`, `random`
  - `volume-up` / `volume-down` — ±5, clamped 0–100, persisted to settings.json

## Architecture

```text
src/livepaper/            # headless C# backend (CLI + daemons + --serve API). NO GUI here anymore.
├── Models/               # WallpaperResult, WallpaperDetail, LibraryItem, AppSettings, LastSession, LibMeta
├── Scrapers/             # MotionBgsScraper, MoewallsScraper, DesktophutScraper, WallpaperEngineScraper (static HTTP+HTML)
├── Services/             # IBgsProvider + one service per source; SteamWorkshopService
├── Helpers/              # PlayerHelper, DownloadHelper, LibraryService, LibraryStore, ImportService,
│                         #   SettingsService, AudioMonitor, MonitorDetector, TransitionService, WorkshopDownloader, WorkshopUnsubQueue
└── Web/                  # ServerHost (minimal-API endpoints), AppOps (orchestration), EventBus (WS), SteamOps
src/native/lp-transition/ # C: wlr-layer-shell + EGL/GLES transition renderer (built by install.sh)
transitions/             # shared GLSL effect catalog (glsl/ + manifest.json + wrap.* + preview/) — UI previews + the renderer
app/
├── ui/                   # Vite + React 19 + TS + zustand + framer-motion (the renderer)
└── shell/                # Electron main/preload + probes; spawns `livepaper --serve`, loads the UI same-origin
```

WE-style **transitions** animate every wallpaper switch (gl-transitions GLSL) — see `.claude/rules/player.md`. v1: video→video, **full-live** (both sides keep playing through the effect — libmpv decodes A+B in the renderer; frozen stills are the warmup/scene fallback).

The Avalonia UI (`Views/`, `ViewModels/`, `App.axaml`) was **removed** in the Electron rewrite — don't reference it. `Web/` only wraps the unchanged scrapers/helpers/daemons; UI lives in `app/`. See `.claude/rules/web-backend.md` + `web-ui.md`.

## Commit Style

Short, title-case, no period. e.g. `Fix App Name`, `Add Shuffle Toggle`.
