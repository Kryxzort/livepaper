// lp-transition — livepaper's wallpaper-transition renderer.
//
// Draws a gl-transitions effect from one frame (`from`) to another (`to`) on a
// per-output wlr-layer-shell surface on the BOTTOM layer (above the wallpaper,
// below app windows), then exits. Fully opaque + input-transparent: it covers
// only the wallpaper region (real windows stay on top, interactive).
//
// The caller (livepaper's TransitionService) hands it everything it needs:
//   - the composed vertex + fragment shader (transitions/wrap.* + the effect body)
//   - the effect's uniform defaults (kept as GLSL uniforms, set here)
//   - raw RGBA8 frame buffers per output (ffmpeg normalizes every source to raw)
// so this binary needs no image library and no knowledge of the effect catalog.
//
// Full-live: when given the actual video files (--from-video/--to-video) it decodes BOTH sides with
// libmpv (render API → FBO texture per side) so they keep PLAYING through the effect, instead of two
// frozen stills. The --from/--to raws are still required as the warmup fallback (and the only source
// for a scene side, which has no video). mpvpaper plays the incoming B live underneath, revealed at
// teardown — so no --mpv-unpause is needed in the live path.
//
// Usage (repeat the --output block once per monitor):
//   lp-transition --duration-ms 600 --vert WRAP.vert --frag COMPOSED.frag
//     [--uniform NAME TYPE V...] ...
//     [--from-video A.mp4 --from-start 12.3] [--to-video B.mp4 --to-start 0]
//     --output DP-1 --from a.raw --to b.raw --width 1920 --height 1080
//     [--output DP-2 ...]
//     [--ready-file PATH] [--on-finish "shell command"]
#define _POSIX_C_SOURCE 200809L
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdarg.h>
#include <math.h>
#include <time.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/time.h>
#include <wayland-client.h>
#include <wayland-egl.h>
#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <mpv/client.h>
#include <mpv/render_gl.h>
#include "wlr-layer-shell-unstable-v1-protocol.h"

#define MAX_OUTPUTS 8
#define MAX_UNIFORMS 16

// ---- CLI-parsed config -----------------------------------------------------
struct uniform_def {
    char name[64];
    char type[8];      // float|int|bool|vec2|vec3|vec4|ivec2
    float v[4];
    int n;             // value count
};
struct out_cfg {
    char name[64];     // connector, e.g. "DP-1"
    char *from, *to;   // raw RGBA8 file paths
    int w, h;          // texture dimensions
};
static struct out_cfg out_cfgs[MAX_OUTPUTS];
static int n_out_cfgs = 0;
static struct uniform_def uniforms[MAX_UNIFORMS];
static int n_uniforms = 0;
static double duration_s = 0.6;
static char *vert_path = NULL, *frag_path = NULL, *on_finish = NULL, *mpv_unpause = NULL;
static char *from_af = NULL, *to_af = NULL; // loudnorm filter for the overlay's A/B audio (normalization)
static double from_speed = 1.0, to_speed = 1.0; // per-side playback speed (matches mpvpaper's setting)
static int from_loop = 1, to_loop = 1;          // per-side DESKTOP loop semantic (mpvpaper's loop-file):
                                                // loop ctx → a too-short side wraps through the effect
                                                // like the wallpaper would; off → freeze on last frame
static int g_fps = 0;              // cap decode fps (0 = native) — mirrors VideoFps
static char *g_hwdec = NULL;       // hwdec mode (mirrors HwDec); NULL → auto-safe
static char *ctl_sock = NULL; static int ctl_fd = -1; // control socket: live mute/volume during the effect
// Full-live: decode the actual videos with libmpv so BOTH sides keep PLAYING during the effect
// (not two frozen stills). When a side has a video, its libmpv frame replaces the still texture
// each frame once decoding is up; the still (--from/--to raw) is the fallback until then / for scenes.
static char *from_video = NULL, *to_video = NULL; // file paths (NULL = use the still / scene grab)
static double from_start = 0.0, to_start = 0.0;   // seek-in seconds (A = its live pos, B = 0)
static double from_epoch = 0.0;                    // CLOCK_REALTIME when from_start was sampled; A is
                                                   // advanced by elapsed-since → lands on mpvpaper-A's live pos
static double from_duration = 0.0;                 // A's length → wrap the advanced start (looping videos)
static bool to_paused = false;                     // hold B at frame 0 until first paint (lockstep handoff)
static double audio_volume = 0.0;                  // effective wallpaper volume (0 = muted → no overlay audio);
static char *ready_file = NULL;                    // touched after the first frame is presented

// Transition method (chosen by the backend, --method). FROZEN = both stills, opaque, B0→B0 handoff;
// REVEAL = frozen A revealed over the LIVE wallpaper-B underneath (alpha, no overlay decoders, B is
// untouched/normal playback); FULL_LIVE = overlay decodes A+B live (the heavy machinery above).
enum tr_method { M_FROZEN, M_REVEAL, M_FULL_LIVE };
static enum tr_method method = M_FROZEN;
static GLuint transparent_tex = 0;                 // 1×1 transparent → REVEAL's toTex (reveals B underneath)
static bool scale_fill = true;                     // --scale fill|fit → panscan on the live full-live decoders

// ---- Wayland globals -------------------------------------------------------
static struct wl_display *display;
static struct wl_compositor *compositor;
static struct zwlr_layer_shell_v1 *layer_shell;

struct out_info {                 // a discovered wl_output + its connector name
    struct wl_output *output;
    char name[64];
    bool named;
};
static struct out_info wl_outs[MAX_OUTPUTS];
static int n_wl_outs = 0;

// ---- EGL (shared across surfaces) -----------------------------------------
static EGLDisplay egl_dpy;
static EGLConfig egl_cfg;
static EGLContext egl_ctx = EGL_NO_CONTEXT;

// ---- a live libmpv video source rendered into an offscreen FBO texture -----
// One mpv instance + render context per side per surface (the FBO is surface-sized). Until the
// first frame is decoded, `ready` is false and the renderer samples the still fallback instead.
struct vid_src {
    mpv_handle *mpv;
    mpv_render_context *rc;
    GLuint fbo, tex;
    int w, h;
    bool ready;
    bool paused;   // loaded paused; unpaused on the surface's first render (lockstep with mpvpaper B)
    const char *tag;    // "A"/"B" for logging
    long uploads;       // mpv_render_context_render calls = actual texture updates (displayed-frame rate)
    long last_uploads;  // for per-interval delta
    double first_frame; // monotonic time of the first frame
};

// ---- diagnostic timing log (-> --log <file>) -------------------------------
static FILE *g_log = NULL;
static double proc_t0 = 0.0;
static double next_stat = 0.0;       // next per-interval stats dump (s since go)
static long last_disp_frames = 0;    // display-frame count at last stats dump
static double now_s(void);
static void logt(const char *fmt, ...) {
    if (!g_log) return;
    fprintf(g_log, "[+%6.0fms] ", (now_s() - proc_t0) * 1000.0);
    va_list ap; va_start(ap, fmt); vfprintf(g_log, fmt, ap); va_end(ap);
    fputc('\n', g_log); fflush(g_log);
}
struct vid_src;
// Dump a decoder's real internal state: playback position (advancing? = not stalled), texture-upload
// delta this interval (= the displayed-frame rate — low = "repeated frames"), mpv's own fps estimate,
// dropped frames, the DECODED dimensions, and — critically — whether hwdec is active or it fell back
// to software (slow). codec too. This is the ground truth the UPDATE_FRAME counter couldn't give.
static void log_mpv_stats(struct vid_src *v);

// ---- per-surface render state ---------------------------------------------
struct surface {
    struct out_cfg *cfg;
    struct wl_surface *wl_surface;
    struct zwlr_layer_surface_v1 *layer_surface;
    struct wl_egl_window *egl_window;
    EGLSurface egl_surface;
    GLuint prog, vao, tex_from, tex_to;
    GLint u_progress, u_ratio;
    int w, h;                     // surface size from configure
    bool configured;
    bool done;                    // reached progress>=1 and presented
    bool primed;                  // (legacy flag; unused)
};
static struct surface surfaces[MAX_OUTPUTS];
static int n_surfaces = 0;

static double start_time = -1.0;
static double reveal_hold = 0.0;   // reveal/frozen: hold OPAQUE (progress 0) this long before animating —
                                   // covers while a slow LWE scene-B spins up underneath, then reveals it
static double hold_end_s = 0.0;    // reveal-a: after the effect completes, hold the final (opaque-B) frame
                                   // this long so the backend can launch mpvpaper-B UNDER the opaque cover
                                   // before teardown (B isn't at BACKGROUND during reveal-a → no handoff else)
static double end_reached = -1.0;  // wall-clock when progress first hit 1 (for hold_end_s)
static bool mpv_seek_b = false;    // reveal-a: at teardown, seek mpvpaper-B to the overlay-B (g_vto) exact
                                   // position before unpausing → the late-launched mpvpaper-B resumes where
                                   // the overlay's B was (V→V gets this free by playing in sync; reveal-a can't)
static bool reveal_b = false;      // full-live variant: decode A LIVE but reveal a live scene-B underneath
                                   // (transparent toTex, no B decoder) — V→S full-live ("reveal-b")
static bool reveal_a = false;      // MIRROR for S→V: decode B LIVE in the overlay + reveal the live
                                   // scene-A underneath (transparent fromTex, no A decoder/still). The
                                   // compositor blends B over the live scene → no scene-A capture needed.
static double go_time = -1.0;      // effect clock start — set once the shared decoders have a live frame
static bool align_a_end = false;   // gate the effect start on A's DECODED remaining so progress hits 1
                                   // exactly at A's EOF (timed video-end advance). CLOSED-LOOP: the
                                   // backend fires EARLY with slack; the gate spends the slack showing
                                   // live A at p=0 (indistinguishable from the wallpaper) — no prediction.
