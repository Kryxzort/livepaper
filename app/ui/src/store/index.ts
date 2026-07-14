import { create } from "zustand";
import {
  api, type Source, type Wallpaper, type Theme, type LibraryItem, type PlaylistSettings, type WorkshopFilter,
} from "../api/client";

const DEFAULT_FILTER: WorkshopFilter = { sort: "trend", trendDays: 7, type: "", ageRating: "", resolution: "", genres: [], features: [] };

// persist browse view-state (source / filter / sort) across restarts — pure UI prefs, so localStorage
// (survives in Electron) rather than the backend settings.json.
const LS = {
  get<T>(k: string, d: T): T { try { const v = localStorage.getItem("lp." + k); return v == null ? d : (JSON.parse(v) as T); } catch { return d; } },
  set(k: string, v: unknown) { try { localStorage.setItem("lp." + k, JSON.stringify(v)); } catch { /* ignore */ } },
};

// Applies a theme's 16 colors to :root as CSS vars (ThemeService color keys).
// COOL: --accent also drives every glow in the app.
function applyThemeVars(t: Theme) {
  const r = document.documentElement.style;
  const set = (k: string, v: string) => r.setProperty(k, v);
  set("--bg-base", t.bgBase); set("--bg-mantle", t.bgMantle); set("--bg-crust", t.bgCrust);
  set("--surface0", t.surface0); set("--surface1", t.surface1); set("--surface2", t.surface2);
  set("--text", t.textColor); set("--subtext", t.subtext); set("--muted", t.muted);
  set("--accent", t.accent); set("--accent-fg", t.accentFg); set("--accent-hover", t.accentHover);
  set("--danger", t.danger); set("--danger-bg", t.dangerBg); set("--success", t.success);
}

const DEFAULT_PS: PlaylistSettings = {
  order: 0, overrideGlobalSettings: false, intervalSeconds: 1800, advanceOnVideoEnd: true, waitForVideoEnd: false,
  transitionEnabled: false, transitionEffectIds: [], transitionDurationMs: 600, transitionDurationMaxMs: 0, transitionShuffle: true,
};

interface State {
  // browse
  sources: Source[]; activeSource: number; wallpapers: Wallpaper[]; page: number;
  loading: boolean; query: string; hasMore: boolean; downloads: Record<string, number>;
  browseFilter: WorkshopFilter; browseSort: number;
  // library
  libraryItems: LibraryItem[]; libQuery: string; libSort: number; undoDepth: number;
  // playlist
  playlist: string[]; playlistSettings: PlaylistSettings; playlistName: string | null; playlistNames: string[];
  // steam / workshop
  steam: { signedIn: boolean; accountName: string; daysLeft: number | null; mode: string } | null;
  qrOpen: boolean; qrPng: string | null;
  unsub: { done: number; total: number; currentId: string; active: boolean } | null;
  // shared
  themes: Theme[]; theme: Theme | null; settings: Record<string, unknown> | null; playingPath: string | null;
  status: string; error: string | null;
  // per-wallpaper settings modal (lifted so library cards AND the playlist strip can open it)
  settingsItem: LibraryItem | null; settingsSiblings: LibraryItem[];
  settingsMorphId: string | null; // layoutId the modal morphs FROM (the surface that opened it: lib-… or pl-…)
  // PERF/FREEZE: false when the window is hidden OR unfocused (you fullscreened another app over it).
  // On X11 an occluded window often never fires visibilitychange, so Chromium keeps decoding all the
  // autoplay gifs for hours → ThreadPool decode backlog saturates → main thread stalls (futex_wait) →
  // frozen UI on return. Gating gif mounts on this unmounts them when backgrounded → no decode drain.
  appActive: boolean;

