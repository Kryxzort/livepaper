// lp-audio — libpulse helper for gapless scene-audio crossfades in livepaper.
//
// Shelling `pactl` per stream is too slow (~10-40ms list/spawn latency) to beat PulseAudio's
// default-volume-on-stream-create and the connect/disconnect volume resets that LWE triggers when a
// scene starts/stops → audible flashes. This helper holds a persistent libpulse context, subscribes
// to sink-input events, and applies the wanted volume IN-PROCESS (~µs) the instant a stream is
// (re)created or changed — so a fresh/re-inited stream never plays at the wrong volume.
//
// It is driven by the backend over stdin (one command per line):
//   set <pid,pid,...> <volPercent> <mute0|1>   upsert a "group" = these process PIDs want this vol/mute
//   clear                                        drop all groups (stop controlling)
//   quit                                         exit
//
// A sink-input is matched to a group by its owning process PID (application.process.id in the
// proplist) — stable across LWE's client/stream re-creation, unlike the sink-input index. On every
// sink-input new/change event AND on every `set`, each matching stream is set to its group's vol/mute.

#define _POSIX_C_SOURCE 200809L
#include <pulse/pulseaudio.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#define MAX_GROUPS 8
#define MAX_PIDS   16
#define MAX_CLIENTS 64

struct group { pid_t pids[MAX_PIDS]; int npids; int vol; int mute; };

static pa_mainloop *g_m;     // single-threaded mainloop (stdin is a PA IO event → no locking needed)
static pa_context *g_ctx;
static FILE *g_log = NULL;   // LP_AUDIO_DEBUG=<path> → diagnostic log (states, commands, applies)
#define LOG(...) do { if (g_log) { fprintf(g_log, __VA_ARGS__); fflush(g_log); } } while (0)
static struct group g_groups[MAX_GROUPS];
static int g_ngroups = 0;

// client.index → owning process PID. The PID lives on the CLIENT proplist (application.process.id),
// NOT on the sink-input proplist — so a sink-input is mapped to a PID via its `client` index here.
static struct { uint32_t idx; pid_t pid; } g_clients[MAX_CLIENTS];
static int g_nclients = 0;

static pid_t pid_for_client(uint32_t idx) {
    for (int i = 0; i < g_nclients; i++) if (g_clients[i].idx == idx) return g_clients[i].pid;
    return 0;
}
static void client_set(uint32_t idx, pid_t pid) {
    for (int i = 0; i < g_nclients; i++) if (g_clients[i].idx == idx) { g_clients[i].pid = pid; return; }
    if (g_nclients < MAX_CLIENTS) { g_clients[g_nclients].idx = idx; g_clients[g_nclients].pid = pid; g_nclients++; }
    else { // full → recycle the oldest slot (FIFO). LWE churns clients constantly; without this the
           // cache fills with dead entries and new scenes' clients can't be cached → never resolve → stuck.
        memmove(&g_clients[0], &g_clients[1], sizeof(g_clients[0]) * (MAX_CLIENTS - 1));
        g_clients[MAX_CLIENTS - 1].idx = idx; g_clients[MAX_CLIENTS - 1].pid = pid;
    }
}
// Evict a client (on PA 'remove') so the cache tracks only LIVE clients and never fills with dead ones.
static void client_del(uint32_t idx) {
    for (int i = 0; i < g_nclients; i++)
        if (g_clients[i].idx == idx) {
            memmove(&g_clients[i], &g_clients[i + 1], sizeof(g_clients[0]) * (g_nclients - i - 1));
            g_nclients--;
            return;
        }
}

// Find the group that owns `pid`, or -1. (Caller holds the mainloop lock.)
static int group_for_pid(pid_t pid) {
    for (int g = 0; g < g_ngroups; g++)
        for (int i = 0; i < g_groups[g].npids; i++)
            if (g_groups[g].pids[i] == pid) return g;
    return -1;
}