static bool a_gate_open = false;   // gate released → effect clock running
static double gate_t0 = -1.0;      // when the gate started holding (logging)
static bool a_end_logged = false;  // one-shot A-END accuracy log at p=1
static double a_tick_pos = -1.0;   // last observed A time-pos (tick detection for sub-frame anchoring)
static double a_target_go = -1.0;  // exact wall-clock instant the effect must start (A_EOF − need)
static bool a_target_logged = false;
static bool a_loop_pinned = false; // long-clip align: A pinned to loop-file=no (freeze-at-EOF failsafe)
#define A_HOLD_MAX 8.0             // max seconds the gate may hold (covers backend slack; a target
                                   // further out = alignment opportunity missed → just start)
static bool b_preseeked = false;   // mpvpaper's paused B has been pre-seeked to the handoff position
static bool ready_signaled = false; // ready-file touched (delayed past go so the opaque frame is on screen)
static bool fx_paused = false;      // effect frozen (pause keybind during the transition)
static double pause_started = 0.0, pause_accum = 0.0; // freeze the progress clock across pause windows
static double cur_speed = 1.0;     // overlay-A's current playback speed (closed-loop position tracking)
static double in_band_t = -1.0;    // when A entered the lag deadband at 1x (must hold before covering)
// Aim A slightly AHEAD of live to cancel the controller's settling drift (A drifts ~this far behind
// during the final 1x hold = actuator latency). Machine-dependent ~0.02–0.05; can be overridden at
// runtime via --lag-offset for portability (self-calibrated from logged residuals by the backend).
static double lag_target = -0.03;
#define LAG_BAND 0.05              // cover once |A − (live+target)| stays within this (s)
#define BAND_HOLD 0.080            // …and holds for this long at 1x (lets the speed-change drain settle)
static long frame_count = 0;       // for fps reporting (LP_TRANSITION_FPS=1)
// FULL_LIVE: ONE shared decoder pair (decode A/B once into the shared GL context; every output samples
// these textures → halves the decode on multi-monitor + one audio stream). Created on the first output.
static struct vid_src g_vfrom, g_vto;
static bool g_decoders_inited = false;

// Send one mpv IPC command (JSON line) to mpvpaper's socket. Used for the frame-accurate B handoff:
// PRE-SEEK the paused/hidden B to the handoff position partway through the effect (so its frame is
// fully decoded well before the reveal — no decode-latency jump-to-0), then UNPAUSE at teardown.
static void mpv_cmd(const char *sock_path, const char *json_line) {
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return;
    struct sockaddr_un addr = { .sun_family = AF_UNIX };
    snprintf(addr.sun_path, sizeof addr.sun_path, "%s", sock_path);
    if (connect(fd, (struct sockaddr *)&addr, sizeof addr) == 0) {
        ssize_t w = write(fd, json_line, strlen(json_line)); (void)w;
    }
    close(fd);
}

// Query a numeric property from a running mpv (e.g. mpvpaper) over its IPC socket. Returns -1 on
// failure. Bounded by a short recv timeout so it can't stall the render loop. mpv may emit event
// lines before the reply, so scan a few reads for the "data" field.
static double mpv_query_double(const char *sock_path, const char *prop) {
    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return -1.0;
    struct sockaddr_un addr = { .sun_family = AF_UNIX };
    snprintf(addr.sun_path, sizeof addr.sun_path, "%s", sock_path);
    double val = -1.0;
    if (connect(fd, (struct sockaddr *)&addr, sizeof addr) == 0) {
        struct timeval tv = { .tv_sec = 0, .tv_usec = 60000 }; // 60ms cap
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof tv);
        static int req_seq = 1000;
        int rid = ++req_seq;                       // unique per call → never match a stale/other reply
        char req[160], ridtag[32];
        snprintf(req, sizeof req, "{\"command\":[\"get_property\",\"%s\"],\"request_id\":%d}\n", prop, rid);
        snprintf(ridtag, sizeof ridtag, "\"request_id\":%d", rid);
        if (write(fd, req, strlen(req)) > 0) {
            char buf[4096]; size_t len = 0;
            for (int i = 0; i < 10; i++) {
                ssize_t n = recv(fd, buf + len, sizeof buf - 1 - len, 0);
                if (n <= 0) break;
                len += (size_t)n; buf[len] = 0;
                // parse only COMPLETE lines, and only OUR reply line (events / other replies carry a
                // different or no request_id — the old code grabbed any "data", which is what glitched).
                char *line = buf, *nl;
                while ((nl = strchr(line, '\n')) != NULL) {
                    *nl = 0;
                    if (strstr(line, ridtag)) {
                        char *d = strstr(line, "\"data\":");
                        if (d) { d += 7; while (*d == ' ') d++; if (strncmp(d, "null", 4) != 0) val = atof(d); }
                        close(fd); return val;     // matched our reply → done
                    }
                    line = nl + 1;
                }
                size_t rem = strlen(line); memmove(buf, line, rem + 1); len = rem; // keep partial tail
            }
        }
    }
    close(fd);
    return val;
}

static double now_s(void) {
    struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}
// wall clock, shared with the backend (CLOCK_REALTIME == unix time) — used to land A on mpvpaper-A's
// live position regardless of how long after sampling the overlay's A decoder actually loads.
static double now_realtime(void) {
    struct timespec t; clock_gettime(CLOCK_REALTIME, &t);
    return t.tv_sec + t.tv_nsec / 1e9;
}
static double smoothstep01(double x) {
    if (x < 0) x = 0;
    if (x > 1) x = 1;
    return x * x * (3.0 - 2.0 * x);
}

// ---- file + GL helpers -----------------------------------------------------
static char *read_file(const char *path) {
    FILE *f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "lp-transition: cannot open %s\n", path); return NULL; }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    char *b = malloc(n + 1);
    if (fread(b, 1, n, f) != (size_t)n) { fclose(f); free(b); return NULL; }
    b[n] = 0; fclose(f); return b;
}
static uint8_t *read_raw(const char *path, int w, int h) {
    FILE *f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "lp-transition: cannot open raw %s\n", path); return NULL; }
    size_t need = (size_t)w * h * 4;
    uint8_t *b = malloc(need);
    size_t got = fread(b, 1, need, f); fclose(f);
    if (got != need) { fprintf(stderr, "lp-transition: %s: got %zu want %zu\n", path, got, need); free(b); return NULL; }
    return b;
}
static GLuint compile(GLenum type, const char *src) {
    GLuint s = glCreateShader(type);
    glShaderSource(s, 1, &src, NULL); glCompileShader(s);
    GLint ok = 0; glGetShaderiv(s, GL_COMPILE_STATUS, &ok);
    if (!ok) { char log[2048]; glGetShaderInfoLog(s, sizeof log, NULL, log);
        fprintf(stderr, "lp-transition: shader compile failed:\n%s\n", log); return 0; }
    return s;
}
static GLuint upload_tex(const char *path, int w, int h) {
    uint8_t *px = read_raw(path, w, h);
    if (!px) return 0;
    GLuint t; glGenTextures(1, &t); glBindTexture(GL_TEXTURE_2D, t);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, px);
    free(px);
    return t;
}
// 1×1 fully-transparent texture — REVEAL binds it as toTex so the effect's "to" regions are
// see-through and the compositor shows the live wallpaper-B playing on the layer underneath.
static GLuint mk_transparent_tex(void) {
    GLuint t; glGenTextures(1, &t); glBindTexture(GL_TEXTURE_2D, t);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    const unsigned char px[4] = { 0, 0, 0, 0 };
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA, 1, 1, 0, GL_RGBA, GL_UNSIGNED_BYTE, px);
    return t;
}
static void apply_uniforms(GLuint prog) {
    for (int i = 0; i < n_uniforms; i++) {
        struct uniform_def *u = &uniforms[i];
        GLint loc = glGetUniformLocation(prog, u->name);
        if (loc < 0) continue;
        if (!strcmp(u->type, "float")) glUniform1f(loc, u->v[0]);
        else if (!strcmp(u->type, "int")) glUniform1i(loc, (int)u->v[0]);
        else if (!strcmp(u->type, "bool")) glUniform1i(loc, u->v[0] != 0.0f);
        else if (!strcmp(u->type, "vec2")) glUniform2f(loc, u->v[0], u->v[1]);
        else if (!strcmp(u->type, "vec3")) glUniform3f(loc, u->v[0], u->v[1], u->v[2]);
        else if (!strcmp(u->type, "vec4")) glUniform4f(loc, u->v[0], u->v[1], u->v[2], u->v[3]);
        else if (!strcmp(u->type, "ivec2")) glUniform2i(loc, (int)u->v[0], (int)u->v[1]);
    }
}

// ---- live video sources (libmpv render API → FBO texture) ------------------
static void *get_proc(void *ctx, const char *name) { (void)ctx; return (void *)eglGetProcAddress(name); }

