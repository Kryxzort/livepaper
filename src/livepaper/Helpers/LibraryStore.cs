using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace livepaper.Helpers;

// One JSON index per source folder under the library, holding each item's metadata, per-item
// volume/speed overrides, and dedup keys. Keyed by the item's folder name (<id> for workshop/local,
// <name> for web/imported). null Volume/Speed = "use global" (the key is simply absent when
// serialized → WhenWritingNull). Layout: library/<source>/<key>/<media>.
public sealed class LibMeta
{
    public string? SourceUrl { get; set; }   // web page URL (dedup) — null for workshop/local (id = folder)
    public string? Title { get; set; }
    public bool IsScene { get; set; }
    public string? WorkshopId { get; set; }
    public int? Volume { get; set; }          // null = use global
    public double? Speed { get; set; }        // null = use global
    // EBU R128 loudness measured once (ffmpeg loudnorm pass 1) → normalizes loudness between videos.
    // All four present = measured; absent = not analyzed. Target-independent (gain applied live).
    public double? LoudnessI { get; set; }      // integrated loudness (LUFS)
    public double? LoudnessTP { get; set; }     // true peak (dBTP)
    public double? LoudnessLRA { get; set; }    // loudness range (LU)
    public double? LoudnessThresh { get; set; } // measurement threshold
    public bool? LoudnessSkip { get; set; }     // true = no audio track → nothing to normalize, don't retry
    public bool Crashed { get; set; }
    public bool Whitelist { get; set; }
    public string? Resolution { get; set; }
    public string? AgeRating { get; set; }
    public string? YoutubeUrl { get; set; }
    public string? PageUrl { get; set; }
    public string? AuthorName { get; set; }
    public string? Description { get; set; }
    public long? FileSizeBytes { get; set; }
    public long? Subscriptions { get; set; }
    public long? Favorites { get; set; }
    public long? Views { get; set; }
    public string[]? Tags { get; set; }
}

public static class LibraryStore
{
    public const string MotionBgs = "motionbgs", Moewalls = "moewalls", Desktophut = "desktophut",
        Imported = "imported", Workshop = "workshop", Local = "local";
    public static readonly string[] Sources = { MotionBgs, Moewalls, Desktophut, Imported, Workshop, Local };

    private static string Root => DownloadHelper.LibraryPath;
    public static string SourceDir(string source) => Path.Combine(Root, source);
    private static string IndexPath(string source) => Path.Combine(SourceDir(source), "index.json");

    private static readonly object _lock = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // absent volume/speed == "use global"
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Dictionary<string, LibMeta> LoadIndex(string source)
    {
        try
        {
            var p = IndexPath(source);
            if (!File.Exists(p)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, LibMeta>>(File.ReadAllText(p), _json) ?? new();
        }
        catch { return new(); }
    }

    public static void SaveIndex(string source, Dictionary<string, LibMeta> idx)
    {
        try
        {
            Directory.CreateDirectory(SourceDir(source));
            File.WriteAllText(IndexPath(source), JsonSerializer.Serialize(idx, _json));
        }
        catch { }
    }

    // mediaPath (library/<source>/<key>/<file>…) → (source, key). Null if outside the library / malformed.
    public static (string Source, string Key)? Locate(string mediaPath)
    {
        try
        {
            var full = Path.GetFullPath(mediaPath);
            var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.Ordinal)) return null;
            var parts = full.Substring(root.Length).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? (parts[0], parts[1]) : null;
        }
        catch { return null; }
    }

    public static LibMeta? GetMeta(string mediaPath)
    {
        var loc = Locate(mediaPath);
        if (loc == null) return null;
        return LoadIndex(loc.Value.Source).TryGetValue(loc.Value.Key, out var m) ? m : null;
    }

    // Read-modify-write the item's index entry under a lock. Creates the entry if missing.
    public static void SetMeta(string mediaPath, Action<LibMeta> mutate)
    {
        var loc = Locate(mediaPath);
        if (loc == null) return;
        lock (_lock)
        {
            var idx = LoadIndex(loc.Value.Source);
            if (!idx.TryGetValue(loc.Value.Key, out var m)) { m = new LibMeta(); idx[loc.Value.Key] = m; }
            mutate(m);
            SaveIndex(loc.Value.Source, idx);
        }
    }

    public static void Remove(string mediaPath)
    {
        var loc = Locate(mediaPath);
        if (loc == null) return;
        lock (_lock)
        {
            var idx = LoadIndex(loc.Value.Source);
            if (idx.Remove(loc.Value.Key)) SaveIndex(loc.Value.Source, idx);
        }
    }
}
