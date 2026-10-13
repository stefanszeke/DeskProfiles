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

    // Find the nearest vacant Explorer grid slot. Search by distance from the
    // desired position, so collisions never compact unrelated icon groups.
    private static (int Col, int Row) NearestFreeSlot(int col, int row, int columns, int rows,
        HashSet<(int Col, int Row)> occupied)
    {
        var best = (Col: col, Row: row);
        int bestDistance = int.MaxValue;
        for (int c = 0; c < columns; c++)
            for (int r = 0; r < rows; r++)
            {
                if (occupied.Contains((c, r))) continue;
                int distance = Math.Abs(c - col) * 2 + Math.Abs(r - row);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = (c, r);
            }
        return best;
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

        // Shell icon positions are relative to the entire Windows virtual desktop,
        // including displays ignored for profile matching.
        var (virtualLeft, virtualTop) = EligibleMonitorPlacement.VirtualOrigin();
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

            // Saved positions are virtual-desktop-local. Donor display topology
            // may not include ignored screens, so do not assume its minimum
            // eligible X/Y is the Shell origin. For older arrangements the
            // source key origin is the best available approximation.
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
        // For a single remaining monitor, transfer the *arrangement* of icons from
        // disconnected displays. Never collapse them into a new first column.
        // Exact layouts are handled by MainWindow before this inheritance path.
        if (targets.Count == 1)
        {
            var target = targets[0];
            // Choose a multi-display donor that actually contains this monitor.
            // This keeps the transferred icons and the directly inherited icons
            // sourced from the same saved arrangement wherever possible.
            var donor = donors.Where(d => d.Displays.Count > 1 &&
                                           d.Displays.Any(s => MatchScore(s, target) > 0))
                              .OrderByDescending(d => d.Displays.Count)
                              .ThenByDescending(d => d.Layout.SavedUtc)
                              .FirstOrDefault()
                        ?? donors.Where(d => d.Displays.Any(s => MatchScore(s, target) > 0))
                                 .OrderByDescending(d => d.Layout.SavedUtc).FirstOrDefault();
            if (donor != null)
            {
                // Keep the actual Explorer grid pitch. The original 86px row pitch
                // was snapped to 85px in this Windows configuration.
                const int gridX = 76, gridY = 85, marginX = 21, marginY = 2;
                int columns = Math.Max(1, (target.Width - marginX - 20) / gridX);
                int rows = Math.Max(1, (target.Height - marginY - 20) / gridY);
                int originX = target.X - virtualLeft, originY = target.Y - virtualTop;
                int sourceVirtualLeft = donor.Displays.Min(s => s.X);
                int sourceVirtualTop = donor.Displays.Min(s => s.Y);
                var occupied = new HashSet<(int Col, int Row)>();
                foreach (var placed in result.Values)
                {
                    int col = (int)Math.Round((placed.X - originX - marginX) / (double)gridX);
                    int row = (int)Math.Round((placed.Y - originY - marginY) / (double)gridY);
                    if (col >= 0 && col < columns && row >= 0 && row < rows)
                        occupied.Add((col, row));
                }

                // Use the donor's screen membership to translate each lost icon's
                // local coordinates onto the remaining display. This preserves
                // original columns, rows, and relative grouping.
                foreach (var icon in donor.Layout.Icons)
                {
                    if (result.ContainsKey(icon.Pidl) || occupied.Count >= columns * rows) continue;
                    int sx = icon.X + sourceVirtualLeft;
                    int sy = icon.Y + sourceVirtualTop;
                    var source = donor.Displays.FirstOrDefault(d =>
                        sx >= d.X && sx < d.X + d.Width && sy >= d.Y && sy < d.Y + d.Height);
                    if (source == null) continue;

                    int localX = sx - source.X;
                    int localY = sy - source.Y;
                    // Snap the transferred *row index* to the current 85px grid.
                    // Saved multi-display positions can use 86px row spacing.
                    int colWanted = Math.Clamp((int)Math.Round((localX - marginX) / (double)gridX), 0, columns - 1);
                    int rowWanted = Math.Clamp((int)Math.Round((localY - marginY) / 86.0), 0, rows - 1);
                    var slot = NearestFreeSlot(colWanted, rowWanted, columns, rows, occupied);
                    occupied.Add(slot);
                    result.Add(icon.Pidl, new IconPosition
                    {
                        Pidl = icon.Pidl,
                        X = originX + marginX + slot.Col * gridX,
                        Y = originY + marginY + slot.Row * gridY
                    });
                }
                if (result.Count > 0) used.Add(donor.Layout.MonitorKey);
            }
        }
        if (result.Count == 0) return null;
        var output = new Layout { MonitorKey = currentKey, Icons = result.Values.ToList(), SavedUtc = DateTime.UtcNow };
        return new Inherited(EligibleMonitorPlacement.Constrain(output, ignored), monitorsMatched, used.Count);
    }
}