// Spin up a libmpv decoder for `path` (seeking to `start`), rendering into an FBO-backed texture
// sized w×h. Returns false on any failure → the caller leaves `ready=false` and the still fallback
// is sampled instead. Audio off (the desktop owns audio), looped, hw-decoded.
static bool vidsrc_init(struct vid_src *v, const char *path, double start, int w, int h, bool start_paused, bool with_audio, const char *af, double speed, const char *tag) {
    v->w = w; v->h = h; v->ready = false; v->paused = start_paused; v->tag = tag;
    logt("decoder %s: init begin (%dx%d start=%.2f paused=%d audio=%d) %s", tag, w, h, start, start_paused, with_audio, path);
    glGenTextures(1, &v->tex);
    glBindTexture(GL_TEXTURE_2D, v->tex);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, w, h, 0, GL_RGBA, GL_UNSIGNED_BYTE, NULL);
    glGenFramebuffers(1, &v->fbo);
    glBindFramebuffer(GL_FRAMEBUFFER, v->fbo);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, v->tex, 0);
    GLenum fb = glCheckFramebufferStatus(GL_FRAMEBUFFER);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    if (fb != GL_FRAMEBUFFER_COMPLETE) { fprintf(stderr, "lp-transition: FBO incomplete 0x%x\n", fb); return false; }

    v->mpv = mpv_create();
    if (!v->mpv) return false;
    // CRITICAL: with the render API, mpv must use the embedded "libmpv" VO and render through OUR GL
    // context. Without this it spins up a real windowed VO (gpu-next → libplacebo Vulkan on NVIDIA)
    // which crashes during Wayland init. Pin it before initialize.
    mpv_set_option_string(v->mpv, "vo", "libmpv");
    mpv_set_option_string(v->mpv, "terminal", "no");
    mpv_set_option_string(v->mpv, "msg-level", g_log ? "all=v" : "all=no"); // capture decode/hwdec logs when logging
    mpv_set_option_string(v->mpv, "config", "no");
    mpv_set_option_string(v->mpv, "ytdl", "no");
    if (with_audio) mpv_set_option_string(v->mpv, "volume", "0"); // enabled but silent → crossfaded up at go
    else            mpv_set_option_string(v->mpv, "audio", "no"); // desktop owns audio; this side is muted
    // loudness normalization for the overlay's own audio (same loudnorm mpvpaper-B gets at teardown), so
    // the crossfade DURING the effect is leveled too — not just after. Volume (above) rides on top.
    if (with_audio && af && *af) mpv_set_option_string(v->mpv, "af", af);
    mpv_set_option_string(v->mpv, "hwdec", (g_hwdec && *g_hwdec) ? g_hwdec : "auto-safe"); // mirror HwDec
    { char sp[16]; snprintf(sp, sizeof sp, "%.4f", speed > 0.01 ? speed : 1.0);
      mpv_set_option_string(v->mpv, "speed", sp); }                                        // mirror per-item/global Speed
    if (g_fps > 0) { char vf[24]; snprintf(vf, sizeof vf, "fps=%d", g_fps);
      mpv_set_option_string(v->mpv, "vf", vf); }                                           // mirror VideoFps cap
    // Loop mirrors this side's DESKTOP semantic (--from-loop/--to-loop = mpvpaper's loop-file):
    // Loop-on / timed / wait-for-video-end contexts wrap through the effect like the wallpaper
    // would; Loop-off holds the LAST frame at EOF (keep-open=yes) — freeze, never rewind/black.
    // (The align-a-end gate may additionally pin A to no-loop at the gate for the aligned-advance
    // freeze failsafe — done there, not here, because during warmup mpvpaper-A is still the visible
    // wallpaper and may loop; a pre-pinned overlay-A would freeze under it and jump at cover.)
    bool side_loop = (tag && tag[0] == 'A') ? from_loop : to_loop;
    mpv_set_option_string(v->mpv, "loop-file", side_loop ? "inf" : "no");
    mpv_set_option_string(v->mpv, "keep-open", "yes");
    mpv_set_option_string(v->mpv, "hr-seek", "yes");
    mpv_set_option_string(v->mpv, "panscan", scale_fill ? "1.0" : "0.0"); // match Video scale: fill=crop, fit=letterbox
    mpv_set_option_string(v->mpv, "pause", start_paused ? "yes" : "no");
    char sbuf[64]; snprintf(sbuf, sizeof sbuf, "%.3f", start);
    mpv_set_option_string(v->mpv, "start", sbuf);
    if (g_log) mpv_request_log_messages(v->mpv, "v"); // route mpv's own logs to our event pump
    if (mpv_initialize(v->mpv) < 0) { logt("decoder %s: mpv_initialize FAILED", tag); mpv_destroy(v->mpv); v->mpv = NULL; return false; }
    logt("decoder %s: mpv_initialize done at +%.0fms", tag, (now_s() - proc_t0) * 1000.0);

    mpv_opengl_init_params gl_init = { .get_proc_address = get_proc };
    mpv_render_param params[] = {
        { MPV_RENDER_PARAM_API_TYPE, (void *)MPV_RENDER_API_TYPE_OPENGL },
        { MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, &gl_init },
        { 0, NULL },
    };
    if (mpv_render_context_create(&v->rc, v->mpv, params) < 0) {
        fprintf(stderr, "lp-transition: mpv render context create failed\n");
        logt("decoder %s: render_context_create FAILED", tag);
        mpv_destroy(v->mpv); v->mpv = NULL; return false;
    }
    const char *cmd[] = { "loadfile", path, NULL };
    mpv_command(v->mpv, cmd);
    logt("decoder %s: render context + loadfile sent at +%.0fms", tag, (now_s() - proc_t0) * 1000.0);
    return true;
}

// Pull the latest decoded frame into the FBO texture (when one is ready). Leaves mpv's GL state
// dirty — the caller restores framebuffer/viewport/program before compositing the effect.
static void vidsrc_render(struct vid_src *v) {
    if (!v->mpv || !v->rc) return;
    // drain events → decoding proceeds; log the significant ones + mpv's own messages (decode/hwdec)
    for (mpv_event *e; (e = mpv_wait_event(v->mpv, 0))->event_id != MPV_EVENT_NONE; ) {
        if (!g_log) continue;
        if (e->event_id == MPV_EVENT_LOG_MESSAGE) {
            mpv_event_log_message *m = e->data;
            if (m && m->level && strcmp(m->level, "v") != 0) { // skip the noisiest 'v' spam, keep info/warn/error/fatal
                char t[256]; snprintf(t, sizeof t, "%s", m->text ? m->text : "");
                size_t n = strlen(t); if (n && t[n-1] == '\n') t[n-1] = 0;
                logt("    mpv[%s/%s] %s", v->tag ? v->tag : "?", m->prefix ? m->prefix : "?", t);
            }
        } else if (e->event_id == MPV_EVENT_FILE_LOADED) {
            logt("decoder %s: FILE_LOADED at +%.0fms", v->tag ? v->tag : "?", (now_s() - proc_t0) * 1000.0);
        } else if (e->event_id == MPV_EVENT_END_FILE) {
            logt("decoder %s: END_FILE", v->tag ? v->tag : "?");
        }
    }
    if (mpv_render_context_update(v->rc) & MPV_RENDER_UPDATE_FRAME) {
        mpv_opengl_fbo fbo = { .fbo = (int)v->fbo, .w = v->w, .h = v->h, .internal_format = 0 };
        int flip = 0;     // match the ffmpeg-rawvideo still orientation (top row first)
        int block = 0;    // don't stall our display-refresh effect loop waiting on the 30fps frame clock
        mpv_render_param p[] = {
            { MPV_RENDER_PARAM_OPENGL_FBO, &fbo },
            { MPV_RENDER_PARAM_FLIP_Y, &flip },
            { MPV_RENDER_PARAM_BLOCK_FOR_TARGET_TIME, &block },
            { 0, NULL },
        };
        mpv_render_context_render(v->rc, p);
        v->ready = true;
        v->uploads++;
        if (v->first_frame == 0.0) { v->first_frame = now_s(); logt("decoder %s: FIRST frame at +%.0fms", v->tag ? v->tag : "?", (v->first_frame - proc_t0) * 1000.0); }
    }
}

static void vidsrc_free(struct vid_src *v) {
    if (v->rc) { mpv_render_context_free(v->rc); v->rc = NULL; }   // GL context must be current
    if (v->mpv) { mpv_destroy(v->mpv); v->mpv = NULL; }
    if (v->fbo) { glDeleteFramebuffers(1, &v->fbo); v->fbo = 0; }
    if (v->tex) { glDeleteTextures(1, &v->tex); v->tex = 0; }
}

static void log_mpv_stats(struct vid_src *v) {
    if (!g_log || !v->mpv) return;
    double tp = -1, vffps = -1; int64_t fd = -1, dfd = -1, dw = -1, dh = -1;
    mpv_get_property(v->mpv, "time-pos", MPV_FORMAT_DOUBLE, &tp);
    mpv_get_property(v->mpv, "estimated-vf-fps", MPV_FORMAT_DOUBLE, &vffps);
    mpv_get_property(v->mpv, "frame-drop-count", MPV_FORMAT_INT64, &fd);
    mpv_get_property(v->mpv, "decoder-frame-drop-count", MPV_FORMAT_INT64, &dfd);
    mpv_get_property(v->mpv, "dwidth", MPV_FORMAT_INT64, &dw);
    mpv_get_property(v->mpv, "dheight", MPV_FORMAT_INT64, &dh);
    char *hw = mpv_get_property_string(v->mpv, "hwdec-current");
    char *codec = mpv_get_property_string(v->mpv, "video-codec");
    long up = v->uploads - v->last_uploads; v->last_uploads = v->uploads; // texture updates this interval
    logt("    %s: pos=%.3f uploads/iv=%ld vf-fps=%.1f drops=%lld/%lld decoded=%lldx%lld hwdec=%s codec=%s",
         v->tag ? v->tag : "?", tp, up, vffps, (long long)fd, (long long)dfd,
         (long long)dw, (long long)dh, hw && *hw ? hw : "SOFTWARE", codec ? codec : "?");
    if (hw) mpv_free(hw);
    if (codec) mpv_free(codec);
}

