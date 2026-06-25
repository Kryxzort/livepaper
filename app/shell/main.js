// Electron shell. Two modes, auto-detected:
//  • DEV (HMR): if a Vite dev server is up on :5173 (systemd livepaper-vite), load the UI from Vite
//    (hot-reload) and talk to the systemd `dotnet watch -- --serve` backend (port via serve.port).
//  • PROD: no Vite → spawn our own backend (LP_BACKEND or `dotnet run`) and load the built dist
//    same-origin. Packaging spawns the published binary.
const { app, BrowserWindow, ipcMain, dialog } = require("electron");
const { spawn } = require("child_process");
const http = require("http");
const fs = require("fs");
const os = require("os");
const path = require("path");

const repoRoot = path.resolve(__dirname, "..", "..");
const uiDir = path.join(repoRoot, "app", "ui", "dist");
const portFile = path.join(os.homedir(), ".config", "livepaper", "serve.port");
const VITE = "http://localhost:5173";

// NVIDIA + native Wayland + Linux ≥6.12 crash-loops the GPU process (OzoneImageBacking/EGLImage) →
// broken accel + dead window + systemic lag. Run under XWayland where NVIDIA accel is stable.
// (override with LP_OZONE=wayland to try native again.)
app.commandLine.appendSwitch("ozone-platform-hint", process.env.LP_OZONE || "x11");

// (Opt-in only) ANGLE backend override. The default GL backend crash-loops the GPU process on some
// NVIDIA stacks (eglCreateImage 0x3009 / OzoneImageBacking) which kills WebGL — but forcing Vulkan
// can break window compositing (black/transparent flicker). So it stays OFF unless explicitly asked.
const angle = process.env.LP_ANGLE;
if (angle) {
  app.commandLine.appendSwitch("use-angle", angle);
  if (angle === "vulkan") app.commandLine.appendSwitch("enable-features", "Vulkan");
}

// (Opt-in) Software GL. On stacks where the hardware GPU process crash-loops (eglCreateImage),
// hardware WebGL is unavailable/slow to warm up → transition previews lag. SwiftShader renders the
// whole renderer on CPU: stable window + instant WebGL, at the cost of CPU-composited UI.
if (process.env.LP_SOFTGL) {
  app.commandLine.appendSwitch("disable-gpu");
  app.commandLine.appendSwitch("enable-unsafe-swiftshader");
}

let backend;
function startBackend() {
  try { fs.unlinkSync(portFile); } catch { /* fresh */ }
  if (process.env.LP_BACKEND) {
    backend = spawn(process.env.LP_BACKEND, ["--serve"], { env: process.env, stdio: "inherit" });
  } else {
    backend = spawn("dotnet", ["run", "--project", "src/livepaper", "--", "--serve"], {
      cwd: repoRoot, env: { ...process.env, LP_UI_DIR: uiDir }, stdio: "inherit",
    });
  }
}

function waitForPort(cb, tries = 120) {
  if (fs.existsSync(portFile)) {
    const port = fs.readFileSync(portFile, "utf8").trim();
    if (port) return cb(port);
  }
  if (tries <= 0) { console.error("backend never wrote serve.port"); app.quit(); return; }
  setTimeout(() => waitForPort(cb, tries - 1), 500);
}

// Is a Vite dev server already running? → dev/HMR mode.
function viteUp(cb) {
  const req = http.get(VITE, (res) => { res.destroy(); cb(true); });
  req.on("error", () => cb(false));
  req.setTimeout(400, () => { req.destroy(); cb(false); });
}

// Native folder/file picker for Settings (Wallpaper Engine folder + steamcmd path).
ipcMain.handle("lp:pick", async (_e, opts) => {
  const r = await dialog.showOpenDialog({ properties: [opts?.folder ? "openDirectory" : "openFile"] });
  return r.canceled || !r.filePaths.length ? null : r.filePaths[0];
});

function openWindow(loadUrl) {
  const win = new BrowserWindow({
    width: 1400, height: 900, backgroundColor: "#1e1e2e", title: "livepaper",
    webPreferences: { backgroundThrottling: false, preload: path.join(__dirname, "preload.js") },
  });
  win.setMenuBarVisibility(false);
  // FREEZE FIX: authoritative active/idle signal from the WM (OS-level focus), more reliable than DOM
  // visibilitychange — on X11 an occluded window (you fullscreened another app over it) never reports
  // hidden, so the renderer would keep decoding all the autoplay gifs forever → ThreadPool backlog →
  // main-thread wedge. blur/focus fire on real focus changes; the renderer pauses gifs on idle.
  const sendActive = (v) => { try { win.webContents.send("lp:active", v); } catch {} };
  win.on("blur", () => sendActive(false));
  win.on("focus", () => sendActive(true));
  win.on("hide", () => sendActive(false));
  win.on("minimize", () => sendActive(false));
  win.on("show", () => sendActive(true));
  win.on("restore", () => sendActive(true));
  win.loadURL(loadUrl);
}

// LP_DEBUG=1 → ?debug → the lpdbg control bridge + perf-metrics tracking (fetch via lpdbg.metrics()).
// It does NOT show the on-screen HUD: the visual overlay is gated to the Settings "Show debug overlay"
// toggle only (see DebugHud overlayOn) — bridge mode keeps the values accessible without painting them.
const DBG = process.env.LP_DEBUG ? "debug" : "";
// LP_DEBUG opens a CDP remote-debugging port so tooling can attach to the live window.
if (process.env.LP_DEBUG) app.commandLine.appendSwitch("remote-debugging-port", "9222");

app.whenReady().then(() => {
  viteUp((dev) => {
    if (dev) {
      // backend is the systemd dotnet-watch serve (pinned port) — don't spawn our own; just read it.
      waitForPort((port) => openWindow(`${VITE}/?api=${port}${DBG ? `&${DBG}` : ""}`));
    } else {
      startBackend();
      waitForPort((port) => openWindow(`http://127.0.0.1:${port}/${DBG ? `?${DBG}` : ""}`));
    }
  });
});

app.on("window-all-closed", () => { try { backend?.kill(); } catch {} app.quit(); });
app.on("before-quit", () => { try { backend?.kill(); } catch {} });
