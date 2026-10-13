using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DeskProfiles;

public static class MonitorIdentity
{
    // Windows may reassign \.\DISPLAY1 and \.\DISPLAY2 when switching projection modes.
    // Use geometry and primary flag rather than those transient enumeration names.
    // Different resolutions/positions/primary assignments intentionally have different layouts.
    private static string ScreenPart(Screen screen) =>
        $"{screen.Bounds.X},{screen.Bounds.Y},{screen.Bounds.Width},{screen.Bounds.Height},{screen.Primary}";

    public static string Key(IEnumerable<string>? ignored = null)
    {
        var excluded = new HashSet<string>(ignored ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return "v2|" + string.Join("|", Screen.AllScreens.Where(s => !excluded.Contains(s.DeviceName))
            .Select(ScreenPart).OrderBy(p => p, StringComparer.Ordinal));
    }

    // Legacy layouts include all monitors. In the UI we can match them against the
    // current filtered topology, without modifying or deleting saved layouts.
    public static string MatchKey(string savedKey, IEnumerable<string> ignored)
    {
        var normalized = NormalizeKey(savedKey);
        var excluded = new HashSet<string>(ignored, StringComparer.OrdinalIgnoreCase);
        if (excluded.Count == 0 || !normalized.StartsWith("v2|", StringComparison.Ordinal)) return normalized;
        var knownIgnoredParts = Screen.AllScreens.Where(s => excluded.Contains(s.DeviceName))
            .Select(ScreenPart).ToHashSet(StringComparer.Ordinal);
        var parts = normalized.Split('|').Skip(1)
            .Where(part => !knownIgnoredParts.Contains(part)).OrderBy(part => part, StringComparer.Ordinal);
        return "v2|" + string.Join("|", parts);
    }

    public static IReadOnlyList<Screen> AllDisplays() => Screen.AllScreens
        .OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();

    // Read keys from v0.1-v0.2.2: \.\DISPLAY1:0,0,1920,1080:True|...
    // Parsing from the right avoids depending on the device name.
    public static string NormalizeKey(string key)
    {
        if (key.StartsWith("v2|", StringComparison.Ordinal)) return key;
        var parts = new List<string>();
        foreach (string segment in key.Split('|'))
        {
            var match = Regex.Match(segment,
                @":(-?\d+),(-?\d+),(\d+),(\d+):(True|False)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return key; // Unknown keys must not be discarded.
            parts.Add($"{match.Groups[1].Value},{match.Groups[2].Value},{match.Groups[3].Value},{match.Groups[4].Value},{bool.Parse(match.Groups[5].Value)}");
        }
        return "v2|" + string.Join("|", parts.OrderBy(p => p, StringComparer.Ordinal));
    }

    public static string Description(IEnumerable<string>? ignored = null) => string.Join("  /  ", Screen.AllScreens
        .Where(s => !(ignored ?? Array.Empty<string>()).Contains(s.DeviceName, StringComparer.OrdinalIgnoreCase))
        .OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y)
        .Select(s => $"{s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (PRIMARY)" : " (secondary)")));

    public static string DescribeKey(string key)
    {
        var normalized = NormalizeKey(key);
        if (!normalized.StartsWith("v2|", StringComparison.Ordinal)) return "Unknown / legacy display setup";
        var parts = normalized.Split('|').Skip(1).ToArray();
        var displays = new List<string>();
        foreach (var part in parts)
        {
            var values = part.Split(',');
            if (values.Length != 5 || !int.TryParse(values[2], out int width) ||
                !int.TryParse(values[3], out int height) || !bool.TryParse(values[4], out bool primary))
                return "Unknown display setup";
            displays.Add($"{width}×{height}" + (primary ? " (PRIMARY)" : " (secondary)"));
        }
        return (displays.Count > 1 ? $"Extended · {displays.Count} monitors · " : "Single monitor · ")
            + string.Join(" + ", displays);
    }
}