// Apply a group's vol/mute to one sink-input (index `idx`, `channels` channels).
static void apply_to(uint32_t idx, int channels, int vol, int mute) {
    pa_cvolume cv;
    if (channels < 1) channels = 2;
    pa_cvolume_set(&cv, channels, (pa_volume_t)((PA_VOLUME_NORM * (vol < 0 ? 0 : vol > 150 ? 150 : vol)) / 100));
    pa_operation *o = pa_context_set_sink_input_volume(g_ctx, idx, &cv, NULL, NULL);
    if (o) pa_operation_unref(o);
    o = pa_context_set_sink_input_mute(g_ctx, idx, mute ? 1 : 0, NULL, NULL);
    if (o) pa_operation_unref(o);
}

// client info callback: cache client.index → application.process.id (the real owning PID).
static void client_info_cb(pa_context *c, const pa_client_info *ci, int eol, void *ud) {
    (void)c; (void)ud;
    if (eol || !ci || !ci->proplist) return;
    const char *pids = pa_proplist_gets(ci->proplist, "application.process.id");
    if (pids) client_set(ci->index, (pid_t)atoi(pids));
}

// sink-input info callback: map via its client → PID, classify, apply the owning group's vol/mute.
static void si_info_cb(pa_context *c, const pa_sink_input_info *i, int eol, void *ud) {
    (void)c; (void)ud;
    if (eol || !i || i->client == PA_INVALID_INDEX) return;
    pid_t pid = pid_for_client(i->client);
    if (pid == 0) return;   // client→pid not cached yet (a client event will re-trigger)
    int g = group_for_pid(pid);
    if (g < 0) return;
    // Skip if the stream is ALREADY at the wanted vol+mute — otherwise setting it fires a 'change'
    // event → we re-apply → 'change' → … an infinite loop that storms PulseAudio (and wedges it).
    int cur = (int)((pa_cvolume_avg(&i->volume) * 100 + PA_VOLUME_NORM / 2) / PA_VOLUME_NORM);
    int want = g_groups[g].vol < 0 ? 0 : g_groups[g].vol > 150 ? 150 : g_groups[g].vol;
    if (cur == want && (i->mute ? 1 : 0) == (g_groups[g].mute ? 1 : 0)) return;
    LOG("apply idx=%u pid=%d %d%%→%d%% mute→%d\n", i->index, (int)pid, cur, want, g_groups[g].mute);
    apply_to(i->index, i->channel_map.channels, g_groups[g].vol, g_groups[g].mute);
}

static void reapply_all(void);  // fwd

// PA events on the mainloop thread:
//  - CLIENT new/change → refresh its idx→pid, then re-scan sink-inputs (a sink-input event may have
//    fired before its client was cached → catch it now).
//  - SINK_INPUT new/change → map via client → classify → apply.
static void subscribe_cb(pa_context *c, pa_subscription_event_type_t t, uint32_t idx, void *ud) {
    (void)ud;
    int fac = t & PA_SUBSCRIPTION_EVENT_FACILITY_MASK;
    int op = t & PA_SUBSCRIPTION_EVENT_TYPE_MASK;
    if (fac == PA_SUBSCRIPTION_EVENT_CLIENT) {
        if (op == PA_SUBSCRIPTION_EVENT_REMOVE) { client_del(idx); return; }
        pa_operation *o = pa_context_get_client_info(c, idx, client_info_cb, NULL);
        if (o) pa_operation_unref(o);
        reapply_all();   // client now known → (re)apply to its streams
    } else if (fac == PA_SUBSCRIPTION_EVENT_SINK_INPUT) {
        if (op == PA_SUBSCRIPTION_EVENT_REMOVE) return;
        pa_operation *o = pa_context_get_sink_input_info(c, idx, si_info_cb, NULL);
        if (o) pa_operation_unref(o);
    }
}

static void ctx_state_cb(pa_context *c, void *ud) {
    (void)ud;
    LOG("state=%d\n", pa_context_get_state(c));
    if (pa_context_get_state(c) == PA_CONTEXT_READY) {
        pa_context_set_subscribe_callback(c, subscribe_cb, NULL);
        pa_operation *o = pa_context_subscribe(c, PA_SUBSCRIPTION_MASK_SINK_INPUT | PA_SUBSCRIPTION_MASK_CLIENT, NULL, NULL);
        if (o) pa_operation_unref(o);
        // seed the client→pid cache with existing clients
        o = pa_context_get_client_info_list(c, client_info_cb, NULL);
        if (o) pa_operation_unref(o);
    }
}

