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
#include <math.h>
#include <time.h>
#include <unistd.h>
#include <sys/socket.h>
#include <sys/un.h>
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
static bool audio_claimed = false;                 // ONE surface carries audio (avoid duplicate streams on multi-monitor)
static char *ready_file = NULL;                    // touched after the first frame is presented

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
};

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
    bool primed;                  // first render happened → paused decoders have been unpaused
    bool has_audio;               // this surface's decoders carry the (crossfaded) transition audio
    struct vid_src vfrom, vto;    // live decoders (zeroed = unused → still fallback)
};
static struct surface surfaces[MAX_OUTPUTS];
static int n_surfaces = 0;

static double start_time = -1.0;
static double go_time = -1.0;      // effect clock start — set once EVERY surface has a live frame
static bool b_preseeked = false;   // mpvpaper's paused B has been pre-seeked to the handoff position
static long frame_count = 0;       // for fps reporting (LP_TRANSITION_FPS=1)

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
static bool vidsrc_init(struct vid_src *v, const char *path, double start, int w, int h, bool start_paused, bool with_audio) {
    v->w = w; v->h = h; v->ready = false; v->paused = start_paused;
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
    mpv_set_option_string(v->mpv, "msg-level", "all=no");
    mpv_set_option_string(v->mpv, "config", "no");
    mpv_set_option_string(v->mpv, "ytdl", "no");
    if (with_audio) mpv_set_option_string(v->mpv, "volume", "0"); // enabled but silent → crossfaded up at go
    else            mpv_set_option_string(v->mpv, "audio", "no"); // desktop owns audio; this side is muted
    mpv_set_option_string(v->mpv, "hwdec", "auto-safe");
    mpv_set_option_string(v->mpv, "loop-file", "inf");
    mpv_set_option_string(v->mpv, "keep-open", "yes");
    mpv_set_option_string(v->mpv, "hr-seek", "yes");
    mpv_set_option_string(v->mpv, "pause", start_paused ? "yes" : "no");
    char sbuf[64]; snprintf(sbuf, sizeof sbuf, "%.3f", start);
    mpv_set_option_string(v->mpv, "start", sbuf);
    if (mpv_initialize(v->mpv) < 0) { mpv_destroy(v->mpv); v->mpv = NULL; return false; }

    mpv_opengl_init_params gl_init = { .get_proc_address = get_proc };
    mpv_render_param params[] = {
        { MPV_RENDER_PARAM_API_TYPE, (void *)MPV_RENDER_API_TYPE_OPENGL },
        { MPV_RENDER_PARAM_OPENGL_INIT_PARAMS, &gl_init },
        { 0, NULL },
    };
    if (mpv_render_context_create(&v->rc, v->mpv, params) < 0) {
        fprintf(stderr, "lp-transition: mpv render context create failed\n");
        mpv_destroy(v->mpv); v->mpv = NULL; return false;
    }
    const char *cmd[] = { "loadfile", path, NULL };
    mpv_command(v->mpv, cmd);
    return true;
}

// Pull the latest decoded frame into the FBO texture (when one is ready). Leaves mpv's GL state
// dirty — the caller restores framebuffer/viewport/program before compositing the effect.
static void vidsrc_render(struct vid_src *v) {
    if (!v->mpv || !v->rc) return;
    while (mpv_wait_event(v->mpv, 0)->event_id != MPV_EVENT_NONE) { } // drain events → decoding proceeds
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
    }
}

static void vidsrc_free(struct vid_src *v) {
    if (v->rc) { mpv_render_context_free(v->rc); v->rc = NULL; }   // GL context must be current
    if (v->mpv) { mpv_destroy(v->mpv); v->mpv = NULL; }
    if (v->fbo) { glDeleteFramebuffers(1, &v->fbo); v->fbo = 0; }
    if (v->tex) { glDeleteTextures(1, &v->tex); v->tex = 0; }
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
    s->tex_from = upload_tex(s->cfg->from, s->cfg->w, s->cfg->h);
    s->tex_to   = upload_tex(s->cfg->to,   s->cfg->w, s->cfg->h);
    if (!s->tex_from || !s->tex_to) return false;
    glUseProgram(s->prog);
    glUniform1i(glGetUniformLocation(s->prog, "fromTex"), 0);
    glUniform1i(glGetUniformLocation(s->prog, "toTex"), 1);
    apply_uniforms(s->prog);
    // full-live: bring up the actual video decoders (the still textures above stay as the fallback
    // until the first decoded frame arrives, and remain the source for scene sides with no video).
    // A resumes from mpvpaper-A's CURRENT position: advance the sampled start by the wall-clock
    // elapsed since it was sampled, so the overlay's A lines up with the live A it's covering.
    double a_start = from_start;
    if (from_video && from_epoch > 0.0) { double d = now_realtime() - from_epoch; if (d > 0) a_start += d; }
    if (from_duration > 0.01) a_start = fmod(a_start, from_duration); // wrap for looping videos (no overshoot)
    // ONE surface carries audio (else multi-monitor = duplicate A/B streams → echo). Crossfaded in render().
    bool wants_audio = audio_volume > 0.0 && !audio_claimed && from_video && to_video;
    if (wants_audio) { audio_claimed = true; s->has_audio = true; }
    if (from_video) vidsrc_init(&s->vfrom, from_video, a_start, s->w, s->h, false,     wants_audio); // A tracks the live A underneath
    if (to_video)   vidsrc_init(&s->vto,   to_video,   to_start, s->w, s->h, to_paused, wants_audio); // B held until first paint
    return true;
}