// ---- per-surface GL init + render -----------------------------------------
static bool surface_init_gl(struct surface *s, const char *vsrc, const char *fsrc) {
    s->egl_window = wl_egl_window_create(s->wl_surface, s->w, s->h);
    s->egl_surface = eglCreateWindowSurface(egl_dpy, egl_cfg,
                        (EGLNativeWindowType)s->egl_window, NULL);
    if (s->egl_surface == EGL_NO_SURFACE) { fprintf(stderr, "lp-transition: eglCreateWindowSurface failed\n"); return false; }
    eglMakeCurrent(egl_dpy, s->egl_surface, s->egl_surface, egl_ctx);

    GLuint vs = compile(GL_VERTEX_SHADER, vsrc), fs = compile(GL_FRAGMENT_SHADER, fsrc);
    if (!vs || !fs) return false;
    s->prog = glCreateProgram();
    glAttachShader(s->prog, vs); glAttachShader(s->prog, fs); glLinkProgram(s->prog);
    GLint ok = 0; glGetProgramiv(s->prog, GL_LINK_STATUS, &ok);
    if (!ok) { char log[2048]; glGetProgramInfoLog(s->prog, sizeof log, NULL, log);
        fprintf(stderr, "lp-transition: link failed:\n%s\n", log); return false; }
    glGenVertexArrays(1, &s->vao);
    s->u_progress = glGetUniformLocation(s->prog, "progress");
    s->u_ratio = glGetUniformLocation(s->prog, "ratio");
    if (reveal_a) {
        // S→V live composite: NO scene-A still (A is the live wallpaper underneath). fromTex = transparent
        // so the effect's "from" regions are see-through → the live scene-A shows; toTex = B (still
        // fallback; the live B decoder overrides it once warm).
        if (!transparent_tex) transparent_tex = mk_transparent_tex();
        s->tex_from = transparent_tex;
        s->tex_to = upload_tex(s->cfg->to, s->cfg->w, s->cfg->h);
        if (!s->tex_to) return false;
    } else {
        s->tex_from = upload_tex(s->cfg->from, s->cfg->w, s->cfg->h);
        if (!s->tex_from) return false;
        if (method == M_REVEAL || reveal_b) {
            if (!transparent_tex) transparent_tex = mk_transparent_tex(); // toTex → reveals live B underneath
        } else {
            s->tex_to = upload_tex(s->cfg->to, s->cfg->w, s->cfg->h);     // FROZEN / FULL_LIVE need B's still
            if (!s->tex_to) return false;
        }
    }
    glUseProgram(s->prog);
    glUniform1i(glGetUniformLocation(s->prog, "fromTex"), 0);
    glUniform1i(glGetUniformLocation(s->prog, "toTex"), 1);
    apply_uniforms(s->prog);
    if (method == M_FULL_LIVE && !g_decoders_inited) {
        // ONE shared decoder pair, created on the FIRST output (this GL context is shared, so every
        // output samples these textures). A resumes from mpvpaper-A's CURRENT position (epoch-
        // compensated, wrapped by its duration for loops); B held until first paint; audio on this pair.
        g_decoders_inited = true;
        double a_start = from_start;
        // mpvpaper-A advanced at ITS speed since the epoch sample → match that (not 1×) for the live position
        if (from_video && from_epoch > 0.0) { double d = now_realtime() - from_epoch; if (d > 0) a_start += from_speed * d; }
        if (from_duration > 0.01) a_start = fmod(a_start, from_duration); // wrap for looping videos
        cur_speed = from_speed;   // A's resting playback rate (catch-up multiplies around this)
        // reveal_a decodes ONLY B (A is the live scene underneath, not in the overlay) → B carries audio
        // on its own; otherwise audio needs both A+B decoders for the in-overlay crossfade.
        bool wants_audio = audio_volume > 0.0 && to_video && (reveal_a || from_video);
        if (from_video) vidsrc_init(&g_vfrom, from_video, a_start, s->w, s->h, false,     wants_audio, from_af, from_speed, "A"); // A tracks live A
        if (to_video)   vidsrc_init(&g_vto,   to_video,   to_start, s->w, s->h, to_paused, wants_audio, to_af, to_speed, "B"); // B held until first paint
    }
    return true;
}

static const struct wl_callback_listener frame_listener;

// ---- warm-up gate ----------------------------------------------------------
// A live side is "pending" only if its decoder is alive and hasn't produced its first frame yet (a
// decoder that FAILED to init falls back to the still and is NOT waited on; a scene side has no
// video and isn't either). We start the effect only when every surface is warm → the opaque effect
// never composites a frozen/stale still, and B switches only once everything is covered.
// A live side is warm when it's FLOWING, not just at its first frame: a cold 4K decoder delivers
// frame 0 then stalls ~230ms filling its pipeline. The PLAYING side (A) must clear that (a few frames
// delivered) before GO, or it freezes on frame 0 right as the effect starts. The PAUSED side (B) can
// only ever produce its single frame-0 while paused, so it's warm at the first frame.
#define WARM_FRAMES 4
static bool side_pending(struct vid_src *v, const char *video) {
    if (!video || !v->mpv) return false;
    return v->paused ? !v->ready : v->uploads < WARM_FRAMES;
}
// full-live uses ONE shared decoder pair → ready once both global decoders have produced a frame
static bool decoders_ready(void) {
    return !side_pending(&g_vfrom, from_video) && !side_pending(&g_vto, to_video);
}
static void schedule_frame(struct surface *s) {
    struct wl_callback *cb = wl_surface_frame(s->wl_surface);
    wl_callback_add_listener(cb, &frame_listener, s);
}

// FROZEN / REVEAL: no decoders, no warm-up. FROZEN = opaque A→B stills (B handed off B0→B0 by the
// teardown unpause). REVEAL = frozen A composited over the LIVE wallpaper-B playing on the layer
// underneath — toTex is transparent so the effect's "to" regions are see-through; the gl-transitions
// output is already premultiplied (mix(A, 0, p) = A·(1-p), alpha 1-p), so the compositor blends it
// correctly over B. B is normal mpvpaper playback — never paused, seeked, or decoded here.
static void render_simple(struct surface *s) {
    if (start_time < 0.0) start_time = now_s();
    double p = smoothstep01((now_s() - start_time - reveal_hold) / duration_s); // hold opaque, then animate
    bool reveal = (method == M_REVEAL);
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glDisable(GL_SCISSOR_TEST);
    glDisable(GL_BLEND);
    glViewport(0, 0, s->w, s->h);
    if (reveal) { glClearColor(0, 0, 0, 0); glClear(GL_COLOR_BUFFER_BIT); }
    else        { glClearColor(0, 0, 0, 1); glClear(GL_COLOR_BUFFER_BIT);
                  glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_FALSE); } // keep surface opaque (still alpha may be <1)
    glUseProgram(s->prog);
    glUniform1f(s->u_progress, (float)p);
    if (s->u_ratio >= 0) glUniform1f(s->u_ratio, (float)s->w / (float)s->h);
    glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, s->tex_from);
    glActiveTexture(GL_TEXTURE1); glBindTexture(GL_TEXTURE_2D, reveal ? transparent_tex : s->tex_to);
    glBindVertexArray(s->vao);
    glDrawArrays(GL_TRIANGLES, 0, 3);
    if (!reveal) glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
    if (p >= 1.0) s->done = true; else schedule_frame(s);
    eglSwapBuffers(egl_dpy, s->egl_surface);
    // first paint on screen → let the caller switch B underneath (frozen A covers / A-over-A reveal,
    // so the switch is hidden). Frozen stills cover instantly; this just removes the spawn-race flash.
    if (ready_file && frame_count == 1) {
        FILE *rf = fopen(ready_file, "w"); if (rf) fclose(rf);
        logt("%s: cover (first frame on screen) at +%.0fms — caller switches B now", reveal ? "REVEAL" : "FROZEN", (now_s() - proc_t0) * 1000.0);
    }
}

// Effect progress time, frozen while paused (pause_accum collects past pause windows) → pausing holds
// the visual + audio at the current point and resumes cleanly instead of jumping ahead.
static double fx_elapsed(void) { return (fx_paused ? pause_started : now_s()) - go_time - pause_accum; }

// Drain control datagrams (live mute/volume/pause from a keybind during the effect) → apply to the overlay.
static void poll_control(void) {
    if (ctl_fd < 0) return;
    char buf[64]; ssize_t n;
    while ((n = recv(ctl_fd, buf, sizeof buf - 1, MSG_DONTWAIT)) > 0) {
        buf[n] = 0;
        if (!strncmp(buf, "mute ", 5)) {
            const char *v = (buf[5] == '1') ? "yes" : "no";
            if (g_vfrom.mpv) mpv_set_property_string(g_vfrom.mpv, "mute", v);
            if (g_vto.mpv)   mpv_set_property_string(g_vto.mpv,   "mute", v);
            logt("control: mute=%s", v);
        } else if (!strncmp(buf, "vol ", 4)) {
            audio_volume = atof(buf + 4); // crossfade picks it up next frame
            logt("control: volume=%.0f", audio_volume);
        } else if (!strncmp(buf, "pause", 5) && go_time >= 0.0) {
            // TOGGLE renderer-side (stateless on the backend → no desync). Freeze/resume the clock + decoders.
            if (!fx_paused) {
                fx_paused = true; pause_started = now_s();
                if (g_vfrom.mpv) mpv_set_property_string(g_vfrom.mpv, "pause", "yes");
                if (g_vto.mpv)   mpv_set_property_string(g_vto.mpv,   "pause", "yes");
                logt("control: pause (el=%.2f)", fx_elapsed());
            } else {
                pause_accum += now_s() - pause_started; fx_paused = false;
                if (g_vfrom.mpv) mpv_set_property_string(g_vfrom.mpv, "pause", "no");
                if (g_vto.mpv)   mpv_set_property_string(g_vto.mpv,   "pause", "no");
                logt("control: resume (el=%.2f)", fx_elapsed());
            }
        }
    }
}