// Re-apply ALL groups to every current sink-input (after a `set`, so existing streams update now).
static void reapply_all(void) {
    pa_operation *o = pa_context_get_sink_input_info_list(g_ctx, si_info_cb, NULL);
    if (o) pa_operation_unref(o);
}

// Parse "set <pidcsv> <vol> <mute>" → upsert a group keyed by its pid set. (Single-threaded; no lock.)
static void cmd_set(char *args) {
    char *pidcsv = strtok(args, " \t");
    char *vols   = strtok(NULL, " \t");
    char *mutes  = strtok(NULL, " \t\n");
    if (!pidcsv || !vols) return;
    struct group ng; ng.npids = 0; ng.vol = atoi(vols); ng.mute = mutes ? atoi(mutes) : 0;
    for (char *p = strtok(pidcsv, ","); p && ng.npids < MAX_PIDS; p = strtok(NULL, ","))
        ng.pids[ng.npids++] = (pid_t)atoi(p);
    if (ng.npids == 0) return;
    // match an existing group by identical first pid → update; else append
    int slot = -1;
    for (int g = 0; g < g_ngroups; g++)
        if (g_groups[g].npids > 0 && g_groups[g].pids[0] == ng.pids[0]) { slot = g; break; }
    if (slot < 0 && g_ngroups < MAX_GROUPS) slot = g_ngroups++;
    if (slot >= 0) g_groups[slot] = ng;
    reapply_all();
}

// stdin is a PA IO event on the single mainloop thread → commands + PA callbacks never race.
static char g_inbuf[4096]; static size_t g_inlen = 0;
static void handle_line(char *line) {
    LOG("cmd: %s\n", line);
    if (!strncmp(line, "set ", 4)) cmd_set(line + 4);
    else if (!strcmp(line, "clear")) g_ngroups = 0;
    else if (!strcmp(line, "quit")) pa_mainloop_quit(g_m, 0);
}
static void stdin_cb(pa_mainloop_api *a, pa_io_event *e, int fd, pa_io_event_flags_t f, void *ud) {
    (void)a; (void)e; (void)ud;
    if (!(f & PA_IO_EVENT_INPUT)) return;
    ssize_t n = read(fd, g_inbuf + g_inlen, sizeof g_inbuf - 1 - g_inlen);
    if (n <= 0) { pa_mainloop_quit(g_m, 0); return; }   // EOF (parent closed) → exit
    g_inlen += (size_t)n; g_inbuf[g_inlen] = 0;
    char *start = g_inbuf, *nl;
    while ((nl = strchr(start, '\n')) != NULL) { *nl = 0; handle_line(start); start = nl + 1; }
    g_inlen = strlen(start); memmove(g_inbuf, start, g_inlen + 1);
}

int main(void) {
    const char *dbg = getenv("LP_AUDIO_DEBUG");
    if (dbg && *dbg) g_log = (dbg[0] == '/' ) ? fopen(dbg, "w") : stderr;
    g_m = pa_mainloop_new();
    pa_mainloop_api *api = pa_mainloop_get_api(g_m);
    g_ctx = pa_context_new(api, "lp-audio");
    pa_context_set_state_callback(g_ctx, ctx_state_cb, NULL);
    if (pa_context_connect(g_ctx, NULL, 0, NULL) < 0) {
        fprintf(stderr, "lp-audio: connect failed: %s\n", pa_strerror(pa_context_errno(g_ctx)));
        return 1;
    }
    api->io_new(api, 0 /*stdin*/, PA_IO_EVENT_INPUT, stdin_cb, NULL);
    int ret = 0;
    pa_mainloop_run(g_m, &ret);   // blocks; pumps PA + stdin until quit/EOF
    pa_context_disconnect(g_ctx);
    pa_context_unref(g_ctx);
    pa_mainloop_free(g_m);
    return 0;
}
