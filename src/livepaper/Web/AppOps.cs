using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using livepaper.Helpers;
using livepaper.Models;

namespace livepaper.Web;

// Backend orchestration for the play / download / trash flows, callable from the API endpoints.
public static class AppOps
{
    private sealed record UndoBatch(string BatchDir, List<LibraryItem> Items);
    private static readonly List<UndoBatch> _undo = new();
    private static readonly object _undoLock = new();

    // ---- effective playlist settings (override-global vs global) ----
    private static int EffInterval(AppSettings s, PlaylistSettings p) =>
        p.OverrideGlobalSettings ? p.IntervalSeconds : s.GlobalIntervalSeconds;
    private static bool EffAdvance(AppSettings s, PlaylistSettings p) =>
        p.OverrideGlobalSettings ? p.AdvanceOnVideoEnd : s.GlobalAdvanceOnVideoEnd;
    private static bool EffWait(AppSettings s, PlaylistSettings p) =>
        p.OverrideGlobalSettings ? p.WaitForVideoEnd : s.GlobalWaitForVideoEnd;

    // `paths` are already in the desired order (the UI sends the current strip order).
    public static string PlayPlaylist(List<string> paths, PlaylistSettings ps)
    {
        if (paths.Count == 0) return "Playlist is empty";
        // Persist what's actually playing as the current playlist state. TransitionService.CurrentConfig()
        // (the transition/crossfade gate) reads playlist_state.json — without this, /playlist/play only
        // saved LastSession, so the state stayed stale and scene switches never transitioned/crossfaded.
        try
        {
            var existing = PlaylistService.LoadCurrentState();
            PlaylistService.SaveCurrentState(new CustomPlaylist { VideoPaths = paths, Settings = ps, Name = existing?.Name ?? "" });
        }
        catch { }
        var s = SettingsService.Load();
        bool shuffle = ps.Order == PlaylistOrder.Shuffle;
        int interval = EffInterval(s, ps);
        bool wait = EffWait(s, ps);

        if (EffAdvance(s, ps))
        {
            if (interval > 0)
            {
                var play = shuffle ? paths.OrderBy(_ => Guid.NewGuid()).ToList() : paths;
                PlayerHelper.ApplyTimedPlaylist(play, s.BuildMpvOptions(), shuffle, interval, wait, advanceOnVideoEnd: true);
                Save(s, new LastSession { IsTimedPlaylist = true, Paths = paths, Shuffle = shuffle, TimedIntervalSeconds = interval, WaitForVideoEnd = wait, AdvanceOnVideoEnd = true, OverrideGlobalSettings = ps.OverrideGlobalSettings });
                return "";
            }
            PlayerHelper.ApplyPlaylist(paths, s.BuildMpvPlaylistOptions(), shuffle, 0);
            Save(s, PlayerHelper.IsTimedPlaylistActive()
                ? new LastSession { IsTimedPlaylist = true, Paths = paths, Shuffle = shuffle, TimedIntervalSeconds = 0, WaitForVideoEnd = true, AdvanceOnVideoEnd = true, OverrideGlobalSettings = ps.OverrideGlobalSettings }
                : new LastSession { IsPlaylist = true, Paths = paths, Shuffle = shuffle, AdvanceOnVideoEnd = true, OverrideGlobalSettings = ps.OverrideGlobalSettings });
            return "";
        }

        if (interval == 0 && paths.Count > 1) return "Set an interval greater than 0 to use timed playlists";
        var timed = shuffle ? paths.OrderBy(_ => Guid.NewGuid()).ToList() : paths;
        PlayerHelper.ApplyTimedPlaylist(timed, s.BuildMpvOptions(), shuffle, interval, wait);
        Save(s, new LastSession { IsTimedPlaylist = true, Paths = paths, Shuffle = shuffle, TimedIntervalSeconds = interval, WaitForVideoEnd = wait, AdvanceOnVideoEnd = false, OverrideGlobalSettings = ps.OverrideGlobalSettings });
        return "";
    }

