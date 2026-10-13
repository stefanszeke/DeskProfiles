namespace DeskProfiles;
public sealed class IconPosition { public string Pidl { get; set; } = ""; public int X { get; set; } public int Y { get; set; } }
public sealed class Layout { public string MonitorKey { get; set; } = ""; public DateTime SavedUtc { get; set; } = DateTime.UtcNow; public List<IconPosition> Icons { get; set; } = new(); }
public sealed class Profile { public string Name { get; set; } = "Personal"; public bool UseCustomWallpaper { get; set; } public string WallpaperPath { get; set; } = ""; public string WallpaperStyle { get; set; } = "Fill"; public Dictionary<string, Layout> Layouts { get; set; } = new(); public override string ToString() => Name; }
public sealed class Settings { public List<Profile> Profiles { get; set; } = new() { new Profile() }; public string ActiveProfile { get; set; } = "Personal"; public bool AutoRestore { get; set; } public List<string> IgnoredMonitors { get; set; } = new(); }
