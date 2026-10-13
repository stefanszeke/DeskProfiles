using System.Globalization;

namespace DeskProfiles;

// Fallback only. Existing setup-specific layouts always take precedence.
// The v2 layout key identifies display geometry, not physical monitor serial numbers.
internal static class SmartMonitorLayouts
{
    internal sealed record Display(int X, int Y, int Width, int Height, bool Primary);
    internal sealed record Inherited(Layout Layout, int MatchedMonitors, int SourcesUsed);

    private static List<Display> Parse(string key)
    {
        var normalized = MonitorIdentity.NormalizeKey(key);
        if (!normalized.StartsWith("v2|", StringComparison.Ordinal)) return new();
        var result = new List<Display>();
        foreach (var part in normalized.Split('|').Skip(1))
        {
            var v = part.Split(',');
            if (v.Length != 5 || !int.TryParse(v[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x)
                || !int.TryParse(v[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y)
                || !int.TryParse(v[2], out var w) || !int.TryParse(v[3], out var h)
                || !bool.TryParse(v[4], out var primary) || w <= 0 || h <= 0) return new();
            result.Add(new Display(x, y, w, h, primary));
        }
        return result;
    }

    // Never guess between multiple indistinguishable monitors. Resolution and primary
    // designation distinguish most common A versus A+B configurations.
    private static int MatchScore(Display from, Display to)
    {
        if (from.Width != to.Width || from.Height != to.Height) return -1;
        return from.Primary == to.Primary ? 2 : 1;
    }

    internal static Inherited? TryCreate(Profile profile, string currentKey, IEnumerable<string> ignored)
    {
        var targets = Parse(currentKey);
        if (targets.Count == 0) return null;
        var donors = profile.Layouts.Values.Select(layout => new
        {
            Layout = layout,
            Displays = Parse(MonitorIdentity.MatchKey(layout.MonitorKey, ignored))
        }).Where(x => x.Displays.Count > 0 && x.Layout.Icons.Count > 0).ToList();
        if (donors.Count == 0) return null;

        var virtualLeft = targets.Min(s => s.X);
        var virtualTop = targets.Min(s => s.Y);
        var result = new Dictionary<string, IconPosition>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.Ordinal);
        int monitorsMatched = 0;

        foreach (var target in targets)
        {
            var matches = donors.SelectMany(d => d.Displays.Select(source => new
            {
                d.Layout, Source = source, Score = MatchScore(source, target), d.Displays
            })).Where(m => m.Score > 0)
              .OrderByDescending(m => m.Score)
              .ThenByDescending(m => m.Layout.SavedUtc).ToList();
            if (matches.Count == 0) continue;
            var winner = matches[0];
            // If two displays in the same donor are indistinguishable, we cannot tell
            // which physical display is the intended match; leave positions untouched.
            if (matches.Count > 1 && matches[1].Score == winner.Score
                && ReferenceEquals(matches[1].Layout, winner.Layout)
                && matches[1].Source != winner.Source) continue;

            int sourceVirtualLeft = winner.Displays.Min(s => s.X);
            int sourceVirtualTop = winner.Displays.Min(s => s.Y);
            int mapped = 0;
            foreach (var icon in winner.Layout.Icons)
            {
                int sourceDesktopX = icon.X + sourceVirtualLeft;
                int sourceDesktopY = icon.Y + sourceVirtualTop;
                if (sourceDesktopX < winner.Source.X || sourceDesktopX >= winner.Source.X + winner.Source.Width
                    || sourceDesktopY < winner.Source.Y || sourceDesktopY >= winner.Source.Y + winner.Source.Height) continue;

                // Explorer stores icon grid positions in virtual-desktop-local coordinates.
                // Translate between the source and current virtual-desktop origins.
                int x = target.X + (sourceDesktopX - winner.Source.X) - virtualLeft;
                int y = target.Y + (sourceDesktopY - winner.Source.Y) - virtualTop;
                if (result.TryAdd(icon.Pidl, new IconPosition { Pidl = icon.Pidl, X = x, Y = y })) mapped++;
            }
            if (mapped > 0)
            {
                monitorsMatched++;
                used.Add(winner.Layout.MonitorKey);
            }
        }
        // Single-display fallback: icons whose original display is no longer present
        // should still be accessible. Place missing icons into free grid cells on the
        // remaining display, *without saving* these temporary positions.
        if (targets.Count == 1)
        {
            var target = targets[0];
            // Prefer a previous multi-monitor layout, which is most likely to contain
            // icons from BOTH displays. Choose newest among the richest layouts.
            var donor = donors.OrderByDescending(d => d.Displays.Count)
                              .ThenByDescending(d => d.Layout.Icons.Count)
                              .ThenByDescending(d => d.Layout.SavedUtc).First();
            var allIcons = donor.Layout.Icons;
            // Fixed desktop grid dimensions are approximate. Use actual saved target
            // positions where available; reserve a free grid cell for each fallback.
            const int gridX = 76, gridY = 86;
            const int marginX = 21, marginY = 2;
            int columns = Math.Max(1, (target.Width - marginX - 20) / gridX);
            int rows = Math.Max(1, (target.Height - marginY - 20) / gridY);
            var occupied = new HashSet<(int, int)>();
            foreach (var placed in result.Values)
                occupied.Add((Math.Clamp((placed.X - marginX + gridX / 2) / gridX, 0, columns - 1),
                              Math.Clamp((placed.Y - marginY + gridY / 2) / gridY, 0, rows - 1)));
            foreach (var icon in allIcons)
            {
                if (result.ContainsKey(icon.Pidl)) continue;
                // Temporary fallback: fill Windows-style desktop grid from the top-left,
                // going DOWN each column before advancing right. Preserve positions
                // already inherited directly from the available display.
                // Do not translate donor coordinates for disconnected monitors.
                if (occupied.Count >= columns * rows) break;
                var slot = Enumerable.Range(0, columns * rows)
                    .Select(index => (Col: index / rows, Row: index % rows))
                    .First(cell => !occupied.Contains((cell.Col, cell.Row)));
                occupied.Add((slot.Col, slot.Row));
                result[icon.Pidl] = new IconPosition { Pidl = icon.Pidl,
                    X = marginX + slot.Col * gridX, Y = marginY + slot.Row * gridY };
            }
            if (result.Count > 0) used.Add(donor.Layout.MonitorKey);
        }
        if (result.Count == 0) return null;
        return new Inherited(new Layout { MonitorKey = currentKey, Icons = result.Values.ToList(), SavedUtc = DateTime.UtcNow },
            monitorsMatched, used.Count);
    }
}