static void render(struct surface *s) {
    eglMakeCurrent(egl_dpy, s->egl_surface, s->egl_surface, egl_ctx);
    frame_count++;
    if (method != M_FULL_LIVE) { render_simple(s); return; } // frozen / reveal: no decoders below
    poll_control(); // live mute/volume during the effect
    // advance the SHARED decoders into their FBO textures (UPDATE_FRAME dedups across outputs)
    vidsrc_render(&g_vfrom);
    vidsrc_render(&g_vto);

    // mpv dirties GL state (FBO/viewport/program/scissor) — restore ours
    glBindFramebuffer(GL_FRAMEBUFFER, 0);
    glDisable(GL_SCISSOR_TEST);
    glDisable(GL_BLEND);

    // ── WARM-UP: don't start the effect until EVERY surface has a live frame ──────────────────
    // While warming, paint fully transparent so the live wallpaper underneath (mpvpaper still on A)
    // shows through SMOOTHLY — no frozen/stale still on screen. Once all surfaces are ready: set the
    // shared clock, release B everywhere in lockstep, and touch ready-file so the caller switches
    // mpvpaper's B under the now-covering overlay (no flash; teardown positions match).
    if (go_time < 0.0) {
        if (decoders_ready()) {
            // overlay-A lost the cold-decode stall time → it lags mpvpaper-A; cover here = rewind. CLOSED-
            // LOOP position track (no seek → no re-stall): each warm frame, measure A vs mpvpaper-A's live
            // pos and nudge speed — behind → 2x, ahead → 0.5x, in-band → 1x. mpv's async speed change +
            // decode-ahead drain overshoots unpredictably, so we don't guess: we keep correcting and only
            // cover once A HOLDS within ±LAG_BAND at 1x for BAND_HOLD. All hidden (transparent + muted).
            if (from_video && from_epoch > 0.0 && g_vfrom.mpv) {
                double ap = -1; mpv_get_property(g_vfrom.mpv, "time-pos", MPV_FORMAT_DOUBLE, &ap);
                double expect = from_start + from_speed * (now_realtime() - from_epoch); // A advances at its speed
                if (from_duration > 0.01) { ap = fmod(ap, from_duration); expect = fmod(expect, from_duration); }
                double lag = expect - ap;
                if (from_duration > 0.01) { // shortest direction around the loop
                    while (lag >  from_duration / 2) lag -= from_duration;
                    while (lag < -from_duration / 2) lag += from_duration;
                }
                bool give_up = (now_s() - proc_t0) > 4.0; // don't track forever
                double e = lag - lag_target; // error from the aim point (slightly ahead of live)
                // rates are RELATIVE to A's resting speed (expect advances at from_speed) → catch up / slow / rest
                double want = (e > LAG_BAND) ? from_speed * 2.0 : (e < -LAG_BAND) ? from_speed * 0.5 : from_speed;
                if (!give_up) {
                    if (want != cur_speed) {
                        char sp[8]; snprintf(sp, sizeof sp, "%.3f", want);
                        mpv_set_property_string(g_vfrom.mpv, "speed", sp);
                        logt("track: lag=%+.3f → %.3fx", lag, want);
                        cur_speed = want;
                        if (want != from_speed) in_band_t = -1.0; // left the deadband
                    }
                    bool covered_ok = false;
                    if (want == from_speed) {
                        if (in_band_t < 0.0) in_band_t = now_s();
                        if (now_s() - in_band_t >= BAND_HOLD) covered_ok = true; // held in-band → cover
                    }
                    if (!covered_ok) {
                        glViewport(0, 0, s->w, s->h);
                        glClearColor(0, 0, 0, 0); glClear(GL_COLOR_BUFFER_BIT);
                        schedule_frame(s); eglSwapBuffers(egl_dpy, s->egl_surface);
                        return;
                    }
                }
                if (cur_speed != from_speed) { char sp[8]; snprintf(sp, sizeof sp, "%.3f", from_speed); mpv_set_property_string(g_vfrom.mpv, "speed", sp); cur_speed = from_speed; }
                logt("track done: lag=%+.3f", lag);
            }
            go_time = start_time = now_s();
            // TRUTH CHECK: compare overlay-A's pos to BOTH the computed expect (what the loop optimizes)
            // AND mpvpaper-A's ACTUAL queried pos (the real reference — mpvpaper is still on A here, the
            // B switch happens ~40ms later). real-seam = overlay − actual mpvpaper-A = what the eye sees;
            // epoch-err = expect − actual = how wrong the extrapolation (and thus the loop target) is.
            { double ap = -1, ex = from_start + from_speed * (now_realtime() - from_epoch);
              if (from_duration > 0.01) ex = fmod(ex, from_duration);
              double t_q0 = now_s();
              double real = (mpv_unpause && *mpv_unpause) ? mpv_query_double(mpv_unpause, "time-pos") : -1.0;
              double q_ms = (now_s() - t_q0) * 1000.0;
              if (g_vfrom.mpv) mpv_get_property(g_vfrom.mpv, "time-pos", MPV_FORMAT_DOUBLE, &ap);
              double rseam = (real > 0) ? ap - real : 0.0, eerr = (real > 0) ? ex - real : 0.0;
              // A big real-seam while the in-process residual is tiny = mpvpaper reported a transient
              // time-pos at this instant (mid loadfile-prep), NOT a real seam — the loop tracked fine.
              const char *flag = (fabs(rseam) > 0.5 && fabs(ap - ex) < 0.1) ? " [suspect query — sync OK per residual]" : "";
              logt("GO resync-check: overlay-A=%.3f computed-expect=%.3f (residual %.3fs) || mpvpaper-A ACTUAL=%.3f → REAL-SEAM=%+.3fs epoch-err=%+.3fs (query %.1fms)%s",
                   ap, ex, ap - ex, real, rseam, eerr, q_ms, flag); }
            logt("GO (all decoders warm, +%.0fms): A first-frame=+%.0fms(%ld) B first-frame=+%.0fms(%ld) | warming render frames=%ld",
                 (go_time - proc_t0) * 1000.0,
                 g_vfrom.first_frame > 0 ? (g_vfrom.first_frame - proc_t0) * 1000.0 : -1.0, g_vfrom.uploads,
                 g_vto.first_frame   > 0 ? (g_vto.first_frame   - proc_t0) * 1000.0 : -1.0, g_vto.uploads, frame_count);
            if (g_vfrom.paused) { mpv_set_property_string(g_vfrom.mpv, "pause", "no"); g_vfrom.paused = false; }
            // align-a-end: hold B paused through the gate (it must start playing when the EFFECT starts,
            // so its teardown position stays = duration_s); released at gate-open below.
            if (g_vto.paused && !align_a_end) { mpv_set_property_string(g_vto.mpv, "pause", "no"); g_vto.paused = false; }
            if (align_a_end) gate_t0 = now_s();
            // ready-file is touched a few frames LATER (below), not here — see the note at teardown of render
        } else {
            glViewport(0, 0, s->w, s->h);
            glClearColor(0, 0, 0, 0); glClear(GL_COLOR_BUFFER_BIT); // transparent → wallpaper shows
            schedule_frame(s);
            eglSwapBuffers(egl_dpy, s->egl_surface);
            return;
        }
    }

    // ── A-END ALIGN GATE (sub-frame) ────────────────────────────────────────────────────────────
    // Hold at p=0 (opaque LIVE A — visually just the wallpaper) and start the effect at the EXACT
    // wall-clock instant `A_EOF − need`, so p hits 1 at A's EOF within ~1 display frame. time-pos is
    // quantized to A's frame rate, so a threshold check alone is ±1 SOURCE frame (17–42ms); instead,
    // anchor on a time-pos TICK: at the instant the property advances, A's true position IS that
    // value → EOF_wall = now + rem. Set go_time to (EOF_wall − need) — a continuous-clock target that
    // may land BETWEEN display frames; p's clamp holds 0 until then, then advances → p=1 at EOF.
    // Re-anchored every tick until the effect starts (also self-heals a pause/resume mid-gate).
    if (align_a_end && !a_gate_open) {
        double posn = -1, durn = -1;
        if (g_vfrom.mpv) {
            mpv_get_property(g_vfrom.mpv, "time-pos", MPV_FORMAT_DOUBLE, &posn);
            mpv_get_property(g_vfrom.mpv, "duration", MPV_FORMAT_DOUBLE, &durn);
        }
        double spd = from_speed > 0.01 ? from_speed : 1.0;
        double need = duration_s + reveal_hold;
        double tnow = now_s();
        if (posn < 0 || durn <= 0.01) {
            // props unreadable (decoder stalled?) → fail-safe: run the full effect now.
            a_gate_open = true;
            a_target_go = go_time = start_time = tnow;
            logt("A-END gate: props unreadable → failsafe start");
        } else {
            double L = durn / spd;             // A's full length, wall seconds
            double rem = (durn - posn) / spd;  // remaining in the CURRENT pass, wall seconds
            bool short_clip = L < need;        // can't contain the effect → alignment impossible
            if (!a_loop_pinned) {
                // ALIGNED advance, clip ≥ effect: pin no-loop so A ends FROZEN on its last frame at
                // EOF (keep-open=yes) — never rewinds. A SHORT clip can't be aligned: if its desktop
                // context loops (--from-loop: Loop on / timed / wait-for-video-end) it keeps LOOPING
                // through the effect like the wallpaper would; Loop-off → plays its remainder and
                // freezes, effect runs out over the frozen tail.
                if (!short_clip || !from_loop)
                    mpv_set_property_string(g_vfrom.mpv, "loop-file", "no");
                a_loop_pinned = true;
            }
            if (posn != a_tick_pos) {
                // TICK — anchor. (The very first read is a pseudo-tick with ≤1-frame phase error;
                // every subsequent real tick, one per source frame, refines the target.)
                a_tick_pos = posn;
                double target = tnow + rem - need;   // p=1 at this pass's EOF
                if (target < tnow - 0.004) { // >1 240Hz-frame past = unalignable (an exact hit falls through)
                    // Clip shorter than the effect, or fired LATE → start now, full effect;
                    // short+loop-ctx keeps looping, otherwise A freezes at EOF. Never a rewind.
                    a_gate_open = true;
                    a_target_go = go_time = start_time = tnow;
                    logt("A-END gate: %s (rem=%.3f need=%.3f held=%.0fms) → immediate start, %s",
                         short_clip ? "clip shorter than effect" : "late fire",
                         rem, need, gate_t0 > 0 ? (tnow - gate_t0) * 1000.0 : 0.0,
                         (short_clip && from_loop) ? "loops through (desktop loop ctx)" : "freeze at EOF");
                } else if (target - tnow > A_HOLD_MAX) {
                    // EOF farther than the hold budget (e.g. A wrapped during warmup after a very
                    // late fire) → don't hold a whole pass hostage; start now, unaligned.
                    a_gate_open = true;
                    a_target_go = go_time = start_time = tnow;
                    logt("A-END gate: EOF %.1fs away > %.0fs budget → immediate start (alignment missed)",
                         target - tnow, A_HOLD_MAX);
                } else {
                    a_target_go = go_time = start_time = target;
                    if (!a_target_logged) {
                        a_target_logged = true;
                        logt("A-END target committed: EOF in %.3fs → effect starts in %.3fs",
                             target - tnow + need, target - tnow);
                    }
                }
            } else if (a_target_go < 0.0) {
                go_time = start_time = tnow; // no tick yet → keep the clock pinned at 0
            }
            if (!a_gate_open && a_target_go >= 0.0 && tnow >= a_target_go) {
                a_gate_open = true;           // target instant reached (el now ≥ 0, continuous)
                logt("A-END gate OPEN (sub-frame): rem=%.3f need=%.3f held=%.0fms",
                     rem, need, gate_t0 > 0 ? (tnow - gate_t0) * 1000.0 : 0.0);
            }
        }
        if (a_gate_open && g_vto.mpv && g_vto.paused) { mpv_set_property_string(g_vto.mpv, "pause", "no"); g_vto.paused = false; }
    }

    // ── TRANSITION: composite the effect over live A → live B ─────────────────────────────────
    double el = fx_elapsed();                          // frozen while paused
    // reveal-b: hold the opaque live-A cover for reveal_hold (LWE scene-B renders hidden), then reveal
    double p = smoothstep01((el - reveal_hold) / duration_s);
    // A-END accuracy metric at the first p=1 frame. wall-err = this frame's wall time vs the anchored
    // EOF instant (the true sub-frame error; expect ≤ ~1 display frame). pos-rem = A's remaining per
    // time-pos — SOURCE-frame quantized, so it reads 0.000 or ±1 frame even when wall-err is ~1ms.
    if (align_a_end && !a_end_logged && p >= 1.0 && g_vfrom.mpv) {
        double posn = -1, durn = -1;
        mpv_get_property(g_vfrom.mpv, "time-pos", MPV_FORMAT_DOUBLE, &posn);
        mpv_get_property(g_vfrom.mpv, "duration", MPV_FORMAT_DOUBLE, &durn);
        double spd = from_speed > 0.01 ? from_speed : 1.0;
        double eof_wall = a_target_go + duration_s + reveal_hold;
        logt("A-END: wall-err=%+.4fs (p=1 frame vs A's EOF instant) | pos-rem=%+.3fs",
             a_target_go > 0 ? now_s() - eof_wall : -99.0,
             (posn >= 0 && durn > 0.01) ? (durn - posn) / spd : -99.0);
        a_end_logged = true;
    }
    // Partway through, pre-seek mpvpaper's paused/hidden B to where it will hand off (= duration),
    // so the frame is fully decoded BEFORE the reveal — no decode-latency jump-to-0 at the end.
    if (!b_preseeked && mpv_unpause && *mpv_unpause && el > duration_s * 0.25) {
        // Handoff position in B MEDIA time = wall-elapsed × speed, WRAPPED for a B shorter than the
        // effect (a raw `seek duration_s` past a short B's EOF clamps to its end → up to a full clip
        // of seam error at teardown; it also under-seeked any speed≠1 item).
        double bspd = to_speed > 0.01 ? to_speed : 1.0;
        double bdur = -1;
        if (g_vto.mpv) mpv_get_property(g_vto.mpv, "duration", MPV_FORMAT_DOUBLE, &bdur);
        double tgt = duration_s * bspd;
        if (bdur > 0.01 && tgt >= bdur)
            // loop ctx → same wrapped frame the (looping) overlay-B shows; Loop-off → the overlay-B
            // froze at its last frame (keep-open), so hold mpvpaper-B just short of EOF to match
            tgt = to_loop ? fmod(tgt, bdur) : (bdur > 0.1 ? bdur - 0.05 : bdur);
        char c[160];
        snprintf(c, sizeof c, "{\"command\":[\"seek\",%.3f,\"absolute\",\"exact\"]}\n", tgt);
        mpv_cmd(mpv_unpause, c);
        b_preseeked = true;
        logt("pre-seek mpvpaper-B to %.2fs media (go+%.0fms, spd=%.2f bdur=%.2f)", tgt, el * 1000.0, bspd, bdur);
    }
    // diagnostic: every ~200ms dump the real per-decoder state (pos advance, upload rate, hwdec, drops)
    if (g_log) {
        if (el >= next_stat) {
            long disp = frame_count - last_disp_frames; last_disp_frames = frame_count;
            logt("  t+%.2fs progress=%.2f | display frames this 0.2s=%ld", el, p, disp);
            log_mpv_stats(&g_vfrom);
            log_mpv_stats(&g_vto);
            next_stat = el + 0.2;
        }
    }
    // audio crossfade A→B on the shared decoder pair. LINEAR-in-time (not the visual smoothstep p) so
    // it's an EVEN fade that lands at exactly 50/50 at the midpoint (A=B=0.5·vol). A short fade-IN
    // (~60ms) still ramps it up from silence at the cover to kill the volume-snap "tick".
    if (audio_volume > 0.0 && g_vfrom.mpv && g_vto.mpv) {
        char vb[24];
        double x = el / duration_s; if (x < 0.0) x = 0.0; if (x > 1.0) x = 1.0; // linear progress
        double fade = el / 0.060; if (fade > 1.0) fade = 1.0; // 60ms ramp, kills the click
        snprintf(vb, sizeof vb, "%.1f", fade * (1.0 - x) * audio_volume); mpv_set_property_string(g_vfrom.mpv, "volume", vb);
        snprintf(vb, sizeof vb, "%.1f", fade * x * audio_volume);         mpv_set_property_string(g_vto.mpv,   "volume", vb);
    } else if (reveal_a && audio_volume > 0.0 && g_vto.mpv) {
        // reveal_a: only B is in the overlay → fade B IN (scene-A's fade-OUT is the backend's lp-audio job).
        char vb[24];
        double x = el / duration_s; if (x < 0.0) x = 0.0; if (x > 1.0) x = 1.0;
        double fade = el / 0.060; if (fade > 1.0) fade = 1.0;
        snprintf(vb, sizeof vb, "%.1f", fade * x * audio_volume); mpv_set_property_string(g_vto.mpv, "volume", vb);
    }
    glViewport(0, 0, s->w, s->h);
    // reveal-b: TRANSPARENT compositing (like reveal) — clear alpha 0, keep the alpha channel so the
    // effect's premultiplied output reveals the live scene-B on the layer underneath. full-live: OPAQUE
    // (clear alpha 1 + mask alpha off) so the desktop never shows through the both-video composite.
    // reveal-b / reveal-a: TRANSPARENT compositing (clear alpha 0, keep the alpha channel) so the
    // effect's premultiplied output reveals the live wallpaper underneath (scene-B for reveal-b,
    // scene-A for reveal-a). full-live: OPAQUE (clear alpha 1 + mask alpha off).
    bool transparent_mode = reveal_b || reveal_a;
    glClearColor(0, 0, 0, transparent_mode ? 0.0f : 1.0f); glClear(GL_COLOR_BUFFER_BIT);
    if (!transparent_mode) glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_FALSE);
    glUseProgram(s->prog);
    glUniform1f(s->u_progress, (float)p);
    if (s->u_ratio >= 0) glUniform1f(s->u_ratio, (float)s->w / (float)s->h);
    // reveal-a: fromTex transparent → live scene-A shows underneath (g_vfrom never inited → s->tex_from
    // is the transparent tex). reveal-b: toTex transparent → scene-B shows. else: live frame / still.
    GLuint tf = g_vfrom.ready ? g_vfrom.tex : s->tex_from;
    GLuint tt = reveal_b ? transparent_tex
                         : (g_vto.ready ? g_vto.tex : s->tex_to);
    glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, tf);
    glActiveTexture(GL_TEXTURE1); glBindTexture(GL_TEXTURE_2D, tt);
    glBindVertexArray(s->vao);
    glDrawArrays(GL_TRIANGLES, 0, 3);
    if (!transparent_mode) glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);

    if (p >= 1.0) {
        // reveal-a: hold the final opaque-B frame for hold_end_s so the backend can launch mpvpaper-B
        // underneath (covered) before we tear down → no flash back to the live scene-A at handoff.
        if (hold_end_s > 0.0) {
            if (end_reached < 0.0) end_reached = now_s();
            if (now_s() - end_reached < hold_end_s) schedule_frame(s); else s->done = true;
        } else s->done = true;
    } else schedule_frame(s);
    eglSwapBuffers(egl_dpy, s->egl_surface);
    // tell mpv the frame was presented → it can pace decoding to real display timing (without this
    // its frame delivery judders / drops to a low effective rate, especially with several decoders)
    if (g_vfrom.rc) mpv_render_context_report_swap(g_vfrom.rc);
    if (g_vto.rc)   mpv_render_context_report_swap(g_vto.rc);

    // Signal the caller to switch mpvpaper's B only AFTER the first OPAQUE effect frame is actually on
    // screen (~40ms past go = a couple vblanks, surface-count-independent). Touching it AT go races the
    // compositor: B (frame 0) loads while the last TRANSPARENT warming frame is still displayed → B
    // flashes through for ~1 frame. The opaque overlay is already covering during this short delay.
    if (ready_file && !ready_signaled && go_time >= 0.0 && el >= 0.040) {
        FILE *rf = fopen(ready_file, "w"); if (rf) fclose(rf);
        ready_signaled = true;
        logt("ready-file signaled (go+%.0fms) → caller switches B now", el * 1000.0);
    }
}