    // Clicked path plays first, the rest follow (shuffled if shuffle).
    public static string PlayFrom(List<string> paths, int startIndex, PlaylistSettings ps)
    {
        if (paths.Count == 0 || startIndex < 0 || startIndex >= paths.Count) return "bad index";
        bool shuffle = ps.Order == PlaylistOrder.Shuffle;
        var rest = paths.Where((_, i) => i != startIndex).ToList();
        if (shuffle) rest = rest.OrderBy(_ => Guid.NewGuid()).ToList();
        var ordered = new List<string> { paths[startIndex] }.Concat(rest).ToList();
        return PlayPlaylist(ordered, new PlaylistSettings
        {
            Order = PlaylistOrder.Sequential, // pre-arranged; don't reshuffle
            OverrideGlobalSettings = ps.OverrideGlobalSettings, IntervalSeconds = ps.IntervalSeconds,
            AdvanceOnVideoEnd = ps.AdvanceOnVideoEnd, WaitForVideoEnd = ps.WaitForVideoEnd,
        });
    }

    private static void Save(AppSettings s, LastSession sess) { s.LastSession = sess; SettingsService.Save(s); }

    // animated preview in an acquired workshop folder (preview.gif/webp), if any
    private static string? ResolveLocalAnimatedPreview(string dir)
    {
        foreach (var ext in new[] { ".gif", ".webp" })
        {
            string f = System.IO.Path.Combine(dir, "preview" + ext);
            if (System.IO.File.Exists(f)) return f;
        }
        return null;
    }

