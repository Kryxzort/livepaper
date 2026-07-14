// API: typed client for the C# --serve backend. The Electron shell launches the window with
// ?api=<port> (read from ~/.config/livepaper/serve.port); browser dev: append ?api=PORT.
const params = new URLSearchParams(location.search);
const port = params.get("api") || localStorage.getItem("lp_api_port") || "";
if (port) localStorage.setItem("lp_api_port", port);
export const API = port ? `http://127.0.0.1:${port}` : "";

// route remote thumbs/gifs through the proxy (UA + Referer + CORS); local files via /media
export const img = (u: string, referer?: string) =>
  `${API}/img?u=${encodeURIComponent(u)}${referer ? `&r=${encodeURIComponent(referer)}` : ""}`;
export const media = (path: string) => `${API}/media?path=${encodeURIComponent(path)}`;
// animated preview: serve remote gifs from a disk cache (/anim, range/length → loops cleanly, like
// local /media) instead of the live /img proxy which flashes black at the gif loop seam.
// gif (remote OR local) → /anim, which trims the leading fade-from-black + serves seekable so it loops
// without a black seam. Other local formats (animated webp) loop fine raw → /media.
export const anim = (u: string) =>
  /^https?:/i.test(u) || /\.gif$/i.test(u) ? `${API}/anim?u=${encodeURIComponent(u)}` : media(u);
// static thumbnail for an animated preview: a non-black/white extracted frame (so AutoPlayGifs-off
// doesn't show the gif's black frame-0, AND so a gif used as a static thumbnail doesn't self-animate).
// Remote URLs and local gif/webp → /still (ffmpeg extract); plain local images → /media as-is.
// w = decode-to-size width. A SIZED request (card layer) always routes through /still so even big
// plain jpgs (WE previews are ~1024–1388px) decode down to the card's px → far less texture memory.
// A NATIVE request (enlarged modal, no w) only needs /still for gif/webp/remote (frame extraction);
// plain local images pass straight to /media uncropped + un-re-encoded (crisp, no quality loss).
export const still = (u: string, w?: number) => {
  if (w) return `${API}/still?u=${encodeURIComponent(u)}&w=${w}`;
  return /^https?:/i.test(u) || /\.(gif|webp)$/i.test(u) ? `${API}/still?u=${encodeURIComponent(u)}` : media(u);
};
// card thumbnails decode to this width (never upscales — min(w,iw) caps at the source). Generous
// enough for Large cards at hidpi; previews are usually ≤640px so most are served at native size.
export const CARD_STILL_W = 640;
// pick the right transport by URL shape: WE-local sources hand us absolute file paths (→ /media),
// everything else is a remote URL (→ /img proxy). Avoids blank WE-local thumbnails.
export const thumb = (u: string, referer?: string) =>
  /^https?:/i.test(u) ? img(u, referer) : media(u);