  init(): Promise<void>;
  // browse
  setSource(i: number): void; setQuery(q: string): void; reload(): Promise<void>; loadMore(): Promise<void>;
  setBrowseFilter(p: Partial<WorkshopFilter>): void; isWorkshop(): boolean; setBrowseSort(i: number): void;
  download(source: number, w: Wallpaper, apply: boolean): Promise<void>;
  cancelDownload(id: string): void;
  setDownload(id: string, v: number): void;
  stop(): Promise<void>; resetAllSettings(): void;
  addManyToPlaylist(paths: string[]): void; removeManyFromPlaylist(paths: string[]): void;
  steamcmdSignin(): void; setError(e: string): void; dismissError(): void;
  // library
  loadLibrary(): Promise<void>; applyItem(path: string): Promise<void>;
  patchLibraryItem(path: string, partial: Partial<LibraryItem>): void;
  trashItems(items: LibraryItem[]): Promise<void>; undo(): Promise<void>;
  setLibQuery(q: string): void; setLibSort(i: number): void;
  // playlist
  inPlaylist(path: string): boolean; toggleInPlaylist(path: string): void; removeFromPlaylist(path: string): void;
  reorderPlaylist(from: number, to: number): void; playPlaylist(): Promise<void>; playFrom(index: number): Promise<void>;
  setPlaylistSettings(p: Partial<PlaylistSettings>): void;
  savePlaylistAs(name: string): Promise<void>; loadNamedPlaylist(name: string): Promise<void>;
  // settings
  setSetting(key: string, value: unknown): void;
  clearLibrary(): Promise<void>; clearCache(): Promise<void>;
  importFile(path: string, title: string): Promise<void>;
  // steam / workshop
  loadSteam(): Promise<void>; startQr(): Promise<void>; cancelQr(): Promise<void>; signoutSteam(): Promise<void>;
  deleteFromSource(items: LibraryItem[]): Promise<void>; drain(): Promise<void>;
  setQrPng(png: string): void; onSignedIn(s: State["steam"]): void;
  setUnsub(u: State["unsub"]): void;
  openExternal(body: { path?: string; url?: string; reveal?: boolean; isScene?: boolean; workshopId?: string | null; copiedSceneDir?: string | null }): void;
  openItemSettings(item: LibraryItem, siblings?: LibraryItem[], morphId?: string): void; closeItemSettings(): void;
  // shared
  applyTheme(name: string): void; setPlaying(p: string | null): void; setStatus(s: string, timed?: boolean): void;
  refreshPlayingStatus(): void;
}

let searchTimer: ReturnType<typeof setTimeout>;
let filterTimer: ReturnType<typeof setTimeout>;
let stateTimer: ReturnType<typeof setTimeout>;
let settingsTimer: ReturnType<typeof setTimeout>;
let statusTimer: ReturnType<typeof setTimeout>;
const dlCtrls: Record<string, AbortController> = {}; // per-download abort (cancel)

// Reset-to-defaults for the playback/mpv fields.
// Mirrors AppSettings defaults (the user-editable subset — NOT daemon-owned fields like lastSession
// or steam tokens). Used for the per-setting reset icon + "Clear settings". wallpaperEnginePath is
// environment-derived so it's intentionally omitted (no fixed default to reset to).
export const SETTINGS_DEFAULTS: Record<string, unknown> = {
  loop: true, noAudio: true, disableCache: false, advancedSettings: false, videoFps: 0, wallpaperBgAllTabs: false,
  debugMode: false, debugOverlay: true, restartOnSwitchOnly: false, replaceDirectWithWorkshop: false, volume: 100, speed: 1, restartIntervalSeconds: 600, normalizeAudio: false, normalizeTargetLufs: -14,
  globalAdvanceOnVideoEnd: true, globalWaitForVideoEnd: false, globalIntervalSeconds: 1800, autoAddLibraryToPlaylist: false,
  globalTransitionEnabled: false, globalTransitionEffectIds: [], globalTransitionDurationMs: 600, globalTransitionDurationMaxMs: 0, globalTransitionShuffle: true, transitionMethod: "reveal", transitionLagOffsetMs: -30,
  autoMute: false, autoMuteOnlyIfMprisActive: false, autoMuteDelayMs: 200, autoUnmuteDelayMs: 2000, autoMuteThresholdDb: -70,
  hwDec: "auto", videoScale: "fill", demuxerMaxBytes: 20, demuxerMaxBackBytes: 5,
  thumbnailAspect: "1:1", cardSize: "Medium", autoPlayGifs: false,
  weCopyFiles: false, allowScenes: false, autoImportWallpaperEngine: false, sceneTransitionDelayMs: 1000, lweMonitors: [],
  workshopAcquireMode: "subscribe", steamCmdPath: "", steamUsername: "",
  resumeFromLast: true, theme: "Catppuccin Mocha",
};

