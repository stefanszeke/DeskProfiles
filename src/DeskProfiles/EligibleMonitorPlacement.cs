using Forms = System.Windows.Forms;

namespace DeskProfiles;

// Windows Explorer positions icons relative to the entire virtual desktop, including
// displays excluded from DeskProfiles' monitor-layout matching.
internal static class EligibleMonitorPlacement
{
    internal static (int Left, int Top) VirtualOrigin()
    {
        var screens = Forms.Screen.AllScreens;
        return screens.Length == 0 ? (0, 0) : (screens.Min(s => s.Bounds.Left), screens.Min(s => s.Bounds.Top));
    }

    internal static Layout Constrain(Layout source, IEnumerable<string> ignored)
    {
        var excluded = ignored.ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Validate even when nothing is ignored: stale exact layouts can contain
        // coordinates from a previous virtual-desktop origin.
        var eligible = Forms.Screen.AllScreens.Where(s => !excluded.Contains(s.DeviceName)).ToArray();
        if (eligible.Length == 0) return source; // Never move icons if no eligible display is known.
        var (left, top) = VirtualOrigin();
        var primary = eligible.FirstOrDefault(s => s.Primary) ?? eligible[0];
        var positions = new List<IconPosition>(source.Icons.Count);
        var displaced = new List<IconPosition>();
        bool valid(IconPosition icon) => eligible.Any(s => s.Bounds.Contains(icon.X + left, icon.Y + top));
        foreach (var icon in source.Icons)
        {
            if (valid(icon))
            {
                positions.Add(new IconPosition { Pidl = icon.Pidl, X = icon.X, Y = icon.Y });
            }
            else displaced.Add(icon);
        }
        if (displaced.Count == 0) return source;

        // Preserve valid positions; relocate only icons on disconnected or ignored displays.
        // Reserve grid cells starting at the upper left of the eligible primary monitor.
        // Match the observed Explorer grid: 85px between vertical slots.
        // The previous 86px caused each lower icon to drift another pixel.
        const int stepX = 76, stepY = 85, insetX = 21, insetY = 2;
        var rect = primary.Bounds;
        int cols = Math.Max(1, (rect.Width - insetX - 20) / stepX);
        int rows = Math.Max(1, (rect.Height - insetY - 20) / stepY);
        var taken = new HashSet<(int Col, int Row)>();
        foreach (var icon in positions)
        {
            int px = icon.X + left, py = icon.Y + top;
            if (!rect.Contains(px, py)) continue;
            taken.Add((Math.Clamp((px - rect.Left - insetX + stepX / 2) / stepX, 0, cols - 1),
                       Math.Clamp((py - rect.Top - insetY + stepY / 2) / stepY, 0, rows - 1)));
        }
        foreach (var icon in displaced)
        {
            var slot = Enumerable.Range(0, cols * rows)
                .Select(n => (Col: n / rows, Row: n % rows))
                .FirstOrDefault(cell => !taken.Contains(cell));
            // Avoid overriding occupied coordinates if the grid is completely full.
            if (taken.Count >= cols * rows) continue;
            taken.Add(slot);
            positions.Add(new IconPosition { Pidl = icon.Pidl,
                X = rect.Left + insetX + slot.Col * stepX - left,
                Y = rect.Top + insetY + slot.Row * stepY - top });
        }
        return new Layout { MonitorKey = source.MonitorKey, SavedUtc = source.SavedUtc,
            Icons = positions };
    }
}