static void frame_done(void *data, struct wl_callback *cb, uint32_t t) {
    (void)t; wl_callback_destroy(cb); render((struct surface *)data);
}
static const struct wl_callback_listener frame_listener = { .done = frame_done };

// ---- layer-surface configure ----------------------------------------------
static void ls_configure(void *data, struct zwlr_layer_surface_v1 *ls,
                         uint32_t serial, uint32_t w, uint32_t h) {
    struct surface *s = data;
    zwlr_layer_surface_v1_ack_configure(ls, serial);
    if (s->configured) return;
    s->configured = true;
    s->w = w ? (int)w : s->cfg->w;
    s->h = h ? (int)h : s->cfg->h;
    logt("surface '%s' configured %dx%d at +%.0fms", s->cfg->name, s->w, s->h, (now_s() - proc_t0) * 1000.0);
    // shaders are read once by caller path; stashed on first surface via globals
    extern char *g_vsrc, *g_fsrc;
    if (!surface_init_gl(s, g_vsrc, g_fsrc)) { fprintf(stderr, "lp-transition: GL init failed\n"); exit(1); }
    logt("surface '%s' GL ready (shader linked, stills uploaded%s)", s->cfg->name, method == M_FULL_LIVE ? ", decoders up" : "");
    render(s);
}
static void ls_closed(void *data, struct zwlr_layer_surface_v1 *ls) {
    (void)data; (void)ls;
}
static const struct zwlr_layer_surface_v1_listener ls_listener = {
    .configure = ls_configure, .closed = ls_closed,
};
char *g_vsrc = NULL, *g_fsrc = NULL;

