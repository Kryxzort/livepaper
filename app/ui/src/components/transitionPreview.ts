// Live transition preview engine for the picker's 16:9 slot.
//
// ONE persistent offscreen WebGL2 context for the whole app (module singleton) renders the effect,
// then blits to the visible canvas via drawImage. The visible canvas mounts/unmounts with the modal,
// but the GL context never does → it can NEVER exhaust (a browser caps ~13 live contexts; creating
// one per picker-open leaked them → getContext()→null → white preview). Each side shows its wallpaper
// THUMBNAIL instantly (no black wait) and upgrades to the live VIDEO once it can decode. Loop:
// A holds 2s → A→B (effect, durMs) → B holds 2s → B→A → repeat; the gl-transitions shader drives it.
//
// Lifecycle: ALWAYS pause+abort+release the <video>s on detach — orphaned autoplay/loop videos keep
// streaming the wallpaper file from the backend over loopback and contend with playback controls.
import { api } from "../api/client";

export interface Source { video?: string; poster: string }
interface Side { vid?: HTMLVideoElement; img?: HTMLImageElement }

const HOLD_MS = 1000; // each wallpaper holds 1s between transitions in the preview loop
// Offscreen render buffer. Grows to the source video's NATIVE height (clamped 720–1080) so the preview
// is as crisp as the wallpaper itself — res was never the perf issue. Always a 16:9 buffer; each frame
// is cover-cropped into it (object-fit: cover) so a non-16:9 video zooms-to-fill instead of stretching.
let rw = 1280, rh = 720;

// ---- the one shared GL context ------------------------------------------------------------------
let glCanvas: HTMLCanvasElement | null = null;
let gl: WebGL2RenderingContext | null = null;
let vao: WebGLVertexArrayObject | null = null;
let texA: WebGLTexture | null = null, texB: WebGLTexture | null = null;
let scratch: HTMLCanvasElement | null = null, sctx: CanvasRenderingContext2D | null = null;
let vert = "";
const programs = new Map<string, WebGLProgram | null>();
const fragCache = new Map<string, string>();

function mkTex(g: WebGL2RenderingContext): WebGLTexture {
  const t = g.createTexture()!; g.bindTexture(g.TEXTURE_2D, t);
  g.texParameteri(g.TEXTURE_2D, g.TEXTURE_MIN_FILTER, g.LINEAR);
  g.texParameteri(g.TEXTURE_2D, g.TEXTURE_MAG_FILTER, g.LINEAR);
  g.texParameteri(g.TEXTURE_2D, g.TEXTURE_WRAP_S, g.CLAMP_TO_EDGE);
  g.texParameteri(g.TEXTURE_2D, g.TEXTURE_WRAP_T, g.CLAMP_TO_EDGE);
  return t;
}
function ensureGL(): boolean {
  if (gl && !gl.isContextLost()) return true;
  if (!glCanvas) { glCanvas = document.createElement("canvas"); glCanvas.width = rw; glCanvas.height = rh; }
  gl = glCanvas.getContext("webgl2", { premultipliedAlpha: false, antialias: false });
  if (!gl) { console.warn("transition preview: no WebGL2 context"); return false; }
  vao = gl.createVertexArray();
  texA = mkTex(gl); texB = mkTex(gl);
  if (!scratch) { scratch = document.createElement("canvas"); scratch.width = rw; scratch.height = rh; sctx = scratch.getContext("2d"); }
  programs.clear(); // programs are context-bound; recompile lazily on the (possibly new) context
  if (!vert) fetch(api.transitionVertUrl()).then((r) => r.text()).then((t) => (vert = t)).catch(() => {});
  return true;
}
function compile(g: WebGL2RenderingContext, type: number, src: string): WebGLShader | null {
  const s = g.createShader(type); if (!s) return null;
  g.shaderSource(s, src); g.compileShader(s);
  if (!g.getShaderParameter(s, g.COMPILE_STATUS)) { console.warn("preview shader", g.getShaderInfoLog(s)); return null; }
  return s;
}

