---
paths:
  - "scripts/**"
  - "app/shell/package.json"
  - "app/ui/package.json"
---

## Distribution

The app ships as **two pieces**: a published self-contained C# backend + the built React UI, launched by an Electron shell.

### `scripts/install.sh` — the current installer
1. `cd app/ui && npm install && npm run build` → UI bundle in `app/ui/dist/`.
2. `dotnet publish src/livepaper -r linux-x64 --self-contained` → backend in `~/.local/share/livepaper-web/backend`.
3. Stages the UI to `~/.local/share/livepaper-web/ui`; ensures `app/shell`'s Electron (`npm install`).
4. Installs to `~/.local/bin`:
   - **`livepaper`** — bare = open the GUI (`exec livepaper-ui`); any flag = the headless backend/CLI (`backend/livepaper "$@"`). Sets `LP_UI_DIR`.
   - **`livepaper-ui`** — runs Electron on `app/shell` (sets `LP_BACKEND` + `LP_UI_DIR`).
5. Writes `~/.local/share/applications/livepaper.desktop` (Exec=`livepaper-ui`).

**Electron is `app/shell/node_modules/electron`** (version pinned in `app/shell/package.json`, currently **42.5.0** — older Electron's Chromium crash-loops the GPU process on NVIDIA + kernel ≥6.12, see `freeze-diagnostics`). `app/shell/package.json` has an `allowScripts` allowlist keyed to the exact version so Electron's binary-download postinstall runs (bump it when bumping Electron). `install.sh` aborts if the binary is missing — no `demos/` fallback (`demos/` is gitignored/local-only, absent on a clone).

### ⚠️ Stale / not-yet-rebuilt packaging
- **`scripts/build-appimage.sh`** and **`scripts/PKGBUILD`** still build the **pre-rewrite single-binary Avalonia app** (PKGBUILD has no `nodejs`/`electron` deps and points at the old upstream repo). They do **not** produce the current Electron+backend stack — treat as broken until rewritten.
- **AppImage via electron-builder is NOT set up** — `app/shell/package.json` has only a `start` script (no `build`/`dist`, no electron-builder config). The "`npm run dist`" mentioned in older notes/README does not exist yet.
- The old `src/livepaper/Assets/` icon (`livepaper.svg`/`.png`) was **deleted**; there is no packaged app icon wired up yet (the `.desktop` entry sets no `Icon=`). UI/static assets live under `app/ui/public`.

### Dev (no install)
`scripts/dev.sh` → systemd user units: `livepaper-vite` (HMR :5173) + `livepaper-watch` (`dotnet watch -- --serve` :5174). Launch the app normally; `main.js` auto-detects Vite. `scripts/dev-stop.sh` stops them. `scripts/freeze-monitor.sh` watches for the main-thread freeze.