// ---- output name discovery -------------------------------------------------
static void output_name(void *data, struct wl_output *o, const char *name) {
    (void)o; struct out_info *oi = data;
    snprintf(oi->name, sizeof oi->name, "%s", name); oi->named = true;
}
static void output_geometry(void *d, struct wl_output *o, int32_t x, int32_t y,
    int32_t pw, int32_t ph, int32_t sp, const char *m, const char *md, int32_t tr) {
    (void)d;(void)o;(void)x;(void)y;(void)pw;(void)ph;(void)sp;(void)m;(void)md;(void)tr;
}
static void output_mode(void *d, struct wl_output *o, uint32_t f, int32_t w, int32_t h, int32_t r)
{ (void)d;(void)o;(void)f;(void)w;(void)h;(void)r; }
static void output_done(void *d, struct wl_output *o) { (void)d;(void)o; }
static void output_scale(void *d, struct wl_output *o, int32_t s) { (void)d;(void)o;(void)s; }
static void output_description(void *d, struct wl_output *o, const char *desc)
{ (void)d;(void)o;(void)desc; }
static const struct wl_output_listener output_listener = {
    .geometry = output_geometry, .mode = output_mode, .done = output_done,
    .scale = output_scale, .name = output_name, .description = output_description,
};

// ---- registry --------------------------------------------------------------
static void reg_global(void *data, struct wl_registry *reg, uint32_t id,
                       const char *iface, uint32_t ver) {
    (void)data; (void)ver;
    if (!strcmp(iface, wl_compositor_interface.name))
        compositor = wl_registry_bind(reg, id, &wl_compositor_interface, 4);
    else if (!strcmp(iface, zwlr_layer_shell_v1_interface.name))
        layer_shell = wl_registry_bind(reg, id, &zwlr_layer_shell_v1_interface, 1);
    else if (!strcmp(iface, wl_output_interface.name) && n_wl_outs < MAX_OUTPUTS) {
        uint32_t v = ver < 4 ? ver : 4;  // need v4 for the name event
        struct out_info *oi = &wl_outs[n_wl_outs++];
        oi->output = wl_registry_bind(reg, id, &wl_output_interface, v);
        oi->named = false;
        wl_output_add_listener(oi->output, &output_listener, oi);
    }
}
static void reg_remove(void *d, struct wl_registry *r, uint32_t id) { (void)d;(void)r;(void)id; }
static const struct wl_registry_listener reg_listener = { .global = reg_global, .global_remove = reg_remove };

static struct wl_output *find_output(const char *name) {
    for (int i = 0; i < n_wl_outs; i++)
        if (wl_outs[i].named && !strcmp(wl_outs[i].name, name)) return wl_outs[i].output;
    return NULL;
}

// ---- arg parsing -----------------------------------------------------------
static void parse_args(int argc, char **argv) {
    for (int i = 1; i < argc; i++) {
        char *a = argv[i];
        if (!strcmp(a, "--duration-ms")) duration_s = atof(argv[++i]) / 1000.0;
        else if (!strcmp(a, "--vert")) vert_path = argv[++i];
        else if (!strcmp(a, "--frag")) frag_path = argv[++i];
        else if (!strcmp(a, "--on-finish")) on_finish = argv[++i];
        else if (!strcmp(a, "--method")) {
            const char *m = argv[++i];
            method = !strcmp(m, "reveal") ? M_REVEAL : !strcmp(m, "full-live") ? M_FULL_LIVE : M_FROZEN;
        }
        else if (!strcmp(a, "--mpv-unpause")) mpv_unpause = argv[++i];
        else if (!strcmp(a, "--from-video")) from_video = argv[++i];
        else if (!strcmp(a, "--to-video")) to_video = argv[++i];
        else if (!strcmp(a, "--from-start")) from_start = atof(argv[++i]);
        else if (!strcmp(a, "--to-start")) to_start = atof(argv[++i]);
        else if (!strcmp(a, "--from-epoch")) from_epoch = atof(argv[++i]);
        else if (!strcmp(a, "--from-duration")) from_duration = atof(argv[++i]);
        else if (!strcmp(a, "--scale")) scale_fill = strcmp(argv[++i], "fit") != 0;
        else if (!strcmp(a, "--log")) { g_log = fopen(argv[++i], "a"); }
        else if (!strcmp(a, "--audio-volume")) audio_volume = atof(argv[++i]);
        else if (!strcmp(a, "--lag-offset"))   lag_target  = atof(argv[++i]); // per-machine A-sync aim (default -0.03)
        else if (!strcmp(a, "--from-af"))      from_af = argv[++i]; // loudnorm for overlay A audio
        else if (!strcmp(a, "--to-af"))        to_af   = argv[++i]; // loudnorm for overlay B audio
        else if (!strcmp(a, "--control-sock")) ctl_sock = argv[++i]; // live mute/volume datagrams
        else if (!strcmp(a, "--align-a-end"))    align_a_end = true; // gate effect start on A's decoded remaining
        else if (!strcmp(a, "--from-loop"))      from_loop = atoi(argv[++i]); // A's desktop loop semantic
        else if (!strcmp(a, "--to-loop"))        to_loop   = atoi(argv[++i]); // B's desktop loop semantic
        else if (!strcmp(a, "--reveal-hold-ms")) reveal_hold = atof(argv[++i]) / 1000.0; // hold opaque before reveal
        else if (!strcmp(a, "--hold-end-ms"))    hold_end_s  = atof(argv[++i]) / 1000.0; // reveal-a: hold opaque-B at the end
        else if (!strcmp(a, "--mpv-seek-b"))      mpv_seek_b  = true; // reveal-a: seek mpvpaper-B to overlay-B at teardown
        else if (!strcmp(a, "--reveal-b")) reveal_b = true; // full-live A + transparent reveal of live scene-B
        else if (!strcmp(a, "--reveal-a")) reveal_a = true; // decode B live + transparent reveal of live scene-A (S→V)
        else if (!strcmp(a, "--from-speed"))   from_speed = atof(argv[++i]);
        else if (!strcmp(a, "--to-speed"))     to_speed   = atof(argv[++i]);
        else if (!strcmp(a, "--fps"))          g_fps = atoi(argv[++i]);
        else if (!strcmp(a, "--hwdec"))        g_hwdec = argv[++i];
        else if (!strcmp(a, "--to-paused")) to_paused = true;
        else if (!strcmp(a, "--ready-file")) ready_file = argv[++i];
        else if (!strcmp(a, "--uniform") && n_uniforms < MAX_UNIFORMS) {
            struct uniform_def *u = &uniforms[n_uniforms++];
            snprintf(u->name, sizeof u->name, "%s", argv[++i]);
            snprintf(u->type, sizeof u->type, "%s", argv[++i]);
            int cnt = !strcmp(u->type, "vec4") ? 4 : !strcmp(u->type, "vec3") ? 3 :
                      (!strcmp(u->type, "vec2") || !strcmp(u->type, "ivec2")) ? 2 : 1;
            for (int k = 0; k < cnt; k++) u->v[k] = atof(argv[++i]);
            u->n = cnt;
        }
        else if (!strcmp(a, "--output") && n_out_cfgs < MAX_OUTPUTS) {
            struct out_cfg *c = &out_cfgs[n_out_cfgs++];
            snprintf(c->name, sizeof c->name, "%s", argv[++i]);
        }
        else if (!strcmp(a, "--from") && n_out_cfgs) out_cfgs[n_out_cfgs-1].from = argv[++i];
        else if (!strcmp(a, "--to") && n_out_cfgs) out_cfgs[n_out_cfgs-1].to = argv[++i];
        else if (!strcmp(a, "--width") && n_out_cfgs) out_cfgs[n_out_cfgs-1].w = atoi(argv[++i]);
        else if (!strcmp(a, "--height") && n_out_cfgs) out_cfgs[n_out_cfgs-1].h = atoi(argv[++i]);
    }
}