    // Reveal the real media in the file manager. Scene → CopiedSceneDir or <WE>/<workshopId>;
    // video → resolve the symlink → reveal the file.
    public static void RevealLibraryItem(string path, bool isScene, string? workshopId, string? copiedSceneDir)
    {
        string? dir = null, filePath = null;
        if (isScene)
        {
            if (!string.IsNullOrEmpty(copiedSceneDir) && Directory.Exists(copiedSceneDir)) dir = copiedSceneDir;
            else if (!string.IsNullOrEmpty(workshopId))
            {
                var wePath = SettingsService.Load().WallpaperEnginePath;
                if (!string.IsNullOrEmpty(wePath)) dir = Path.Combine(wePath, workshopId);
            }
        }
        if (dir == null)
        {
            string real = path;
            try { var t = File.ResolveLinkTarget(path, returnFinalTarget: true); if (t != null) real = t.FullName; } catch { }
            filePath = real;
            dir = Path.GetDirectoryName(real);
        }
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (filePath != null && File.Exists(filePath) && TryShowItem(filePath)) return;
        try { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { dir }, UseShellExecute = false }); } catch { }
    }

    // dbus FileManager1.ShowItems — highlights the file in the file manager (vs just opening the dir).
    private static bool TryShowItem(string path)
    {
        try
        {
            string uri = new Uri(path).AbsoluteUri;
            var psi = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:" })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(2000);
            return proc?.ExitCode == 0;
        }
        catch { return false; }
    }

    // ffprobe the video length in seconds.
    public static double GetDurationSeconds(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", path })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit(4000);
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out double s)) return s;
        }
        catch { }
        return 0;
    }

    // Web-source download OR workshop acquire (subscribe/steamcmd) then import into the library.
    public static async Task<LibraryItem> DownloadAsync(WallpaperDetail detail, string thumbnailUrl, string sourceId, CancellationToken ct, string? animatedUrl = null)
    {
        var s = SettingsService.Load();
        var progress = new Progress<double>(v =>
            EventBus.Broadcast("download-progress", new { id = sourceId, value = v }));

        WallpaperDetail dl = detail;
        string? anim = animatedUrl;
        if (detail.IsWorkshopAcquire && detail.WorkshopId != null)
        {
            var ap = new Progress<(double, string)>(t =>
                EventBus.Broadcast("download-progress", new { id = sourceId, value = Math.Max(0, t.Item1), status = t.Item2 }));
            string dir = await WorkshopDownloader.AcquireAsync(detail.WorkshopId, s, ap, ct);
            string url = dir;
            if (!detail.IsScene)
            {
                var vf = await WorkshopDownloader.ResolveVideoFileAsync(dir);
                if (!string.IsNullOrEmpty(vf)) url = vf;
            }
            // animated preview lives in the acquired workshop folder (preview.gif/webp)
            anim = ResolveLocalAnimatedPreview(dir) ?? anim;
            dl = new WallpaperDetail { Title = detail.Title, PreviewUrl = thumbnailUrl, DownloadUrl = url, IsScene = detail.IsScene, WorkshopId = detail.WorkshopId, NeedsReferrer = false,
                Resolution = detail.Resolution, AgeRating = detail.AgeRating, YoutubeUrl = detail.YoutubeUrl,
                PageUrl = detail.PageUrl, AuthorName = detail.AuthorName, Description = detail.Description,
                FileSizeBytes = detail.FileSizeBytes, Subscriptions = detail.Subscriptions,
                Favorites = detail.Favorites, Views = detail.Views, Tags = detail.Tags };
        }

        var item = await DownloadHelper.DownloadAsync(dl, thumbnailUrl, sourceId, detail.IsWorkshopAcquire ? null : progress, s.WeCopyFiles, ct, animatedUrl: anim);
        if (item.WorkshopId is string wid) WorkshopUnsubQueue.Unblock(wid); // re-adding an item clears any pending unsubscribe
        EventBus.Broadcast("download-progress", new { id = sourceId, value = 1.0, done = true });
        return item;
    }

    // Delete from source — enqueue the (durable) unsubscribe + soft-delete the entries.
    public static string DeleteFromSource(List<LibraryItem> items)
    {
        var ids = items.Where(i => i.WorkshopId != null).Select(i => i.WorkshopId!).Distinct();
        WorkshopUnsubQueue.AddPending(ids);
        return Trash(items);
    }

    // Soft-delete to trash; if the playing item was among them, stop or advance playback.
    public static string Trash(List<LibraryItem> items)
    {
        if (items.Count == 0) return "";
        string batchDir = Path.Combine(LibraryService.TrashPath, Guid.NewGuid().ToString("N")[..8]);
        var done = new List<LibraryItem>();
        var playing = PlayerHelper.QueryCurrentPath();
        bool playingDeleted = playing != null && items.Any(i => i.VideoPath == playing);

        foreach (var item in items)
        {
            try { LibraryService.Trash(item, batchDir); done.Add(item); } catch { }
        }
        if (done.Count > 0) lock (_undoLock) _undo.Add(new UndoBatch(batchDir, done));

        if (playingDeleted) StopOrAdvanceAfterDelete(items);
        return batchDir;
    }

    // Playlist re-plays its survivors (advancing past the deleted item); a single item / no survivor stops.
    private static void StopOrAdvanceAfterDelete(List<LibraryItem> deleted)
    {
        var s = SettingsService.Load();
        var sess = s.LastSession;
        var del = new HashSet<string>(deleted.Select(i => i.VideoPath));
        bool isPlaylist = sess != null && (sess.IsPlaylist || sess.IsTimedPlaylist);
        if (isPlaylist && sess != null)
        {
            var survivors = sess.Paths.Where(p => !del.Contains(p)).ToList();
            if (survivors.Count > 0)
            {
                PlayPlaylist(survivors, new PlaylistSettings
                {
                    Order = sess.Shuffle ? PlaylistOrder.Shuffle : PlaylistOrder.Sequential,
                    OverrideGlobalSettings = sess.OverrideGlobalSettings,
                    IntervalSeconds = sess.TimedIntervalSeconds,
                    AdvanceOnVideoEnd = sess.AdvanceOnVideoEnd, WaitForVideoEnd = sess.WaitForVideoEnd,
                });
                return;
            }
        }
        PlayerHelper.Stop();
        EventBus.Broadcast("wallpaper-changed", new { path = (string?)null });
    }

    public record UndoResult(bool Ok, int Count, string? Title);
    public static UndoResult Undo()
    {
        UndoBatch? batch;
        lock (_undoLock)
        {
            if (_undo.Count == 0) return new(false, 0, null);
            batch = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        }
        LibraryService.RestoreBatch(batch.BatchDir);
        return new(true, batch.Items.Count, batch.Items.Count > 0 ? batch.Items[0].Title : null);
    }

    public static int UndoDepth() { lock (_undoLock) return _undo.Count; }
    public static void PurgeTrash() => LibraryService.CleanTrash();
}
