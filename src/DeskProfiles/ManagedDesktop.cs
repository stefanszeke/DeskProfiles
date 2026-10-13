using System.IO;
using System.Security.Cryptography;

namespace DeskProfiles;

// A managed item is opt-in. Original shortcut is archived before any desktop changes.
// Never touch normal files, Public Desktop items, or unowned shortcuts.
public static class ManagedDesktop
{
    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string ArchiveFolder => Path.Combine(Store.DirectoryPath, "shortcut-originals");
    static bool IsShortcut(string path) => new[] { ".lnk", ".url" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    static bool OnUserDesktop(string path) => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path))?.TrimEnd(Path.DirectorySeparatorChar), UserDesktop.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    static bool SameBytes(string a, string b) => File.Exists(a) && File.Exists(b) && SHA256.HashData(File.ReadAllBytes(a)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(b)));

    public static void Enroll(Settings settings, ManagedShortcut item, string source)
    {
        if (item.IsManaged) return;
        if (!File.Exists(source) || !IsShortcut(source) || !OnUserDesktop(source))
            throw new InvalidOperationException("Only .lnk/.url files on your own Desktop can be enrolled. Public Desktop is not supported.");
        string fileName = Path.GetFileName(source);
        if (settings.ManagedShortcuts.Any(x => x.IsManaged && x.Id != item.Id && string.Equals(x.DesktopFileName, fileName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Another managed shortcut already uses this desktop filename.");
        Directory.CreateDirectory(ArchiveFolder);
        var archive = Path.Combine(ArchiveFolder, item.Id + Path.GetExtension(fileName));
        // Archive the existing shortcut first. No real target files or folders are moved.
        if (!File.Exists(archive)) File.Copy(source, archive, false);
        if (!SameBytes(source, archive)) throw new IOException("Original backup does not match desktop shortcut; nothing changed.");
        item.DesktopFileName = fileName;
        item.ArchivedOriginal = archive;
        item.IsManaged = true;
        // Persist ownership BEFORE any removal; a crash cannot orphan an untracked file.
        try { Store.Write(settings); }
        catch { item.IsManaged = false; item.DesktopFileName = ""; item.ArchivedOriginal = ""; throw; }
    }

    public static List<string> Reconcile(Settings settings, Profile active)
    {
        var messages = new List<string>();
        var items = settings.ManagedShortcuts.Where(i => i.IsManaged).ToList();
        // First hide items not belonging to the active profile, then create desired copies.
        foreach (var item in items.OrderBy(i => (i.Shared || active.ShortcutIds.Contains(i.Id)) ? 1 : 0))
        {
            try
            {
                var path = Path.Combine(UserDesktop, item.DesktopFileName);
                var archive = item.ArchivedOriginal;
                if (string.IsNullOrWhiteSpace(item.DesktopFileName) || !IsShortcut(path) || !File.Exists(archive))
                { messages.Add($"Skipped {item.Name}: managed shortcut has no valid original archive."); continue; }
                bool visible = item.Shared || active.ShortcutIds.Contains(item.Id);
                if (!visible)
                {
                    if (!File.Exists(path)) continue;
                    if (!SameBytes(path, archive)) { messages.Add($"NOT hidden: {item.DesktopFileName} differs from its backup; manual review required."); continue; }
                    File.Delete(path); // Only the explicitly enrolled shortcut whose contents still match its backup.
                    messages.Add($"Hidden {item.DesktopFileName}");
                }
                else if (!File.Exists(path))
                {
                    File.Copy(archive, path, false);
                    messages.Add($"Shown {item.DesktopFileName}");
                }
                else if (!SameBytes(path, archive))
                    messages.Add($"Collision: {item.DesktopFileName} already exists and differs; left unchanged.");
            }
            catch (Exception ex) { messages.Add($"Could not update {item.Name}: {ex.Message}"); }
        }
        return messages;
    }

    public static void Release(Settings settings, ManagedShortcut item)
    {
        // Releasing ownership always restores the shortcut to the user's Desktop if absent;
        // if a different file exists, leave it and keep ownership for manual recovery.
        if (!item.IsManaged) return;
        string path = Path.Combine(UserDesktop, item.DesktopFileName);
        if (File.Exists(path) && !SameBytes(path, item.ArchivedOriginal))
            throw new IOException("The existing Desktop file differs from its backup; cannot safely release ownership.");
        if (!File.Exists(path)) File.Copy(item.ArchivedOriginal, path, false);
        item.IsManaged = false;
        Store.Write(settings);
    }
}