int main(int argc, char **argv) {
    proc_t0 = now_s();
    parse_args(argc, argv);
    if (!vert_path || !frag_path || !n_out_cfgs) {
        fprintf(stderr, "lp-transition: need --vert --frag and at least one --output\n");
        return 2;
    }
    logt("start: method=%s dur=%.2fs scale=%s outputs=%d", method == M_REVEAL ? "reveal" :
         method == M_FULL_LIVE ? "full-live" : "frozen", duration_s, scale_fill ? "fill" : "fit", n_out_cfgs);
    g_vsrc = read_file(vert_path); g_fsrc = read_file(frag_path);
    if (!g_vsrc || !g_fsrc) return 1;

    // control socket (DGRAM): the backend sends "mute 1/0" / "vol N" so a keybind during the effect
    // reaches the overlay's own audio. Best-effort — failure just means mute/volume apply post-teardown.
    if (ctl_sock) {
        ctl_fd = socket(AF_UNIX, SOCK_DGRAM, 0);
        if (ctl_fd >= 0) {
            struct sockaddr_un ca = { .sun_family = AF_UNIX };
            snprintf(ca.sun_path, sizeof ca.sun_path, "%s", ctl_sock);
            unlink(ctl_sock);
            if (bind(ctl_fd, (struct sockaddr *)&ca, sizeof ca) < 0) { close(ctl_fd); ctl_fd = -1; }
        }
    }

    display = wl_display_connect(NULL);
    if (!display) { fprintf(stderr, "lp-transition: no Wayland display\n"); return 1; }
    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &reg_listener, NULL);
    wl_display_roundtrip(display);   // globals
    wl_display_roundtrip(display);   // output name/geometry events
    if (!compositor || !layer_shell) { fprintf(stderr, "lp-transition: missing compositor/layer-shell\n"); return 1; }
    logt("wayland ready at +%.0fms (compositor+layer-shell ok, %d outputs discovered)", (now_s() - proc_t0) * 1000.0, n_wl_outs);

    // EGL init (shared display/config/context)
    egl_dpy = eglGetDisplay((EGLNativeDisplayType)display);
    eglInitialize(egl_dpy, NULL, NULL);
    eglBindAPI(EGL_OPENGL_ES_API);
    EGLint cfg_attr[] = {
        EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
        EGL_RENDERABLE_TYPE, EGL_OPENGL_ES2_BIT,
        EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
        EGL_NONE };
    EGLint ncfg = 0;
    eglChooseConfig(egl_dpy, cfg_attr, &egl_cfg, 1, &ncfg);
    if (ncfg < 1) { fprintf(stderr, "lp-transition: no EGL config\n"); return 1; }
    EGLint ctx_attr[] = { EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE };
    egl_ctx = eglCreateContext(egl_dpy, egl_cfg, EGL_NO_CONTEXT, ctx_attr);
    if (egl_ctx == EGL_NO_CONTEXT) { fprintf(stderr, "lp-transition: no EGL ES3 context\n"); return 1; }
    logt("EGL ready at +%.0fms (GLES3 context)", (now_s() - proc_t0) * 1000.0);

    // build one layer surface per --output that maps to a live wl_output
    for (int i = 0; i < n_out_cfgs; i++) {
        struct wl_output *o = find_output(out_cfgs[i].name);
        if (!o) { fprintf(stderr, "lp-transition: output %s not found, skipping\n", out_cfgs[i].name); continue; }
        struct surface *s = &surfaces[n_surfaces++];
        s->cfg = &out_cfgs[i];
        s->wl_surface = wl_compositor_create_surface(compositor);
        // click-through: empty input region
        struct wl_region *empty = wl_compositor_create_region(compositor);
        wl_surface_set_input_region(s->wl_surface, empty);
        wl_region_destroy(empty);
        s->layer_surface = zwlr_layer_shell_v1_get_layer_surface(
            layer_shell, s->wl_surface, o, ZWLR_LAYER_SHELL_V1_LAYER_BOTTOM, "livepaper-transition");
        zwlr_layer_surface_v1_add_listener(s->layer_surface, &ls_listener, s);
        zwlr_layer_surface_v1_set_anchor(s->layer_surface,
            ZWLR_LAYER_SURFACE_V1_ANCHOR_TOP | ZWLR_LAYER_SURFACE_V1_ANCHOR_BOTTOM |
            ZWLR_LAYER_SURFACE_V1_ANCHOR_LEFT | ZWLR_LAYER_SURFACE_V1_ANCHOR_RIGHT);
        zwlr_layer_surface_v1_set_exclusive_zone(s->layer_surface, -1);
        zwlr_layer_surface_v1_set_keyboard_interactivity(s->layer_surface, 0);
        wl_surface_commit(s->wl_surface);
    }
    if (!n_surfaces) { fprintf(stderr, "lp-transition: no matching outputs\n"); return 1; }

    // event loop until every surface finishes (+ a hard safety timeout that covers decoder warm-up
    // and, when align-a-end gates the start on A's remaining, the backend's early-fire slack)
    double deadline = now_s() + duration_s + (align_a_end ? 15.0 : 8.0);
    while (wl_display_dispatch(display) != -1) {
        bool all_done = true;
        for (int i = 0; i < n_surfaces; i++) if (!surfaces[i].done) all_done = false;
        if (all_done || now_s() > deadline) break;
    }

    // B handoff: B was pre-seeked to the handoff position (decoded + ready). UNPAUSE mpvpaper-B FIRST
    // (its B audio resumes at the wallpaper volume = the overlay-B's current crossfade level → matched,
    // no gap), THEN mute the overlay decoders so they don't echo B during their ~1s async teardown.
    // Order matters: muting first would leave a silent gap before mpvpaper-B is audible → a click.
    logt("teardown: effect done — unpause mpvpaper-B, mute overlay audio, unmap overlay");
    // B-SEAM diagnostic: where the overlay's B decoder IS (what the effect just showed) vs where
    // mpvpaper-B is (what gets revealed). seam = the visible jump at handoff. V→V ≈ 0 (mpvpaper-B
    // played in sync under the opaque cover); a late/paused mpvpaper-B (reveal-a) jumps by ~the seam.
    if (mpv_seek_b && g_vto.mpv && mpv_unpause && *mpv_unpause) {
        // reveal-a: mpvpaper-B was launched late + paused near here → seek it to the overlay-B's EXACT
        // frame (hr-seek) so the reveal CONTINUES instead of jumping back. Anticipate the decode-settle
        // window (overlay-B keeps playing during it) so the two MATCH at unmap, not after.
        double ovB = -1; mpv_get_property(g_vto.mpv, "time-pos", MPV_FORMAT_DOUBLE, &ovB);
        double realB0 = mpv_query_double(mpv_unpause, "time-pos");
        const double settle_s = 0.13;   // decode time after the seek (must elapse before unmap)
        const double anticip  = 0.10;   // overlay-B advances ~this much during the settle → match at unmap
                                        // (slight forward bias: a tiny SKIP, never a repeat)
        if (ovB > 0) {
            char c[96]; snprintf(c, sizeof c, "{\"command\":[\"seek\",%.3f,\"absolute\",\"exact\"]}\n", ovB + anticip);
            mpv_cmd(mpv_unpause, c);
            struct timespec ts = { 0, (long)(settle_s * 1e9) }; nanosleep(&ts, NULL); // let the seeked frame decode
        }
        double ovB2 = -1; mpv_get_property(g_vto.mpv, "time-pos", MPV_FORMAT_DOUBLE, &ovB2);
        double realB1 = mpv_query_double(mpv_unpause, "time-pos");
        logt("B-SEAM(reveal-a): pre overlay=%.3f mpvB=%.3f → sought %.3f → at-unmap overlay=%.3f mpvB=%.3f SEAM=%+.3fs",
             ovB, realB0, ovB + anticip, ovB2, realB1, (realB1 >= 0 ? ovB2 - realB1 : 0.0));
    } else if (g_vto.mpv) {
        double ovB = -1; mpv_get_property(g_vto.mpv, "time-pos", MPV_FORMAT_DOUBLE, &ovB);
        double realB = (mpv_unpause && *mpv_unpause) ? mpv_query_double(mpv_unpause, "time-pos") : -1.0;
        // wrap-aware seam for a LOOPING short B: 0.967 vs 0.000 on a 1s clip is a one-frame seam
        // across the wrap (0.967→1.0≡0.0), not 0.967s — take the shortest distance around the loop.
        double seam = (realB >= 0 ? ovB - realB : 0.0);
        double bdur = -1; mpv_get_property(g_vto.mpv, "duration", MPV_FORMAT_DOUBLE, &bdur);
        if (to_loop && bdur > 0.01) { while (seam >  bdur / 2) seam -= bdur; while (seam < -bdur / 2) seam += bdur; }
        logt("B-SEAM: overlay-B=%.3f mpvpaper-B=%.3f → seam=%+.3fs%s", ovB, realB, seam,
             (bdur > 0.01 && fabs(ovB - realB) > bdur / 2) ? " (wrapped)" : "");
    }
    if (mpv_unpause && *mpv_unpause) mpv_cmd(mpv_unpause, "{\"command\":[\"set_property\",\"pause\",false]}\n");
    if (g_vfrom.mpv) mpv_set_property_string(g_vfrom.mpv, "mute", "yes");
    if (g_vto.mpv)   mpv_set_property_string(g_vto.mpv,   "mute", "yes");

    // 2) UNMAP the overlay surfaces NOW so the live wallpaper (mpvpaper's B, now at the matched
    //    position) is revealed instantly. The libmpv teardown below takes ~1s (freeing render contexts
    //    + instances) and would otherwise hold the last frozen frame on screen → a freeze at the end.
    for (int i = 0; i < n_surfaces; i++) {
        wl_surface_attach(surfaces[i].wl_surface, NULL, 0, 0);
        wl_surface_commit(surfaces[i].wl_surface);
    }
    wl_display_flush(display);
    wl_display_roundtrip(display);

    if (getenv("LP_TRANSITION_FPS")) {
        double el = now_s() - start_time;
        fprintf(stderr, "lp-transition: %ld frames / %d surface(s) / %.2fs = %.1f fps/surface\n",
                frame_count, n_surfaces, el, frame_count / (double)n_surfaces / (el > 0 ? el : 1));
    }
    // tear down the shared decoders (GL context must be current for the render-context cleanup)
    if (n_surfaces > 0) {
        eglMakeCurrent(egl_dpy, surfaces[0].egl_surface, surfaces[0].egl_surface, egl_ctx);
        vidsrc_free(&g_vfrom);
        vidsrc_free(&g_vto);
    }
    if (on_finish && *on_finish) { int r = system(on_finish); (void)r; }
    {
        double el = (go_time > 0.0) ? now_s() - go_time : 0.0;
        logt("DONE: transition=%.2fs | A uploads=%ld (%.1f/s) B uploads=%ld (%.1f/s) | display frames=%ld / %d surface(s)",
             el, g_vfrom.uploads, el > 0 ? g_vfrom.uploads / el : 0.0,
             g_vto.uploads, el > 0 ? g_vto.uploads / el : 0.0, frame_count, n_surfaces);
        logt("----");
    }
    if (ctl_fd >= 0) { close(ctl_fd); if (ctl_sock) unlink(ctl_sock); }
    if (g_log) fclose(g_log);
    return 0;
}
