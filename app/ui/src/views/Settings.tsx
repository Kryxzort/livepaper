import { memo, useEffect, useRef, useState } from "react";
import {
  FolderOpen, X, RotateCcw, SlidersHorizontal, Repeat, VolumeX, Cpu, Palette,
  Layers, MonitorSmartphone, Download, Terminal, Keyboard, Library as LibraryIcon, Wrench, Sparkles,
} from "lucide-react";
import { useStore, SETTINGS_DEFAULTS } from "../store";
import { api } from "../api/client";
import { useSmoothScroll } from "../hooks/useSmoothScroll";
import { Select } from "../components/Select";
import { Combo } from "../components/Combo";
import { TransitionPicker, type TransitionValue } from "../components/TransitionPicker";

// Electron preload (app/shell/preload.js) exposes native dialogs; null in a browser → text entry only.
const lp = (window as unknown as { lp?: { pickFolder?: () => Promise<string | null>; pickFile?: () => Promise<string | null> } }).lp;

// reads/writes go through the store's settings object (camelCase, mirrors AppSettings).
function useS() {
  const settings = useStore((s) => s.settings) ?? {};
  const setSetting = useStore((s) => s.setSetting);
  const g = <T,>(k: string, d: T): T => (settings[k] as T) ?? d;
  return { g, set: setSetting, settings };
}

// `rk` (+ optional `def`) ties a row to a settings key: a reset icon appears ONLY when the current
// value deviates from its default (SETTINGS_DEFAULTS), and resets that one setting. `onReset`/`deviated`
// cover non-settings-backed rows (e.g. Theme, applied via applyTheme not setSetting).
function Row({ label, hint, disabled, rk, def, deviated, onReset, children }: {
  label: string; hint?: string; disabled?: boolean;
  rk?: string; def?: unknown; deviated?: boolean; onReset?: () => void; children: React.ReactNode;
}) {
  const cur = useStore((s) => (rk ? s.settings?.[rk] : undefined));
  const set = useStore((s) => s.setSetting);
  const d = rk !== undefined ? (def ?? SETTINGS_DEFAULTS[rk]) : undefined;
  const show = onReset
    ? !!deviated
    : (rk !== undefined && cur !== undefined && JSON.stringify(cur) !== JSON.stringify(d));
  const reset = onReset ?? (() => { if (rk !== undefined) set(rk, d); });
  return (
    <div className={`srow${disabled ? " disabled" : ""}`}>
      <div className="slabel">
        <span className="slabel-row">{label}
          {show && <button className="s-reset" title="Reset to default" onClick={reset}><RotateCcw size={12} /></button>}
        </span>
        {hint && <span className="shint">{hint}</span>}
      </div>
      <div className="sctl">{children}</div>
    </div>
  );
}
const Section = ({ title, icon, children }: { title: string; icon?: React.ReactNode; children: React.ReactNode }) => (
  <div className="ssection">
    <h3>{icon && <span className="ssection-ico">{icon}</span>}{title}</h3>
    {children}
  </div>
);
const Help = ({ children }: { children: React.ReactNode }) => <p className="help">{children}</p>;