async function jget<T>(path: string): Promise<T> {
  const r = await fetch(`${API}${path}`);
  if (!r.ok) throw new Error(`${path} → ${r.status}`);
  return r.json();
}
async function jpost<T = void>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const r = await fetch(`${API}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  });
  if (!r.ok) throw new Error(`${path} → ${r.status}`);
  return (r.headers.get("content-type")?.includes("json") ? r.json() : undefined) as Promise<T>;
}

// ---- types (mirror the C# camelCase DTOs) ----
export interface WorkshopFilter {
  sort: string; trendDays: number; type: string;
  ageRating?: string; resolution?: string; genres: string[]; features: string[];
}
export interface Source {
  index: number; name: string; supportsSearch: boolean; supportsPagination: boolean;
  supportsSorting: boolean; supportsTagFilter: boolean; pageSizeHint: number; available: boolean;
}
export interface Wallpaper {
  title: string; thumbnailUrl: string; animatedThumbnailUrl: string | null; pageUrl: string;
  resolution: string | null; isScene: boolean; workshopId: string | null; addedAt: string | null;
  description: string | null; authorName: string | null; fileSizeBytes: number | null;
  subscriptions: number | null; favorites: number | null; views: number | null; tags: string[] | null;
}
export interface Detail {
  title: string; previewUrl: string; downloadUrl: string; needsReferrer: boolean;
  referrer: string | null; isScene: boolean; workshopId: string | null; isWorkshopAcquire: boolean;
  resolution: string | null; ageRating: string | null; youtubeUrl: string | null; pageUrl: string | null;
  authorName: string | null; description: string | null; fileSizeBytes: number | null;
  subscriptions: number | null; favorites: number | null; views: number | null; tags: string[] | null;
}
export interface LibraryItem {
  title: string; videoPath: string; thumbnailPath: string | null; animatedThumbnailPath: string | null;
  sourceId: string | null;
  isScene: boolean; workshopId: string | null; copiedSceneDir: string | null;
  hasCrashed: boolean; isWhitelisted: boolean; volumeOverride: number | null;
  speedOverride: number | null; addedAt: string;
  resolution: string | null; ageRating: string | null; youtubeUrl: string | null; pageUrl: string | null;
  authorName: string | null; description: string | null; fileSizeBytes: number | null;
  subscriptions: number | null; favorites: number | null; views: number | null; tags: string[] | null;
}
export interface PlaylistSettings {
  order: 0 | 1; overrideGlobalSettings: boolean; intervalSeconds: number;
  advanceOnVideoEnd: boolean; waitForVideoEnd: boolean;
  // WE-style transitions (gated by overrideGlobalSettings; else the globalTransition* fallbacks).
  transitionEnabled: boolean; transitionEffectIds: string[];
  transitionDurationMs: number; transitionDurationMaxMs: number; transitionShuffle: boolean;
}
export interface TransitionUniform { name: string; type: string; default: number[] | null }
export interface TransitionEffect {
  id: string; name: string; category: string; defaultOn: boolean;
  author: string; license: string; uniforms: TransitionUniform[] | null;
  reveal?: boolean; // plays live (B keeps playing) in Reveal method; else frozen fallback there
}
export interface CustomPlaylist { videoPaths: string[]; settings: PlaylistSettings; name: string | null; }
export interface Theme {
  name: string; bgBase: string; bgMantle: string; bgCrust: string;
  surface0: string; surface1: string; surface2: string; textColor: string; subtext: string;
  muted: string; accent: string; accentFg: string; accentHover: string;
  danger: string; dangerBg: string; success: string;
}

export const api = {
  sources: () => jget<Source[]>("/sources"),
  browse: (source: number, page: number, query?: string, filter?: WorkshopFilter) =>
    jpost<Wallpaper[]>("/browse", { source, page, query, filter }),
  detail: (source: number, result: Wallpaper) => jpost<Detail>("/detail", { source, result }),
  settings: () => jget<Record<string, unknown>>("/settings"),
  monitors: () => jget<{ name: string; refreshHz: number; primary: boolean }[]>("/monitors"),
  saveSettings: (s: Record<string, unknown>) => jpost("/settings", s),
  mpvPreview: () => fetch(`${API}/mpv-preview`).then((r) => r.text()),
  lwePreview: () => fetch(`${API}/lwe-preview`).then((r) => r.text()),
  importFile: (path: string, title: string) => jpost<LibraryItem>("/library/import", { path, title }),
  libraryClear: () => jpost("/library/clear"),
  clearCache: () => jpost<{ freedBytes: number; count: number }>("/cache/clear"),
  themes: () => jget<Theme[]>("/themes"),
  library: () => jget<LibraryItem[]>("/library"),
  libraryDelete: (item: LibraryItem) => jpost("/library/delete", item),
  libraryVolume: (path: string, intValue: number | null) => jpost("/library/volume", { path, intValue }),
  librarySpeed: (path: string, doubleValue: number | null) => jpost("/library/speed", { path, doubleValue }),
  // live drag preview — set the playing wallpaper's volume/speed NOW, no persist (see /preview)
  preview: (body: { volume?: number; speed?: number }) => jpost("/preview", body),
  libraryWhitelist: (path: string, value: boolean) => jpost("/library/whitelist", { path, value }),
  librarySync: () => jpost<{ added: string[] }>("/library/sync"),
  trash: (items: LibraryItem[]) => jpost<{ batch: string }>("/library/trash", { items }),
  undo: () => jpost<{ ok: boolean; count: number; title: string | null }>("/library/undo"),
  undoDepth: () => jget<{ depth: number }>("/library/undo-depth"),
  download: (source: number, result: Wallpaper, apply: boolean, signal?: AbortSignal) =>
    jpost<LibraryItem & { error?: string }>("/download", { source, result, apply }, signal),
  steamcmdSignin: () => jpost("/steam/steamcmd-signin"),
  transitions: () => jget<TransitionEffect[]>("/transitions"),
  // cache-bust frag/vert: the GLSL is regenerated during dev, and a stale Electron HTTP-cache
  // entry would replay an old (possibly broken) shader. Tiny text, fetched once per picker open.
  transitionFragUrl: (id: string) => `${API}/transitions/frag/${encodeURIComponent(id)}?t=${Date.now()}`,
  transitionVertUrl: () => `${API}/transitions/vert?t=${Date.now()}`,
  transitionPreviewUrl: (which: "a" | "b") => `${API}/transitions/preview/${which}`,
  playlistState: () => jget<CustomPlaylist | null>("/playlist/state"),
  savePlaylistState: (p: CustomPlaylist) => jpost("/playlist/state", p),
  playlistNames: () => jget<string[]>("/playlist/names"),
  savePlaylist: (name: string, playlist: CustomPlaylist) => jpost("/playlist/save", { name, playlist }),
  loadPlaylist: (name: string) => jpost<CustomPlaylist | null>("/playlist/load", { name }),
  playPlaylist: (paths: string[], settings: PlaylistSettings) =>
    jpost<{ message: string }>("/playlist/play", { paths, settings }),
  playFrom: (paths: string[], startIndex: number, settings: PlaylistSettings) =>
    jpost<{ message: string }>("/playlist/play-from", { paths, startIndex, settings }),
  steamStatus: () => jget<{ signedIn: boolean; accountName: string; daysLeft: number | null; mode: string }>("/steam/status"),
  qrStart: () => jpost("/steam/qr/start"),
  qrCancel: () => jpost("/steam/qr/cancel"),
  signout: () => jpost("/steam/signout"),
  deleteFromSource: (items: LibraryItem[]) => jpost<{ batch: string }>("/workshop/delete-from-source", { items }),
  drain: () => jpost("/workshop/drain"),
  workshopQueue: () => jget<{ pending: string[]; hasPending: boolean }>("/workshop/queue"),
  open: (body: { path?: string; url?: string; reveal?: boolean; isScene?: boolean; workshopId?: string | null; copiedSceneDir?: string | null }) => jpost("/open", body),
  duration: (path: string) => jget<{ seconds: number }>(`/library/duration?path=${encodeURIComponent(path)}`),
  apply: (path: string) => jpost("/apply", { path }),
  stop: () => jpost("/stop"),
  next: () => jpost("/next"),
  prev: () => jpost("/prev"),
  random: () => jpost("/random"),
  pause: () => jpost("/pause"),
  current: () => jget<{ path: string | null; sceneId: string | null; playing: boolean; timed: boolean; position: number | null }>("/current"),
};
