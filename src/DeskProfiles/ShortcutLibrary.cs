using System.IO;
using System.Runtime.InteropServices;

namespace DeskProfiles;

// Stage 1: Library operations ONLY. Never write, move or delete Desktop items.
public static class ShortcutLibrary
{
    public static string Folder => Path.Combine(Store.DirectoryPath, "shortcuts");
    public static string PathFor(ManagedShortcut item) => Path.Combine(Folder, item.CatalogFileName);

    public static ManagedShortcut Import(Settings settings, string source)
    {
        string extension = Path.GetExtension(source).ToLowerInvariant();
        if (extension != ".lnk" && extension != ".url")
            throw new InvalidOperationException("Choose a Windows .lnk or .url shortcut.");
        if (!File.Exists(source)) throw new FileNotFoundException("Shortcut not found.", source);
        var item = new ManagedShortcut
        {
            Name = Path.GetFileNameWithoutExtension(source),
            CatalogFileName = Guid.NewGuid().ToString("N") + extension,
            SourcePath = Path.GetFullPath(source)
        };
        Directory.CreateDirectory(Folder);
        File.Copy(source, PathFor(item), overwrite: false);
        settings.ManagedShortcuts.Add(item);
        return item;
    }

    // Discovery is read-only. User Desktop may be redirected to OneDrive.
    public static IReadOnlyList<string> ScanDesktop()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        var results = new List<string>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) continue;
            foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(path);
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) results.Add(path);
            }
        }
        return results.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool AlreadyImported(Settings settings, string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (settings.ManagedShortcuts.Any(x => !string.IsNullOrWhiteSpace(x.SourcePath) &&
            string.Equals(Path.GetFullPath(x.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase))) return true;
        // Older library items did not record their origin: compare their bytes.
        // This avoids repeatedly importing the same existing Desktop shortcut.
        var info = new FileInfo(path);
        foreach (var item in settings.ManagedShortcuts)
        {
            var cached = PathFor(item);
            if (!File.Exists(cached) || new FileInfo(cached).Length != info.Length) continue;
            using var a = File.OpenRead(path);
            using var b = File.OpenRead(cached);
            if (System.Security.Cryptography.SHA256.HashData(a)
                .SequenceEqual(System.Security.Cryptography.SHA256.HashData(b))) return true;
        }
        return false;
    }

    public static ManagedShortcut AddFolder(Settings settings, string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var item = new ManagedShortcut
        {
            Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path)),
            CatalogFileName = Guid.NewGuid().ToString("N") + ".lnk"
        };
        Directory.CreateDirectory(Folder);
        // Windows Script Host creates a shortcut in our private library, never on Desktop.
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host unavailable.");
        object shell = Activator.CreateInstance(type)!;
        object? link = null;
        try
        {
            dynamic ws = shell;
            link = ws.CreateShortcut(PathFor(item));
            dynamic shortcut = link;
            shortcut.TargetPath = path;
            shortcut.WorkingDirectory = path;
            shortcut.Save();
        }
        finally
        {
            if (link != null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        settings.ManagedShortcuts.Add(item);
        return item;
    }
}
