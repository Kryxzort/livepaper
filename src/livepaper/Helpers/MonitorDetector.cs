using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace livepaper.Helpers;

// A detected output: its name, current refresh rate, and whether it's the primary (main) display.
// X/Y are the layout origin (used server-side to pick the primary); not part of the /monitors contract.
public record MonitorInfo(string Name, int RefreshHz, bool Primary)
{
    [JsonIgnore] public int X { get; init; }
    [JsonIgnore] public int Y { get; init; }
    [JsonIgnore] public int Width { get; init; }   // current-mode pixel size (for transition frame capture)
    [JsonIgnore] public int Height { get; init; }
}

public static class MonitorDetector
{
    public static async Task<List<MonitorInfo>> DetectAsync()
    {
        // hyprctl monitors -j: [{ name, refreshRate (Hz float), focused (bool) }]
        var hypr = await TryAsync("hyprctl", "monitors -j", ParseMonitors);
        if (hypr != null) return hypr;

        // swaymsg -t get_outputs: [{ name, focused, primary, current_mode:{ refresh (mHz) } }]
        var sway = await TryAsync("swaymsg", "-t get_outputs", ParseMonitors);
        if (sway != null) return sway;

        // wlr-randr --json: any wlroots compositor (river, Wayfire, labwc, …) → [{ name, position{x,y},
        // modes:[{ refresh (Hz float), current }] }]. No "primary" → derived from layout origin.
        var wlr = await TryAsync("wlr-randr", "--json", ParseWlrRandr);
        if (wlr != null) return wlr;

        // xrandr: universal X11 + XWayland fallback (covers GNOME/KDE/XFCE/etc. on X11, and most Wayland
        // sessions via XWayland). Has a real "primary" flag. Text output, parsed below.
        var xr = await TryAsync("xrandr", "", ParseXrandr);
        if (xr != null) return xr;

        return [];
    }

    private static async Task<List<MonitorInfo>?> TryAsync(string cmd, string args, Func<string, List<MonitorInfo>?> parse)
    {
        try
        {
            var psi = new ProcessStartInfo(cmd)
            {
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string output;
            try
            {
                output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(); } catch { }
                return null;
            }
            if (proc.ExitCode != 0) return null;
            return parse(output);
        }
        catch { return null; }
    }