export const useStore = create<State>((set, get) => ({
  sources: [], activeSource: 0, wallpapers: [], page: 1, loading: false, query: "", hasMore: true, downloads: {},
  browseFilter: { ...DEFAULT_FILTER, ...LS.get<Partial<WorkshopFilter>>("browseFilter", {}) }, browseSort: LS.get("browseSort", 0),
  libraryItems: [], libQuery: "", libSort: LS.get("libSort", 5), undoDepth: 0,
  playlist: [], playlistSettings: DEFAULT_PS, playlistName: null, playlistNames: [],
  steam: null, qrOpen: false, qrPng: null, unsub: null,
  themes: [], theme: null, settings: null, playingPath: null, status: "", error: null,
  settingsItem: null, settingsSiblings: [], settingsMorphId: null, appActive: true,

  async init() {
    const [sources, themes, settings, cur, lib, pstate, names, depth, steam] = await Promise.all([
      api.sources(), api.themes(), api.settings(), api.current().catch(() => null),
      api.library().catch(() => []), api.playlistState().catch(() => null),
      api.playlistNames().catch(() => []), api.undoDepth().catch(() => ({ depth: 0 })),
      api.steamStatus().catch(() => null),
    ]);
    const themeName = (settings.theme as string) || themes[0]?.name;
    const theme = themes.find((t) => t.name === themeName) ?? themes[0] ?? null;
    if (theme) applyThemeVars(theme);
    // restore the saved source if it's still available, else first available
    const savedSrc = LS.get<number>("activeSource", -1);
    const avail = new Set(sources.filter((s) => s.available).map((s) => s.index));
    set({
      sources, themes, settings, theme,
      activeSource: avail.has(savedSrc) ? savedSrc : (sources.find((s) => s.available)?.index ?? 0),
      playingPath: cur?.path ?? null, libraryItems: lib, playlistNames: names, undoDepth: depth.depth,
      playlist: pstate?.videoPaths ?? [], playlistSettings: pstate?.settings ?? DEFAULT_PS,
      playlistName: pstate?.name ?? null, steam,
    });
    get().refreshPlayingStatus(); // show the now-playing line if something's already playing
    await get().reload();
  },

  // ---- browse ----
  setSource(i) { set({ activeSource: i }); LS.set("activeSource", i); get().reload(); },
  setQuery(q) { set({ query: q }); clearTimeout(searchTimer); searchTimer = setTimeout(() => get().reload(), 200); },
  isWorkshop() { return !!get().sources[get().activeSource]?.supportsTagFilter; },
  // update the filter instantly (UI is snappy) but DEBOUNCE the network reload — rapid chip toggles
  // would otherwise fire a full refetch + grid re-render each → laggy.
  setBrowseFilter(p) { const bf = { ...get().browseFilter, ...p }; set({ browseFilter: bf }); LS.set("browseFilter", bf); clearTimeout(filterTimer); filterTimer = setTimeout(() => get().reload(), 280); },
  setBrowseSort(i) { set({ browseSort: i }); LS.set("browseSort", i); }, // client-side (WE-local; single page)
  async reload() {
    const { activeSource, query } = get();
    const filter = get().isWorkshop() ? get().browseFilter : undefined;
    set({ loading: true, page: 1 });
    try {
      const list = await api.browse(activeSource, 1, query || undefined, filter);
      set({ wallpapers: list, hasMore: !!get().sources[activeSource]?.supportsPagination && list.length > 0 });
    } finally { set({ loading: false }); }
  },
  async loadMore() {
    const { activeSource, query, page, loading, hasMore, wallpapers } = get();
    if (loading || !hasMore) return;
    const filter = get().isWorkshop() ? get().browseFilter : undefined;
    set({ loading: true });
    try {
      const next = page + 1;
      const list = await api.browse(activeSource, next, query || undefined, filter);
      // Dedup by pageUrl — "Most Popular" et al. shift between requests, so pages overlap
      // pages overlap; appending dupes = duplicate React keys → reorder/jump. Keep existing order, add
      // only fresh items at the bottom, and stop when a page yields nothing new.
      const existing = new Set(wallpapers.map((w) => w.pageUrl));
      const fresh = list.filter((w) => !existing.has(w.pageUrl));
      if (fresh.length === 0) { set({ page: next, hasMore: false }); return; }
      set({ wallpapers: [...wallpapers, ...fresh], page: next, hasMore: list.length > 0 });
    } finally { set({ loading: false }); }
  },
  async download(source, w, apply) {
    const ctrl = new AbortController(); dlCtrls[w.pageUrl] = ctrl;
    set({ downloads: { ...get().downloads, [w.pageUrl]: 0 } });
    try {
      const item = await api.download(source, w, apply, ctrl.signal);
      if ("error" in item && item.error) { get().setError(`Download failed: ${item.error}`); return; }
      await get().loadLibrary();
      get().setStatus(apply ? `Applied: ${w.title}` : `Downloaded: ${w.title}`);
    } catch (e) {
      if (ctrl.signal.aborted) get().setStatus(`Cancelled: ${w.title}`);
      else get().setError(`Download failed: ${(e as Error).message}`); // error modal
    } finally { delete dlCtrls[w.pageUrl]; const d = { ...get().downloads }; delete d[w.pageUrl]; set({ downloads: d }); }
  },
  cancelDownload(id) { dlCtrls[id]?.abort(); }, // cancel an in-flight download
  setDownload(id, v) { set({ downloads: { ...get().downloads, [id]: v } }); },
  async stop() { await api.stop(); set({ playingPath: null }); get().setStatus("Stopped"); },
  resetAllSettings() {
    // spread defaults over current → preserves daemon-owned fields (lastSession, steam tokens) the
    // backend also guards; re-applies the default theme's CSS vars (settings.theme alone won't).
    const s = { ...(get().settings ?? {}), ...SETTINGS_DEFAULTS };
    set({ settings: s }); api.saveSettings(s).catch(() => {});
    get().applyTheme(SETTINGS_DEFAULTS.theme as string);
    get().setStatus("Settings cleared — reset to defaults");
  },
  addManyToPlaylist(paths) {
    const set0 = new Set(get().playlist);
    set({ playlist: [...get().playlist, ...paths.filter((p) => !set0.has(p))] }); savePlaylistDebounced(get);
  },
  removeManyFromPlaylist(paths) {
    const rm = new Set(paths);
    set({ playlist: get().playlist.filter((p) => !rm.has(p)) }); savePlaylistDebounced(get);
  },
  steamcmdSignin() { api.steamcmdSignin().then(() => get().setStatus("steamcmd sign-in launched in terminal")).catch((e) => get().setError(String(e))); },
  setError(e) { set({ error: e }); },
  dismissError() { set({ error: null }); },

  // ---- library ----
  async loadLibrary() { set({ libraryItems: await api.library(), undoDepth: (await api.undoDepth()).depth }); },
  // optimistic per-item patch (volume/speed override etc.) so consumers — incl. the live WallpaperBg —
  // reflect a change instantly without waiting for a full library reload.
  patchLibraryItem(path, partial) { set((s) => ({ libraryItems: s.libraryItems.map((i) => i.videoPath === path ? { ...i, ...partial } : i) })); },
  async applyItem(path) {
    await api.apply(path); set({ playingPath: path });
    const t = get().libraryItems.find((i) => i.videoPath === path)?.title ?? (path.split("/").pop() ?? "").replace(/\.[^.]+$/, "");
    get().setStatus(`Applied: ${t}`);
  },
  async trashItems(items) {
    if (items.length === 0) return;
    await api.trash(items);
    await get().loadLibrary();
    const n = items.length;
    get().setStatus(n > 1 ? `Deleted ${n} wallpapers` : `Deleted: ${items[0].title}`);
  },
  async undo() {
    const r = await api.undo();
    if (!r.ok) return;
    await get().loadLibrary();
    get().setStatus(r.count > 1 ? `Restored ${r.count} wallpapers` : `Restored: ${r.title ?? ""}`);
  },
  setLibQuery(q) { set({ libQuery: q }); },
  setLibSort(i) { set({ libSort: i }); LS.set("libSort", i); },

  // ---- playlist ----
  inPlaylist(path) { return get().playlist.includes(path); },
  toggleInPlaylist(path) {
    const p = get().playlist;
    set({ playlist: p.includes(path) ? p.filter((x) => x !== path) : [...p, path] });
    savePlaylistDebounced(get);
  },
  removeFromPlaylist(path) { set({ playlist: get().playlist.filter((x) => x !== path) }); savePlaylistDebounced(get); },
  reorderPlaylist(from, to) {
    const p = [...get().playlist]; const [m] = p.splice(from, 1); p.splice(to, 0, m);
    set({ playlist: p }); savePlaylistDebounced(get);
  },
  async playPlaylist() {
    const { playlist, playlistSettings } = get();
    if (playlist.length === 0) { get().setStatus("Playlist is empty"); return; }
    const r = await api.playPlaylist(playlist, playlistSettings);
    if (r.message) { get().setStatus(r.message); return; }   // error/interval messages
    set({ playingPath: playlist[0] }); get().refreshPlayingStatus(); // persistent now-playing line
  },
  async playFrom(index) {
    const { playlist, playlistSettings } = get();
    const r = await api.playFrom(playlist, index, playlistSettings);
    if (r.message) { get().setStatus(r.message); return; }
    set({ playingPath: playlist[index] }); get().refreshPlayingStatus();
  },
  setPlaylistSettings(p) {
    set({ playlistSettings: { ...get().playlistSettings, ...p } }); savePlaylistDebounced(get);
    if (get().playingPath) get().refreshPlayingStatus();
  },
  async savePlaylistAs(name) {
    await api.savePlaylist(name, { videoPaths: get().playlist, settings: get().playlistSettings, name });
    set({ playlistName: name, playlistNames: await api.playlistNames() });
    get().setStatus(`Saved playlist "${name}"`);
  },
  async loadNamedPlaylist(name) {
    const p = await api.loadPlaylist(name);
    if (p) {
      set({ playlist: p.videoPaths, playlistSettings: p.settings, playlistName: name }); savePlaylistDebounced(get);
      get().setStatus(`Loaded playlist "${name}" (${p.videoPaths.length} wallpapers)`);
    }
  },

  // ---- settings ----
  setSetting(key, value) {
    const s = { ...(get().settings ?? {}), [key]: value };
    set({ settings: s });
    clearTimeout(settingsTimer);
    settingsTimer = setTimeout(() => api.saveSettings(s).catch(() => {}), 250);
    // vol/loop/speed/rotation changes refresh the now-playing line live
    if (get().playingPath && ["volume", "noAudio", "loop", "speed", "globalAdvanceOnVideoEnd", "globalIntervalSeconds"].includes(key))
      get().refreshPlayingStatus();
  },
  async clearLibrary() { await api.libraryClear(); await get().loadLibrary(); get().setStatus("Library cleared"); },
  async clearCache() {
    try { const r = await api.clearCache(); get().setStatus(`Cache cleared — freed ${(r.freedBytes / (1 << 20)).toFixed(0)} MB (${r.count} files)`); }
    catch { get().setStatus("Failed to clear cache"); }
  },
  async importFile(path, title) {
    try { await api.importFile(path, title); await get().loadLibrary(); get().setStatus(`Imported: ${title}`); }
    catch (e) { get().setStatus(`Import failed: ${(e as Error).message}`); }
  },

  // ---- steam / workshop ----
  async loadSteam() { set({ steam: await api.steamStatus() }); },
  async startQr() { set({ qrOpen: true, qrPng: null }); await api.qrStart(); },
  async cancelQr() { await api.qrCancel(); set({ qrOpen: false, qrPng: null }); },
  async signoutSteam() { await api.signout(); await get().loadSteam(); },
  async deleteFromSource(items) {
    if (items.length === 0) return;
    await api.deleteFromSource(items); await get().loadLibrary();
    get().setStatus(`Deleting ${items.length} from Steam`);
    get().drain();
  },
  async drain() { set({ unsub: { done: 0, total: 0, currentId: "", active: true } }); await api.drain(); },
  setQrPng(png) { set({ qrPng: png }); },
  onSignedIn(s) { set({ steam: s, qrOpen: false, qrPng: null }); get().setStatus("Signed in to Steam"); },
  setUnsub(u) { set({ unsub: u }); },

  openExternal(body) { api.open(body).catch(() => {}); },

  openItemSettings(item, siblings, morphId) { set({ settingsItem: item, settingsSiblings: siblings ?? [item], settingsMorphId: morphId ?? null }); },
  closeItemSettings() { set({ settingsItem: null, settingsSiblings: [], settingsMorphId: null }); get().loadLibrary(); },

  // ---- shared ----
  applyTheme(name) {
    const theme = get().themes.find((t) => t.name === name);
    if (!theme) return;
    applyThemeVars(theme); set({ theme });
    const s = { ...(get().settings ?? {}), theme: name };
    set({ settings: s }); api.saveSettings(s).catch(() => {});
  },
  setPlaying(p) { set({ playingPath: p }); get().refreshPlayingStatus(); },
  // Transient status message for 5s, then revert to the now-playing line.
  setStatus(s, timed = true) {
    clearTimeout(statusTimer);
    set({ status: s });
    if (timed && s) statusTimer = setTimeout(() => { if (get().status === s) set({ status: buildPlayingStatus(get) }); }, 5000);
  },
  // Persistent "now playing" line (no auto-clear).
  refreshPlayingStatus() { clearTimeout(statusTimer); set({ status: buildPlayingStatus(get) }); },
}));