export const Settings = memo(function Settings() {
  const { g, set } = useS();
  const clearLibrary = useStore((s) => s.clearLibrary);
  const clearCache = useStore((s) => s.clearCache);
  const steam = useStore((s) => s.steam);
  const startQr = useStore((s) => s.startQr);
  const signoutSteam = useStore((s) => s.signoutSteam);
  const resetAllSettings = useStore((s) => s.resetAllSettings);
  const steamcmdSignin = useStore((s) => s.steamcmdSignin);
  const themes = useStore((s) => s.themes);
  const themeName = useStore((s) => s.theme?.name ?? "");
  const applyTheme = useStore((s) => s.applyTheme);
  const scrollRef = useRef<HTMLDivElement>(null);
  useSmoothScroll(scrollRef);
  const [mpv, setMpv] = useState("");
  const [lwe, setLwe] = useState("");
  const [clearArm, setClearArm] = useState(-1); // -1 idle, >0 counting, 0 ready-to-confirm
  const [wipeArm, setWipeArm] = useState(-1);   // same arming pattern for "Clear settings"
  // auto-detected outputs (hyprctl/sway): name + current refresh rate + which is focused/primary
  const [detected, setDetected] = useState<{ name: string; refreshHz: number; primary: boolean }[]>([]);

  useEffect(() => {
    let alive = true;
    const t = setTimeout(() => {
      api.mpvPreview().then((x) => alive && setMpv(x));
      api.lwePreview().then((x) => alive && setLwe(x));
    }, 200);
    return () => { alive = false; clearTimeout(t); };
  }, [g("loop", true), g("noAudio", false), g("disableCache", false), g("volume", 100), g("speed", 1), g("hwDec", "auto"), g("videoScale", "fill"), g("demuxerMaxBytes", 20), g("demuxerMaxBackBytes", 5), JSON.stringify(g("lweMonitors", []))]); // eslint-disable-line

  useEffect(() => {
    if (clearArm <= 0) return;
    const t = setTimeout(() => setClearArm((n) => n - 1), 1000);
    return () => clearTimeout(t);
  }, [clearArm]);
  useEffect(() => {
    if (wipeArm <= 0) return;
    const t = setTimeout(() => setWipeArm((n) => n - 1), 1000);
    return () => clearTimeout(t);
  }, [wipeArm]);
  // Monitor auto-detection feeds a datalist so the name fields autocomplete real outputs
  // instead of pure free-text guessing.
  useEffect(() => { api.monitors().then(setDetected).catch(() => {}); }, []);

  // interval H/M/S
  const iv = g("globalIntervalSeconds", 1800);
  const setIv = (h: number, m: number, sec: number) => set("globalIntervalSeconds", h * 3600 + m * 60 + sec);
  const H = Math.floor(iv / 3600), M = Math.floor((iv % 3600) / 60), S = iv % 60;

  const adv = g("advancedSettings", false); // gates demuxer / hw-decode / cache / copy-files / live previews
  type Mon = { name: string; fps: number; isPrimary: boolean };
  const monitors = g<Mon[]>("lweMonitors", []);
  const setMonitors = (m: Mon[]) => set("lweMonitors", m);
  // Auto-populate the scene monitor list from detected outputs the first time scenes are enabled
  // (empty list). First detected output = primary. Won't clobber a list the user already configured.
  useEffect(() => {
    if (!g("allowScenes", false) || !detected.length || monitors.length) return;
    setMonitors(detected.map((d) => ({ name: d.name, fps: d.refreshHz, isPrimary: d.primary })));
  }, [detected, g("allowScenes", false)]); // eslint-disable-line

  const mode = g<string>("workshopAcquireMode", "subscribe");
  const mute = g("noAudio", false);          // disables the volume row (parity: muted = no volume)
  const am = g("autoMute", false);           // gates the auto-mute children

  // (live wallpaper bg is now rendered at App level via <WallpaperBg> — Settings always shows it, and
  // the "Live wallpaper behind all tabs" advanced toggle extends it to Browse/Library)
  const pickInto = async (key: string, kind: "folder" | "file") => {
    const p = await (kind === "folder" ? lp?.pickFolder?.() : lp?.pickFile?.());
    if (p) set(key, p);
  };

  const actions = ["toggle-mute", "toggle-pause", "stop", "play", "toggle-play",
    "next-wallpaper", "previous-wallpaper", "random", "volume-up", "volume-down"];

  // Global transition config (the fallback used when a playlist doesn't override). The picker speaks
  // the per-playlist field names, so adapt to/from the global* keys.
  const [trPicker, setTrPicker] = useState(false);
  const trVal: TransitionValue = {
    transitionEnabled: g("globalTransitionEnabled", false),
    transitionEffectIds: g<string[]>("globalTransitionEffectIds", []),
    transitionDurationMs: g("globalTransitionDurationMs", 600),
    transitionDurationMaxMs: g("globalTransitionDurationMaxMs", 0),
    transitionShuffle: g("globalTransitionShuffle", true),
  };
  const trChange = (p: Partial<TransitionValue>) => {
    for (const [k, v] of Object.entries(p)) set("global" + k[0].toUpperCase() + k.slice(1), v as never);
  };

  return (
    <div className="scroll settings" ref={scrollRef}>
      <Section title="Playback" icon={<SlidersHorizontal size={15} />}>
        <Row label="Loop" rk="loop"><input type="checkbox" checked={g("loop", true)} onChange={(e) => set("loop", e.target.checked)} /></Row>
        <Row label="Mute audio" rk="noAudio"><input type="checkbox" checked={mute} onChange={(e) => set("noAudio", e.target.checked)} /></Row>
        {adv && <Row label="Disable cache" rk="disableCache"><input type="checkbox" checked={g("disableCache", false)} onChange={(e) => set("disableCache", e.target.checked)} /></Row>}
        <Row label="Volume" hint={mute ? "muted" : `${g("volume", 100)}`} disabled={mute} rk="volume">
          <input type="range" min={0} max={100} value={g("volume", 100)} disabled={mute} onChange={(e) => set("volume", +e.target.value)} /></Row>
        <Row label="Normalize audio" hint="match perceived loudness across videos (EBU R128) so switching wallpapers doesn't jump in volume — per-video & global volume still apply on top. Each video is measured once (in the background) the first time it plays." disabled={mute} rk="normalizeAudio">
          <input type="checkbox" checked={g("normalizeAudio", false)} disabled={mute} onChange={(e) => set("normalizeAudio", e.target.checked)} /></Row>
        {!mute && g("normalizeAudio", false) && <Row label="Target loudness" hint="LUFS (dB-referenced loudness); −14 = streaming standard, lower = quieter. Pick a preset or type your own. Every video is matched to this level." rk="normalizeTargetLufs">
          <Combo value={g("normalizeTargetLufs", -14) as number} min={-100} max={0} step={1}
            onChange={(v) => set("normalizeTargetLufs", v)}
            options={[{ value: -11, label: "Loud (−11)" }, { value: -14, label: "Streaming (−14)" }, { value: -19, label: "Quiet (−19)" }, { value: -23, label: "Broadcast (−23)" }]} /><span>LUFS</span></Row>}
        <Row label="Speed" hint={`${g("speed", 1)}×`} rk="speed"><input type="range" min={0.1} max={4} step={0.1} value={g("speed", 1)} onChange={(e) => set("speed", +e.target.value)} /></Row>
        <Row label="Restart mpvpaper every (s)" hint="0 = off" rk="restartIntervalSeconds"><input className="num" type="number" min={0} max={3600} value={g("restartIntervalSeconds", 600)} onChange={(e) => set("restartIntervalSeconds", +e.target.value)} /></Row>
        {adv && <Row label="Restart only at playlist changeover" hint="defer the restart to the next video change (no mid-video flash); lone videos still restart" rk="restartOnSwitchOnly"><input type="checkbox" checked={g("restartOnSwitchOnly", false)} onChange={(e) => set("restartOnSwitchOnly", e.target.checked)} /></Row>}
        <Help>Kills and relaunches mpvpaper periodically to prevent memory leaks.</Help>
      </Section>

      <Section title="Rotation (global)" icon={<Repeat size={15} />}>
        <Row label="Switch when video ends" rk="globalAdvanceOnVideoEnd"><input type="checkbox" checked={g("globalAdvanceOnVideoEnd", true)} onChange={(e) => set("globalAdvanceOnVideoEnd", e.target.checked)} /></Row>
        <Row label="Wait for video to end after interval" rk="globalWaitForVideoEnd"><input type="checkbox" checked={g("globalWaitForVideoEnd", false)} onChange={(e) => set("globalWaitForVideoEnd", e.target.checked)} /></Row>
        <Row label="Interval" rk="globalIntervalSeconds">
          <div className="hms">
            <input className="num" type="number" min={0} max={99} value={H} onChange={(e) => setIv(+e.target.value, M, S)} /><span>h</span>
            <input className="num" type="number" min={0} max={59} value={M} onChange={(e) => setIv(H, +e.target.value, S)} /><span>m</span>
            <input className="num" type="number" min={0} max={59} value={S} onChange={(e) => setIv(H, M, +e.target.value)} /><span>s</span>
          </div>
        </Row>
        <Row label="Auto-add new library items to playlist" rk="autoAddLibraryToPlaylist"><input type="checkbox" checked={g("autoAddLibraryToPlaylist", false)} onChange={(e) => set("autoAddLibraryToPlaylist", e.target.checked)} /></Row>
        <Row label="Transitions" hint="animated effect between wallpaper switches" rk="globalTransitionEnabled">
          <button className="btn ghost ico" onClick={() => setTrPicker(true)}><Sparkles size={14} />
            {trVal.transitionEnabled ? `On · ${trVal.transitionEffectIds.length}` : "Off"}</button>
        </Row>
      </Section>
      <TransitionPicker open={trPicker} value={trVal} onChange={trChange} onClose={() => setTrPicker(false)} title="Global Transitions" />

      <Section title="Auto-Mute" icon={<VolumeX size={15} />}>
        <Row label="Mute when system audio plays" rk="autoMute"><input type="checkbox" checked={am} onChange={(e) => set("autoMute", e.target.checked)} /></Row>
        <Row label="Only if MPRIS player active" disabled={!am} rk="autoMuteOnlyIfMprisActive"><input type="checkbox" disabled={!am} checked={g("autoMuteOnlyIfMprisActive", false)} onChange={(e) => set("autoMuteOnlyIfMprisActive", e.target.checked)} /></Row>
        <Row label="Mute after (ms)" disabled={!am} rk="autoMuteDelayMs"><input className="num" type="number" disabled={!am} min={0} max={30000} value={g("autoMuteDelayMs", 200)} onChange={(e) => set("autoMuteDelayMs", +e.target.value)} /></Row>
        <Row label="Unmute after (ms)" disabled={!am} rk="autoUnmuteDelayMs"><input className="num" type="number" disabled={!am} min={0} max={30000} value={g("autoUnmuteDelayMs", 2000)} onChange={(e) => set("autoUnmuteDelayMs", +e.target.value)} /></Row>
        <Row label="Min level (dB)" disabled={!am} rk="autoMuteThresholdDb"><input className="num" type="number" disabled={!am} min={-80} max={0} value={g("autoMuteThresholdDb", -70)} onChange={(e) => set("autoMuteThresholdDb", +e.target.value)} /></Row>
      </Section>

      <Section title="Rendering & Memory" icon={<Cpu size={15} />}>
        {adv && (
          <Row label="Hardware decoding" rk="hwDec">
            <Select value={g("hwDec", "auto")} onChange={(v) => set("hwDec", v)}
              options={["auto", "nvdec", "vaapi", "no"].map((x) => ({ value: x, label: x }))} />
          </Row>
        )}
        <Row label="Video scale" rk="videoScale">
          <Select value={g("videoScale", "fill")} onChange={(v) => set("videoScale", v)}
            options={[{ value: "fill", label: "fill" }, { value: "fit", label: "fit" }]} />
        </Row>
        <Row label="Video FPS cap" hint="0 = native · applies to videos, not scenes" rk="videoFps"><input className="num" type="number" min={0} max={480} value={g("videoFps", 0)} onChange={(e) => set("videoFps", +e.target.value)} /></Row>
        {adv && <Row label="Demuxer max (MiB)" rk="demuxerMaxBytes"><input className="num" type="number" min={1} max={10000} value={g("demuxerMaxBytes", 20)} onChange={(e) => set("demuxerMaxBytes", +e.target.value)} /></Row>}
        {adv && <Row label="Demuxer max back (MiB)" rk="demuxerMaxBackBytes"><input className="num" type="number" min={1} max={10000} value={g("demuxerMaxBackBytes", 5)} onChange={(e) => set("demuxerMaxBackBytes", +e.target.value)} /></Row>}
      </Section>

      <Section title="Appearance" icon={<Palette size={15} />}>
        <Row label="Theme" onReset={() => applyTheme(SETTINGS_DEFAULTS.theme as string)} deviated={themeName !== SETTINGS_DEFAULTS.theme}>
          <Select value={themeName} onChange={(v) => applyTheme(v)}
            options={themes.map((t) => ({ value: t.name, label: t.name }))} />
        </Row>
        <Row label="Thumbnail aspect" rk="thumbnailAspect">
          <Select value={g("thumbnailAspect", "1:1")} onChange={(v) => set("thumbnailAspect", v)}
            options={["1:1", "16:9"].map((x) => ({ value: x, label: x }))} />
        </Row>
        <Row label="Card size" rk="cardSize">
          <Select value={g("cardSize", "Medium")} onChange={(v) => set("cardSize", v)}
            options={["Small", "Medium", "Large"].map((x) => ({ value: x, label: x }))} />
        </Row>
        <Row label="Autoplay GIF thumbnails" rk="autoPlayGifs"><input type="checkbox" checked={g("autoPlayGifs", false)} onChange={(e) => set("autoPlayGifs", e.target.checked)} /></Row>
      </Section>

      <Section title="Wallpaper Engine" icon={<Layers size={15} />}>
        <Row label="Workshop folder">
          <input className="text" value={g("wallpaperEnginePath", "")} onChange={(e) => set("wallpaperEnginePath", e.target.value)} />
          <button className="btn ghost ico" title="Choose folder" onClick={() => pickInto("wallpaperEnginePath", "folder")}><FolderOpen size={14} /> Browse</button>
        </Row>
        {adv && <Row label="Copy files instead of symlinking" rk="weCopyFiles"><input type="checkbox" checked={g("weCopyFiles", false)} onChange={(e) => set("weCopyFiles", e.target.checked)} /></Row>}
        {adv && <Row label="Replace direct downloads with Workshop copies" hint="when a directly-downloaded wallpaper appears in the WE folder, swap to the WE symlink/copy & reclaim the duplicate's disk" rk="replaceDirectWithWorkshop"><input type="checkbox" checked={g("replaceDirectWithWorkshop", false)} onChange={(e) => set("replaceDirectWithWorkshop", e.target.checked)} /></Row>}
        <Row label="Allow scene support" rk="allowScenes"><input type="checkbox" checked={g("allowScenes", false)} onChange={(e) => set("allowScenes", e.target.checked)} /></Row>
        <Row label="Auto-add new WE wallpapers to library" rk="autoImportWallpaperEngine"><input type="checkbox" checked={g("autoImportWallpaperEngine", false)} onChange={(e) => set("autoImportWallpaperEngine", e.target.checked)} /></Row>
        <Row label="Scene transition delay (ms)" rk="sceneTransitionDelayMs"><input className="num" type="number" min={0} max={5000} value={g("sceneTransitionDelayMs", 1000)} onChange={(e) => set("sceneTransitionDelayMs", +e.target.value)} /></Row>
      </Section>

      {g("allowScenes", false) && (
        <Section title="Monitors (scenes)" icon={<MonitorSmartphone size={15} />}>
          <Help>Names of the monitors to play scenes on.{detected.length > 0
            ? <> Detected: {detected.map((d) => <code key={d.name} className="kb">{d.name} {d.refreshHz}Hz{d.primary ? " ★" : ""}</code>)} — type or pick below.</>
            : <> Auto-detect found none; on Hyprland run <code className="kb">hyprctl monitors</code> to list names (use the equivalent for your compositor).</>}</Help>
          <datalist id="lp-monitors">{detected.map((d) => <option key={d.name} value={d.name} />)}</datalist>
          {monitors.map((m, i) => (
            <Row key={i} label={`Monitor ${i + 1}`}>
              <div className="hms">
                <input className="text mon-name" list="lp-monitors" placeholder="e.g. DP-1" value={m.name}
                  onChange={(e) => setMonitors(monitors.map((x, j) => j === i ? { ...x, name: e.target.value } : x))} />
                <input className="num" type="number" min={1} max={480} value={m.fps}
                  onChange={(e) => setMonitors(monitors.map((x, j) => j === i ? { ...x, fps: +e.target.value } : x))} /><span>fps</span>
                {/* primary is one-of-N (exactly one monitor) → radio, like the Workshop download mode section */}
                <label className="inline"><input type="radio" name="lwe-primary" checked={m.isPrimary}
                  onChange={() => setMonitors(monitors.map((x, j) => ({ ...x, isPrimary: j === i })))} /> primary</label>
                <button className="mini ico" title="Remove monitor" onClick={() => setMonitors(monitors.filter((_, j) => j !== i))}><X size={14} /></button>
              </div>
            </Row>
          ))}
          <button className="btn ghost" onClick={() => {
            const used = new Set(monitors.map((m) => m.name));
            const next = detected.find((d) => !used.has(d.name));
            setMonitors([...monitors, { name: next?.name ?? `DP-${monitors.length + 1}`, fps: next?.refreshHz ?? 60, isPrimary: monitors.length === 0 }]);
          }}>+ Add monitor</button>
        </Section>
      )}

      <Section title="Workshop download mode" icon={<Download size={15} />}>
        <Row label="Subscribe via Steam" hint="keeps synced + auto-updates">
          <input type="radio" checked={mode === "subscribe"} onChange={() => set("workshopAcquireMode", "subscribe")} /></Row>
        <Row label="Download via steamcmd" hint="snapshot, no sync">
          <input type="radio" checked={mode === "steamcmd"} onChange={() => set("workshopAcquireMode", "steamcmd")} /></Row>
        {mode === "subscribe"
          ? <Row label="Steam">
              {steam?.signedIn
                ? <div className="hms"><span className="shint">Signed in as <b>{steam.accountName}</b> · ~{steam.daysLeft}d left</span>
                    <button className="btn ghost" onClick={signoutSteam}>Sign out</button></div>
                : <button className="btn accent" onClick={startQr}>Sign in with QR</button>}
            </Row>
          : <>
              <Help>Downloads a one-time snapshot via steamcmd. No online subscription. Requires steamcmd + one-time sign-in.</Help>
              <Row label="steamcmd path" hint="blank = auto-detect">
                <input className="text" placeholder="/usr/bin/steamcmd" value={g("steamCmdPath", "")} onChange={(e) => set("steamCmdPath", e.target.value)} />
                <button className="btn ghost ico" title="Choose steamcmd binary" onClick={() => pickInto("steamCmdPath", "file")}><FolderOpen size={14} /> Browse</button>
              </Row>
              <Row label="Steam username"><input className="text" value={g("steamUsername", "")} onChange={(e) => set("steamUsername", e.target.value)} /></Row>
              <Row label="One-time sign-in" hint="caches sentry; opens a terminal"><button className="btn accent" onClick={steamcmdSignin}>Sign in (one-time)</button></Row>
            </>}
      </Section>

      <Section title="Advanced" icon={<Wrench size={15} />}>
        <Row label="Advanced settings" hint="show disable-cache, hardware decoding, demuxer, copy-files & the live command previews" rk="advancedSettings"><input type="checkbox" checked={adv} onChange={(e) => set("advancedSettings", e.target.checked)} /></Row>
        {adv && <Row label="Transition method" hint="how a wallpaper switch animates — Reveal: freeze the old, keep the new playing (recommended); Frozen: both sides still; Full-live: both play (heavy on 4K). Warp-style effects fall back to Frozen in Reveal." rk="transitionMethod">
          <Select value={g("transitionMethod", "reveal")} onChange={(v) => set("transitionMethod", v)}
            options={[{ value: "reveal", label: "Reveal (live B)" }, { value: "frozen", label: "Frozen" }, { value: "full-live", label: "Full-live" }]} />
        </Row>}
        {adv && g<string>("transitionMethod", "reveal") === "full-live" && <Row label="Full-live A-sync offset" hint="Full-live only: aim the outgoing video this many ms vs the live wallpaper at the cover, to cancel per-machine sync drift (negative = aim ahead). If you see a slight rewind raise it toward 0; a slight skip-ahead lower it. ~-30 here." rk="transitionLagOffsetMs">
          <input className="num" type="number" min={-200} max={200} step={5} value={g("transitionLagOffsetMs", -30)} onChange={(e) => set("transitionLagOffsetMs", +e.target.value || 0)} /><span>ms</span>
        </Row>}
        {adv && <Row label="Live wallpaper behind all tabs" hint="show the live wallpaper behind the grid on Browse & Library (visible in the gaps between cards)" rk="wallpaperBgAllTabs"><input type="checkbox" checked={g("wallpaperBgAllTabs", false)} onChange={(e) => set("wallpaperBgAllTabs", e.target.checked)} /></Row>}
        {adv && <Row label="Debug mode" hint="enable the lpdbg bridge + perf metrics" rk="debugMode"><input type="checkbox" checked={g("debugMode", false)} onChange={(e) => set("debugMode", e.target.checked)} /></Row>}
        {adv && g("debugMode", false) && <Row label="Show debug overlay" hint="the on-screen FPS / metrics HUD" rk="debugOverlay"><input type="checkbox" checked={g("debugOverlay", true)} onChange={(e) => set("debugOverlay", e.target.checked)} /></Row>}
      </Section>

      {adv && (
        <Section title="mpv options (live)" icon={<Terminal size={15} />}>
          <pre className="mpv">{mpv || "…"}</pre>
        </Section>
      )}

      {adv && (
        <Section title="linux-wallpaperengine options (live)" icon={<Terminal size={15} />}>
          <Help>Scene command (one line per configured monitor); the workshop ID is appended per scene.</Help>
          <pre className="mpv">{lwe || "…"}</pre>
        </Section>
      )}

      <Section title="Keybind reference" icon={<Keyboard size={15} />}>
        {actions.map((a) => (
          <Row key={a} label={a}>
            <code className="kb">livepaper --action={a}</code>
            <button className="mini" onClick={() => navigator.clipboard?.writeText(`livepaper --action=${a}`)}>copy</button>
          </Row>
        ))}
        <Row label="Autostart"><code className="kb">livepaper --restore</code>
          <button className="mini" onClick={() => navigator.clipboard?.writeText("livepaper --restore")}>copy</button></Row>
      </Section>

      <Section title="Library" icon={<LibraryIcon size={15} />}>
        <Row label="Resume from last on launch" rk="resumeFromLast"><input type="checkbox" checked={g("resumeFromLast", true)} onChange={(e) => set("resumeFromLast", e.target.checked)} /></Row>
        <Row label="Clear cache" hint="thumbnail/preview cache (re-downloads as needed)">
          <button className="btn ghost" onClick={() => clearCache()}>Clear Cache</button>
        </Row>
        <Row label="Clear settings" hint="reset all to defaults">
          <div className="hms">
            {wipeArm < 0 && <button className="btn danger" onClick={() => setWipeArm(3)}>Clear Settings</button>}
            {wipeArm > 0 && <button className="btn danger" disabled>Clear in {wipeArm}s…</button>}
            {wipeArm === 0 && <button className="btn danger solid glow-pulse" onClick={() => { resetAllSettings(); setWipeArm(-1); }}>Confirm — reset all</button>}
            {wipeArm >= 0 && <button className="btn ghost" onClick={() => setWipeArm(-1)}>Cancel</button>}
          </div>
        </Row>
        <Row label="Danger zone" hint="permanent">
          <div className="hms">
            {clearArm < 0 && <button className="btn danger" onClick={() => setClearArm(5)}>Clear Library</button>}
            {clearArm > 0 && <button className="btn danger" disabled>Clear in {clearArm}s…</button>}
            {clearArm === 0 && <button className="btn danger solid glow-pulse" onClick={() => { clearLibrary(); setClearArm(-1); }}>Confirm — wipe all</button>}
            {clearArm >= 0 && <button className="btn ghost" onClick={() => setClearArm(-1)}>Cancel</button>}
          </div>
        </Row>
      </Section>
    </div>
  );
});