    private static List<MonitorInfo>? ParseMonitors(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<MonitorInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;

            int hz = 0;
            // Hyprland: "refreshRate" in Hz (float, e.g. 143.97)
            if (e.TryGetProperty("refreshRate", out var rr) && rr.ValueKind == JsonValueKind.Number)
                hz = (int)Math.Round(rr.GetDouble());
            // Sway: "current_mode": { "refresh": mHz (e.g. 144000) }
            else if (e.TryGetProperty("current_mode", out var cm) && cm.ValueKind == JsonValueKind.Object
                     && cm.TryGetProperty("refresh", out var rf) && rf.ValueKind == JsonValueKind.Number)
                hz = (int)Math.Round(rf.GetDouble() / 1000.0);
            if (hz <= 0) hz = 60; // safe fallback

            // Sway exposes a real "primary" flag; honor it. (Hyprland has none — handled below by layout.)
            bool primary = e.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;
            // layout origin (0,0) = the conventional main display; stable, unlike "focused" (cursor-dependent)
            int x = e.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number ? xe.GetInt32() : int.MaxValue;
            int y = e.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number ? ye.GetInt32() : int.MaxValue;

            // Hyprland: top-level "width"/"height" (px). Sway: "current_mode":{ width, height }.
            int w = 0, h = 0;
            if (e.TryGetProperty("width", out var we) && we.ValueKind == JsonValueKind.Number) w = we.GetInt32();
            if (e.TryGetProperty("height", out var he) && he.ValueKind == JsonValueKind.Number) h = he.GetInt32();
            if ((w == 0 || h == 0) && e.TryGetProperty("current_mode", out var cmd) && cmd.ValueKind == JsonValueKind.Object)
            {
                if (cmd.TryGetProperty("width", out var cw) && cw.ValueKind == JsonValueKind.Number) w = cw.GetInt32();
                if (cmd.TryGetProperty("height", out var ch) && ch.ValueKind == JsonValueKind.Number) h = ch.GetInt32();
            }

            list.Add(new MonitorInfo(name, hz, primary) { X = x, Y = y, Width = w, Height = h });
        }
        return ResolvePrimary(list);
    }

    // wlr-randr --json: [{ name, enabled, position:{x,y}, modes:[{ refresh (Hz float), current (bool) }] }].
    // No primary concept → derived from layout origin by ResolvePrimary.
    private static List<MonitorInfo>? ParseWlrRandr(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<MonitorInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
            if (!e.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } name) continue;
            int hz = 0, w = 0, h = 0;
            if (e.TryGetProperty("modes", out var modes) && modes.ValueKind == JsonValueKind.Array)
                foreach (var m in modes.EnumerateArray())
                    if (m.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.True)
                    {
                        if (m.TryGetProperty("refresh", out var rf) && rf.ValueKind == JsonValueKind.Number)
                            hz = (int)Math.Round(rf.GetDouble());
                        if (m.TryGetProperty("width", out var mw) && mw.ValueKind == JsonValueKind.Number) w = mw.GetInt32();
                        if (m.TryGetProperty("height", out var mh) && mh.ValueKind == JsonValueKind.Number) h = mh.GetInt32();
                        break;
                    }
            if (hz <= 0) hz = 60;
            int x = int.MaxValue, y = int.MaxValue;
            if (e.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                if (pos.TryGetProperty("x", out var xe) && xe.ValueKind == JsonValueKind.Number) x = xe.GetInt32();
                if (pos.TryGetProperty("y", out var ye) && ye.ValueKind == JsonValueKind.Number) y = ye.GetInt32();
            }
            list.Add(new MonitorInfo(name, hz, false) { X = x, Y = y, Width = w, Height = h });
        }
        return ResolvePrimary(list);
    }

    // xrandr text: a connected line "<name> connected [primary] WxH+X+Y ..." then indented mode lines;
    // the active mode is the one flagged with '*'. Universal X11/XWayland fallback (real "primary").
    private static readonly Regex _xConn = new(
        @"^(\S+) connected( primary)?(?: (\d+)x(\d+)\+(\d+)\+(\d+))?", RegexOptions.Compiled);
    private static readonly Regex _xMode = new(@"^\s+\d+x\d+\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex _xRate = new(@"([\d.]+)\*", RegexOptions.Compiled);
    private static List<MonitorInfo>? ParseXrandr(string text)
    {
        var list = new List<MonitorInfo>();
        string? name = null; bool primary = false; int x = int.MaxValue, y = int.MaxValue, hz = 0, w = 0, h = 0;
        void Flush() { if (name != null) list.Add(new MonitorInfo(name, hz > 0 ? hz : 60, primary) { X = x, Y = y, Width = w, Height = h }); }
        foreach (var line in text.Split('\n'))
        {
            var c = _xConn.Match(line);
            if (c.Success && line.Contains(" connected"))
            {
                Flush();
                name = c.Groups[1].Value; primary = c.Groups[2].Success; hz = 0;
                w = c.Groups[3].Success ? int.Parse(c.Groups[3].Value) : 0;
                h = c.Groups[4].Success ? int.Parse(c.Groups[4].Value) : 0;
                x = c.Groups[5].Success ? int.Parse(c.Groups[5].Value) : int.MaxValue;
                y = c.Groups[6].Success ? int.Parse(c.Groups[6].Value) : int.MaxValue;
                continue;
            }
            if (line.Contains(" disconnected")) { Flush(); name = null; continue; }
            // active mode line carries '*' next to the current refresh rate
            if (name != null && hz == 0)
            {
                var mm = _xMode.Match(line);
                if (mm.Success)
                {
                    var r = _xRate.Match(mm.Groups[1].Value);
                    if (r.Success && double.TryParse(r.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        hz = (int)Math.Round(d);
                }
            }
        }
        Flush();
        return ResolvePrimary(list);
    }

    // Ensure exactly one primary: keep a real flag if present, else the monitor at origin (0,0),
    // else the top-left-most. (Wayland has no primary concept — this is the conventional main display.)
    private static List<MonitorInfo>? ResolvePrimary(List<MonitorInfo> list)
    {
        if (list.Count == 0) return null;
        if (list.Count(m => m.Primary) != 1)
        {
            var main = list.FirstOrDefault(m => m.X == 0 && m.Y == 0)
                ?? list.OrderBy(m => m.Y).ThenBy(m => m.X).First();
            list = list.Select(m => m with { Primary = ReferenceEquals(m, main) }).ToList();
        }
        return list;
    }
}
