using System.Windows.Forms;
namespace DeskProfiles;
public static class MonitorIdentity
{
    // V1 heuristic: Includes names, desktop coordinates, dimensions and primary status.
    // This deliberately treats changes in resolution or monitor ordering as new setups.
    public static string Key() => string.Join("|", Screen.AllScreens
        .Select(s => $"{s.DeviceName}:{s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width},{s.Bounds.Height}:{s.Primary}")
        .OrderBy(s => s, StringComparer.Ordinal));
    public static string Description() => string.Join("  /  ", Screen.AllScreens.Select(s =>
        $"{s.DeviceName} {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (primary)" : "")));
}
