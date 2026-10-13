namespace DeskProfiles;
public sealed class IconPosition { public string Pidl { get; set; } = ""; public int X { get; set; } public int Y { get; set; } }
public sealed class Layout { public string MonitorKey { get; set; } = ""; public DateTime SavedUtc { get; set; } = DateTime.UtcNow; public List<IconPosition> Icons { get; set; } = new(); }
public sealed class Profile { public bool IsOriginalDesktop { get; set; } public string Name { get; set; } = "Personal"; public bool UseCustomWallpaper { get; set; } public string WallpaperPath { get; set; } = ""; public string WallpaperStyle { get; set; } = "Fill"; public string Hotkey { get; set; } = ""; public Dictionary<string, Layout> Layouts { get; set; } = new(); public Dictionary<string, string> LayoutNames { get; set; } = new(); public List<string> ShortcutIds { get; set; } = new(); public List<string> FolderIds { get; set; } = new(); public override string ToString() => Name; }
public sealed class Settings { public List<Profile> Profiles { get; set; } = new() { new Profile() }; public string ActiveProfile { get; set; } = "Personal"; public bool AutoRestore { get; set; } public List<string> IgnoredMonitors { get; set; } = new(); public List<ManagedShortcut> ManagedShortcuts { get; set; } = new(); public List<ManagedFolder> ManagedFolders { get; set; } = new(); public bool EnableShortcutVisibilitySwitching { get; set; } = true; public bool AutoManageNewShortcuts { get; set; } public bool UseFolderStaging { get; set; } public bool UseWholeDesktopFolders { get; set; } = true; }

public sealed class ManagedShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string CatalogFileName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public bool Shared { get; set; }
    public bool IsManaged { get; set; }
    public string DesktopFileName { get; set; } = "";
    public string ArchivedOriginal { get; set; } = "";
}

// Original folder is NEVER moved or recursively deleted. Profile desktops contain junctions only.
public sealed class ManagedFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public bool Shared { get; set; } = true;
}
