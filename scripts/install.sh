#!/bin/bash
# Install the web rewrite: publish the C# backend (CLI/daemons/--serve) + build the React UI,
# install a `livepaper` CLI launcher (restore/action/daemons/serve) and a `livepaper-ui` GUI
# launcher (Electron shell → spawns the backend, loads the UI). For a portable AppImage use
# electron-builder: (cd app/shell && npm run dist).
set -e
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIB="$HOME/.local/share/livepaper-web"
BIN="$HOME/.local/bin"
APPS="$HOME/.local/share/applications"

echo "==> building React UI"
(cd "$ROOT/app/ui" && npm install && npm run build)

echo "==> publishing backend (self-contained)"
rm -rf "$LIB/backend"
dotnet publish "$ROOT/src/livepaper" -r linux-x64 --self-contained -c Release -o "$LIB/backend"

echo "==> staging UI"
rm -rf "$LIB/ui"; mkdir -p "$LIB/ui"; cp -r "$ROOT/app/ui/dist/." "$LIB/ui/"

echo "==> staging transition assets (shaders + manifest + preview frames)"
rm -rf "$LIB/transitions"; mkdir -p "$LIB/transitions"; cp -r "$ROOT/transitions/." "$LIB/transitions/"

# Native helpers are REQUIRED, not optional: lp-transition renders every wallpaper switch
# (transitions are a core feature, never a no-op) and lp-audio gapless-crossfades scene audio.
# Preflight the toolchain and fail loudly with a fix — never silently ship a degraded build.
echo "==> checking native build toolchain (lp-transition + lp-audio)"
missing=""
command -v cc            >/dev/null 2>&1 || missing="$missing cc"
command -v make          >/dev/null 2>&1 || missing="$missing make"
command -v pkg-config    >/dev/null 2>&1 || missing="$missing pkg-config"
command -v wayland-scanner >/dev/null 2>&1 || missing="$missing wayland-scanner"
if command -v pkg-config >/dev/null 2>&1; then
  for p in wayland-client wayland-egl egl glesv2 mpv libpulse; do
    pkg-config --exists "$p" 2>/dev/null || missing="$missing ${p}(dev)"
  done
fi
if [ -n "$missing" ]; then
  cat >&2 <<MSG
ERROR: missing native build dependencies:$missing
  Transitions and scene-audio crossfade are core features, not optional — install these and re-run:
    Arch:   sudo pacman -S base-devel wayland wayland-protocols libglvnd mpv libpulse
    Debian: sudo apt install build-essential libwayland-bin libwayland-dev libegl-dev libgles-dev libmpv-dev libpulse-dev
    Fedora: sudo dnf install gcc make wayland-devel mesa-libEGL-devel mesa-libGLES-devel mpv-libs-devel pulseaudio-libs-devel
    NixOS:  use the flake instead — 'nix run github:kryxzort/livepaper-pro' (all deps handled)
MSG
  exit 1
fi

echo "==> building lp-transition (wlr-layer-shell GL renderer)"
make -C "$ROOT/src/native/lp-transition" >/dev/null   # set -e aborts on failure
cp "$ROOT/src/native/lp-transition/lp-transition" "$LIB/lp-transition"

echo "==> building lp-audio (libpulse scene-audio crossfade helper)"
make -C "$ROOT/src/native/lp-audio" >/dev/null
cp "$ROOT/src/native/lp-audio/lp-audio" "$LIB/lp-audio"

echo "==> ensuring Electron (shell)"
(cd "$ROOT/app/shell" && npm install >/dev/null 2>&1 || true)
# Prefer a system Electron (NixOS: the bundled prebuilt can't load its libs — libatk etc. aren't
# at FHS paths; a native electron_42 from nixpkgs works). Fall back to the bundled prebuilt on
# distros where it runs. Keep the 42.x pin (older crash-loops the GPU on NVIDIA + kernel ≥6.12).
ELECTRON="$(command -v electron 2>/dev/null || echo "$ROOT/app/shell/node_modules/electron/dist/electron")"
[ -x "$ELECTRON" ] || { echo "ERROR: no Electron (system or bundled). Install electron or run npm install in app/shell."; exit 1; }

mkdir -p "$BIN"
echo "==> installing 'livepaper' (headless CLI: --restore/--action/daemons/--serve)"
cat > "$BIN/livepaper" <<WRAP
#!/bin/bash
export LP_UI_DIR="$LIB/ui"
export LP_TRANSITIONS_DIR="$LIB/transitions"
[ -x "$LIB/lp-transition" ] && export LP_TRANSITION_BIN="$LIB/lp-transition"
[ -x "$LIB/lp-audio" ] && export LP_AUDIO_BIN="$LIB/lp-audio"
# bare 'livepaper' opens the GUI; any flag (--restore/--action/--serve/daemons) runs the headless backend
[ \$# -eq 0 ] && exec "$BIN/livepaper-ui"
exec "$LIB/backend/livepaper" "\$@"
WRAP
chmod 755 "$BIN/livepaper"

echo "==> installing 'livepaper-ui' (GUI)"
cat > "$BIN/livepaper-ui" <<WRAP
#!/bin/bash
export LP_BACKEND="$LIB/backend/livepaper"
export LP_UI_DIR="$LIB/ui"
export LP_TRANSITIONS_DIR="$LIB/transitions"
[ -x "$LIB/lp-transition" ] && export LP_TRANSITION_BIN="$LIB/lp-transition"
[ -x "$LIB/lp-audio" ] && export LP_AUDIO_BIN="$LIB/lp-audio"
exec "$ELECTRON" "$ROOT/app/shell"
WRAP
chmod 755 "$BIN/livepaper-ui"

mkdir -p "$APPS"
cat > "$APPS/livepaper.desktop" <<EOF
[Desktop Entry]
Name=Livepaper
Comment=Live wallpaper manager (Wayland)
Exec=$BIN/livepaper-ui
Type=Application
Categories=Utility;
Keywords=wallpaper;live;wayland;video;
EOF
update-desktop-database "$APPS" 2>/dev/null || true

echo ""
echo "Done. Ensure $BIN is on PATH."
echo "  GUI:  livepaper            (bare = open the app; livepaper-ui also works)"
echo "  CLI:  livepaper --restore | --action=next-wallpaper | --kill | --serve | …"