// Format an interval (e.g. "1h 30m", "45s").
function fmtInterval(total: number): string {
  const p: string[] = []; const h = Math.floor(total / 3600), m = Math.floor((total % 3600) / 60), s = total % 60;
  if (h) p.push(`${h}h`); if (m) p.push(`${m}m`); if (s || !p.length) p.push(`${s}s`);
  return p.join(" ");
}
// Now-playing line — playlist/wallpaper • Vol% • Loop • speed×.
function buildPlayingStatus(get: () => State): string {
  const st = get();
  if (!st.playingPath) return "";
  const set = st.settings ?? {};
  const titleOf = (p: string) => st.libraryItems.find((i) => i.videoPath === p)?.title
    ?? (p.split("/").pop() ?? "").replace(/\.[^.]+$/, "");
  const parts: string[] = [];
  const pl = st.playlist, ps = st.playlistSettings;
  const inPl = pl.length > 1 && pl.includes(st.playingPath);
  const advance = ps.overrideGlobalSettings ? ps.advanceOnVideoEnd : ((set.globalAdvanceOnVideoEnd as boolean) ?? true);
  const interval = ps.overrideGlobalSettings ? ps.intervalSeconds : ((set.globalIntervalSeconds as number) ?? 1800);
  const shuffle = ps.order === 1;
  const cur = titleOf(st.playingPath);
  if (inPl) {
    let desc = advance ? `${pl.length} wallpapers, on video end` : `${pl.length} wallpapers, every ${fmtInterval(interval)}`;
    if (shuffle) desc += " (shuffled)";
    if (cur) desc += ` → ${cur}`;
    parts.push(desc);
  } else if (cur) {
    parts.push(cur);
  }
  const vol = (set.volume as number) ?? 100;
  parts.push(set.noAudio ? `Vol ${vol}% (muted)` : `Vol ${vol}%`);
  if ((set.loop as boolean) ?? true) parts.push("Loop");
  const spd = (set.speed as number) ?? 1;
  if (Math.abs(spd - 1) > 0.01) parts.push(`${spd}×`);
  return parts.join(" • ");
}

function savePlaylistDebounced(get: () => State) {
  clearTimeout(stateTimer);
  stateTimer = setTimeout(() => {
    const { playlist, playlistSettings, playlistName } = get();
    api.savePlaylistState({ videoPaths: playlist, settings: playlistSettings, name: playlistName }).catch(() => {});
  }, 200);
}