// ---- the active session (one picker-open at a time) --------------------------------------------
let target: HTMLCanvasElement | null = null, tctx: CanvasRenderingContext2D | null = null;
let a: Side = {}, b: Side = {};
let effectId = "", uniforms: { name: string; type: string; default: number[] | null }[] = [];
let durMs = 600, raf = 0, t0 = 0;

// Resize the render buffer to the tallest source's NATIVE height (clamped 720–1080, kept 16:9).
// The WebGL program/textures/VAO are not size-bound, so only the canvases + the visible target need
// resizing; the frame loop re-sets the viewport every call.
function applyRenderSize() {
  let h = 0;
  for (const side of [a, b]) {
    if (side.vid?.videoHeight) h = Math.max(h, side.vid.videoHeight);
    if (side.img?.naturalHeight) h = Math.max(h, side.img.naturalHeight);
  }
  const nh = Math.min(1080, Math.max(720, h || 720));
  const nw = Math.round((nh * 16) / 9);
  if (nw === rw && nh === rh) return;
  rw = nw; rh = nh;
  if (glCanvas) { glCanvas.width = rw; glCanvas.height = rh; }
  if (scratch) { scratch.width = rw; scratch.height = rh; }
  if (target) { target.width = rw; target.height = rh; }
}

function mk(s: Source): Side {
  const img = new Image(); img.crossOrigin = "anonymous"; img.src = s.poster;
  img.addEventListener("load", applyRenderSize);
  let vid: HTMLVideoElement | undefined;
  if (s.video) {
    vid = document.createElement("video");
    vid.crossOrigin = "anonymous"; vid.muted = true; vid.loop = true; vid.playsInline = true;
    vid.addEventListener("loadedmetadata", applyRenderSize); // grow to native res once dimensions known
    vid.src = s.video; vid.play().catch(() => {});
  }
  return { vid, img };
}
function free(side: Side) { const v = side.vid; if (v) { try { v.pause(); v.removeAttribute("src"); v.load(); } catch { /**/ } } }
function disposeSources() { free(a); free(b); a = {}; b = {}; }

function upload(side: Side, tex: WebGLTexture | null) {
  if (!gl || !tex || !sctx || !scratch) return;
  const v = side.vid, img = side.img;
  const useVid = !!(v && v.readyState >= 2);
  const src: CanvasImageSource | null = useVid ? v! : (img && img.complete && img.naturalWidth ? img : null);
  if (!src) return;
  const iw = useVid ? v!.videoWidth : (img!.naturalWidth || rw);
  const ih = useVid ? v!.videoHeight : (img!.naturalHeight || rh);
  gl.bindTexture(gl.TEXTURE_2D, tex);
  try {
    // object-fit: cover — scale the frame to FILL the 16:9 buffer, center, crop the overflow (no stretch).
    const scale = Math.max(rw / iw, rh / ih);
    const dw = iw * scale, dh = ih * scale;
    sctx.clearRect(0, 0, rw, rh);
    sctx.drawImage(src, (rw - dw) / 2, (rh - dh) / 2, dw, dh);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, scratch);
  } catch { /* not decodable yet / tainted — skip this frame */ }
}