static const struct wl_callback_listener frame_listener;

// ---- warm-up gate ----------------------------------------------------------
// A live side is "pending" only if its decoder is alive and hasn't produced its first frame yet (a
// decoder that FAILED to init falls back to the still and is NOT waited on; a scene side has no
// video and isn't either). We start the effect only when every surface is warm → the opaque effect
// never composites a frozen/stale still, and B switches only once everything is covered.
static bool side_pending(struct vid_src *v, const char *video) { return video && v->mpv && !v->ready; }
static bool surface_warm(struct surface *s) {
    return !side_pending(&s->vfrom, from_video) && !side_pending(&s->vto, to_video);
}
static bool all_surfaces_warm(void) {
    for (int i = 0; i < n_surfaces; i++) if (!surface_warm(&surfaces[i])) return false;
    return true;
}
static void schedule_frame(struct surface *s) {
    struct wl_callback *cb = wl_surface_frame(s->wl_surface);
    wl_callback_add_listener(cb, &frame_listener, s);
}

static void render(struct surface *s) {
    eglMakeCurrent(egl_dpy, s->egl_surface, s->egl_surface, egl_ctx);
    frame_count++;
    // advance each live decoder into its FBO texture (no-op for still/scene sides)
    vidsrc_render(&s->vfrom);
    vidsrc_render(&s->vto);

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
        if (all_surfaces_warm()) {
            go_time = start_time = now_s();
            for (int i = 0; i < n_surfaces; i++) {
                struct surface *o = &surfaces[i];
                if (o->vfrom.paused) { mpv_set_property_string(o->vfrom.mpv, "pause", "no"); o->vfrom.paused = false; }
                if (o->vto.paused)   { mpv_set_property_string(o->vto.mpv,   "pause", "no"); o->vto.paused = false; }
            }
            if (ready_file) { FILE *rf = fopen(ready_file, "w"); if (rf) fclose(rf); }
        } else {
            glViewport(0, 0, s->w, s->h);
            glClearColor(0, 0, 0, 0); glClear(GL_COLOR_BUFFER_BIT); // transparent → wallpaper shows
            schedule_frame(s);
            eglSwapBuffers(egl_dpy, s->egl_surface);
            return;
        }
    }

    // ── TRANSITION: composite the effect over live A → live B ─────────────────────────────────
    double p = smoothstep01((now_s() - go_time) / duration_s);
    // Partway through, pre-seek mpvpaper's paused/hidden B to where it will hand off (= duration),
    // so the frame is fully decoded BEFORE the reveal — no decode-latency jump-to-0 at the end.
    if (!b_preseeked && mpv_unpause && *mpv_unpause && (now_s() - go_time) > duration_s * 0.25) {
        char c[160];
        snprintf(c, sizeof c, "{\"command\":[\"seek\",%.3f,\"absolute\",\"exact\"]}\n", duration_s);
        mpv_cmd(mpv_unpause, c);
        b_preseeked = true;
    }
    // audio crossfade A→B tracking the visual progress (only the surface that owns audio)
    if (s->has_audio) {
        char vb[24];
        snprintf(vb, sizeof vb, "%.1f", (1.0 - p) * audio_volume); mpv_set_property_string(s->vfrom.mpv, "volume", vb);
        snprintf(vb, sizeof vb, "%.1f", p * audio_volume);         mpv_set_property_string(s->vto.mpv,   "volume", vb);
    }
    glViewport(0, 0, s->w, s->h);
    glClearColor(0, 0, 0, 1); glClear(GL_COLOR_BUFFER_BIT);
    // Keep the surface fully OPAQUE: clear alpha=1, then mask off the alpha channel so the effect
    // writes only RGB. mpv's FBO textures carry alpha ≠ 1, so without this the blended output alpha
    // is < 1 and the compositor lets the desktop show THROUGH the transition (see-through overlay).
    glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_FALSE);
    glUseProgram(s->prog);
    glUniform1f(s->u_progress, (float)p);
    if (s->u_ratio >= 0) glUniform1f(s->u_ratio, (float)s->w / (float)s->h);
    GLuint tf = s->vfrom.ready ? s->vfrom.tex : s->tex_from; // live frame, else still (scene/failed side)
    GLuint tt = s->vto.ready   ? s->vto.tex   : s->tex_to;
    glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, tf);
    glActiveTexture(GL_TEXTURE1); glBindTexture(GL_TEXTURE_2D, tt);
    glBindVertexArray(s->vao);
    glDrawArrays(GL_TRIANGLES, 0, 3);
    glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);

    if (p >= 1.0) s->done = true;
    else schedule_frame(s);
    eglSwapBuffers(egl_dpy, s->egl_surface);
    // tell mpv the frame was presented → it can pace decoding to real display timing (without this
    // its frame delivery judders / drops to a low effective rate, especially with several decoders)
    if (s->vfrom.rc) mpv_render_context_report_swap(s->vfrom.rc);
    if (s->vto.rc)   mpv_render_context_report_swap(s->vto.rc);
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
    // shaders are read once by caller path; stashed on first surface via globals
    extern char *g_vsrc, *g_fsrc;
    if (!surface_init_gl(s, g_vsrc, g_fsrc)) { fprintf(stderr, "lp-transition: GL init failed\n"); exit(1); }
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
        else if (!strcmp(a, "--mpv-unpause")) mpv_unpause = argv[++i];
        else if (!strcmp(a, "--from-video")) from_video = argv[++i];
        else if (!strcmp(a, "--to-video")) to_video = argv[++i];
        else if (!strcmp(a, "--from-start")) from_start = atof(argv[++i]);
        else if (!strcmp(a, "--to-start")) to_start = atof(argv[++i]);
        else if (!strcmp(a, "--from-epoch")) from_epoch = atof(argv[++i]);
        else if (!strcmp(a, "--from-duration")) from_duration = atof(argv[++i]);
        else if (!strcmp(a, "--audio-volume")) audio_volume = atof(argv[++i]);
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
    parse_args(argc, argv);
    if (!vert_path || !frag_path || !n_out_cfgs) {
        fprintf(stderr, "lp-transition: need --vert --frag and at least one --output\n");
        return 2;
    }
    g_vsrc = read_file(vert_path); g_fsrc = read_file(frag_path);
    if (!g_vsrc || !g_fsrc) return 1;

    display = wl_display_connect(NULL);
    if (!display) { fprintf(stderr, "lp-transition: no Wayland display\n"); return 1; }
    struct wl_registry *reg = wl_display_get_registry(display);
    wl_registry_add_listener(reg, &reg_listener, NULL);
    wl_display_roundtrip(display);   // globals
    wl_display_roundtrip(display);   // output name/geometry events
    if (!compositor || !layer_shell) { fprintf(stderr, "lp-transition: missing compositor/layer-shell\n"); return 1; }

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

    // event loop until every surface finishes (+ a hard safety timeout that covers decoder warm-up)
    double deadline = now_s() + duration_s + 8.0;
    while (wl_display_dispatch(display) != -1) {
        bool all_done = true;
        for (int i = 0; i < n_surfaces; i++) if (!surfaces[i].done) all_done = false;
        if (all_done || now_s() > deadline) break;
    }

    // 0) Silence the overlay's audio BEFORE mpvpaper's B resumes — the overlay decoders live ~1s
    //    longer (async teardown) and would otherwise echo B over mpvpaper's now-audible B.
    for (int i = 0; i < n_surfaces; i++) {
        if (surfaces[i].vfrom.mpv) mpv_set_property_string(surfaces[i].vfrom.mpv, "mute", "yes");
        if (surfaces[i].vto.mpv)   mpv_set_property_string(surfaces[i].vto.mpv,   "mute", "yes");
    }
    // 1) B handoff: B was pre-seeked to the handoff position mid-effect (decoded + ready), so just
    //    UNPAUSE it here — it continues from the matched frame with no decode-latency backward jump.
    if (mpv_unpause && *mpv_unpause) mpv_cmd(mpv_unpause, "{\"command\":[\"set_property\",\"pause\",false]}\n");

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
    // tear down the live decoders (GL context must be current for the render-context cleanup)
    for (int i = 0; i < n_surfaces; i++) {
        eglMakeCurrent(egl_dpy, surfaces[i].egl_surface, surfaces[i].egl_surface, egl_ctx);
        vidsrc_free(&surfaces[i].vfrom);
        vidsrc_free(&surfaces[i].vto);
    }
    if (on_finish && *on_finish) { int r = system(on_finish); (void)r; }
    return 0;
}