function frame() {
  raf = requestAnimationFrame(frame);
  const g = gl; if (!g || !target || !tctx) return;
  g.viewport(0, 0, rw, rh);
  g.clearColor(0, 0, 0, 1); g.clear(g.COLOR_BUFFER_BIT);
  const prog = programs.get(effectId);
  if (prog) {
    upload(a, texA); upload(b, texB);
    // each cycle = hold + forward transition, then SWAP which wallpaper is incoming. So it's always
    // a proper forward play (A→B, then B→A as a fresh forward transition), never the effect rewound.
    const dur = durMs, cycleLen = HOLD_MS + dur;
    const t = performance.now() - t0;
    const cycleIdx = Math.floor(t / cycleLen);
    const within = t % cycleLen;
    const p = within < HOLD_MS ? 0 : Math.min(1, (within - HOLD_MS) / dur);
    const sp = p * p * (3 - 2 * p);
    const flip = cycleIdx % 2 === 1;            // alternate from/to each cycle
    g.useProgram(prog); g.bindVertexArray(vao);
    g.activeTexture(g.TEXTURE0); g.bindTexture(g.TEXTURE_2D, flip ? texB : texA);
    g.activeTexture(g.TEXTURE1); g.bindTexture(g.TEXTURE_2D, flip ? texA : texB);
    g.uniform1i(g.getUniformLocation(prog, "fromTex"), 0);
    g.uniform1i(g.getUniformLocation(prog, "toTex"), 1);
    g.uniform1f(g.getUniformLocation(prog, "progress"), sp);
    const r = g.getUniformLocation(prog, "ratio"); if (r) g.uniform1f(r, rw / rh);
    for (const u of uniforms) {
      const d = u.default; if (!d || !d.length) continue;
      const loc = g.getUniformLocation(prog, u.name); if (!loc) continue;
      switch (u.type) {
        case "float": g.uniform1f(loc, d[0]); break;
        case "int": g.uniform1i(loc, d[0] | 0); break;
        case "bool": g.uniform1i(loc, d[0] ? 1 : 0); break;
        case "vec2": g.uniform2f(loc, d[0], d[1]); break;
        case "vec3": g.uniform3f(loc, d[0], d[1], d[2]); break;
        case "vec4": g.uniform4f(loc, d[0], d[1], d[2], d[3]); break;
        case "ivec2": g.uniform2i(loc, d[0] | 0, d[1] | 0); break;
      }
    }
    g.drawArrays(g.TRIANGLES, 0, 3);
  }
  // blit the offscreen GL canvas onto the visible 2D canvas
  try { tctx.drawImage(glCanvas!, 0, 0, target.width, target.height); } catch { /**/ }
}

export const preview = {
  attach(targetCanvas: HTMLCanvasElement) {
    if (!ensureGL()) return false;
    target = targetCanvas; target.width = rw; target.height = rh; tctx = targetCanvas.getContext("2d");
    return true;
  },
  setSources(sa: Source, sb: Source) { disposeSources(); a = mk(sa); b = mk(sb); },
  setEffect(id: string, u: { name: string; type: string; default: number[] | null }[]) {
    effectId = id; uniforms = u ?? [];
    if (!gl || programs.has(id)) return;
    programs.set(id, null);
    (async () => {
      try {
        let f = fragCache.get(id);
        if (!f) { f = await (await fetch(api.transitionFragUrl(id))).text(); fragCache.set(id, f); }
        if (!gl) return;
        if (!vert) vert = await (await fetch(api.transitionVertUrl())).text();
        const vs = compile(gl, gl.VERTEX_SHADER, vert), fs = compile(gl, gl.FRAGMENT_SHADER, f);
        if (!vs || !fs) return;
        const p = gl.createProgram()!; gl.attachShader(p, vs); gl.attachShader(p, fs); gl.linkProgram(p);
        if (!gl.getProgramParameter(p, gl.LINK_STATUS)) { console.warn("preview link", gl.getProgramInfoLog(p)); return; }
        programs.set(id, p);
      } catch { /* leave null → tile clears black, never white */ }
    })();
  },
  setDuration(ms: number) { durMs = Math.max(100, ms); },
  start() { t0 = performance.now(); if (!raf) raf = requestAnimationFrame(frame); },
  // close the picker: stop the loop + kill the video streams, but KEEP the shared GL context.
  detach() { cancelAnimationFrame(raf); raf = 0; disposeSources(); target = null; tctx = null; },
};
