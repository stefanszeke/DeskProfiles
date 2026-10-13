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

    // Experimental folder-backed visibility: keep each enrolled shortcut in a private
    // holding folder when hidden. No executable target or ordinary desktop file moves.
    // The original immutable enrollment backup remains separate for recovery.
    public static string HoldingFolder => Path.Combine(Store.DirectoryPath, "profile-shortcut-holding");
    static string HoldingPath(ManagedShortcut item) => Path.Combine(HoldingFolder, item.Id + Path.GetExtension(item.DesktopFileName));

    public static List<string> Reconcile(Settings settings, Profile active)
    {
        var messages = new List<string>();
        var items = settings.ManagedShortcuts.Where(i => i.IsManaged).ToList();
        if (settings.UseFolderStaging) Directory.CreateDirectory(HoldingFolder);
        // Hide before showing to reduce temporary collisions in Explorer's view.
        foreach (var item in items.OrderBy(i => (i.Shared || active.ShortcutIds.Contains(i.Id)) ? 1 : 0))
        {
            try
            {
                var path = Path.Combine(UserDesktop, item.DesktopFileName);
                var archive = item.ArchivedOriginal;
                if (string.IsNullOrWhiteSpace(item.DesktopFileName) || !IsShortcut(path) || !File.Exists(archive))
                { messages.Add($"Skipped {item.Name}: managed shortcut has no valid original archive."); continue; }
                bool visible = item.Shared || active.ShortcutIds.Contains(item.Id);
                if (!settings.UseFolderStaging)
                {
                    // Original tested implementation, retained as an immediate rollback.
                    if (!visible)
                    {
                        if (!File.Exists(path)) continue;
                        if (!SameBytes(path, archive)) { messages.Add($"NOT hidden: {item.DesktopFileName} differs from its backup; manual review required."); continue; }
                        File.Delete(path);
                        messages.Add($"Hidden {item.DesktopFileName}");
                    }
                    else if (!File.Exists(path))
                    {
                        File.Copy(archive, path, false);
                        messages.Add($"Shown {item.DesktopFileName}");
                    }
                    else if (!SameBytes(path, archive))
                        messages.Add($"Collision: {item.DesktopFileName} already exists and differs; left unchanged.");
                    continue;
                }

                string holding = HoldingPath(item);
                if (!visible)
                {
                    if (!File.Exists(path)) continue;
                    if (!SameBytes(path, archive)) { messages.Add($"NOT staged: {item.DesktopFileName} differs from backup; manual review required."); continue; }
                    if (File.Exists(holding))
                    {
                        if (!SameBytes(holding, archive)) { messages.Add($"Holding collision for {item.DesktopFileName}; left unchanged."); continue; }
                        // Crash/restart recovery: both copies are identical, so retain the staged copy.
                        File.Delete(path);
                    }
                    else File.Move(path, holding);
                    messages.Add($"Staged {item.DesktopFileName}");
                }
                else if (!File.Exists(path))
                {
                    if (File.Exists(holding))
                    {
                        if (!SameBytes(holding, archive)) { messages.Add($"Holding copy for {item.DesktopFileName} differs from backup; not restored."); continue; }
                        File.Move(holding, path);
                    }
                    else File.Copy(archive, path, false); // First activation / migration.
                    messages.Add($"Shown {item.DesktopFileName}");
                }
                else if (!SameBytes(path, archive))
                    messages.Add($"Collision: {item.DesktopFileName} already exists and differs; left unchanged.");
                else if (File.Exists(holding) && SameBytes(holding, archive))
                    File.Delete(holding); // Clean a duplicate after interrupted recovery.
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
        string holding = HoldingPath(item);
        if (!File.Exists(path))
        {
            if (File.Exists(holding) && SameBytes(holding, item.ArchivedOriginal)) File.Move(holding, path);
            else File.Copy(item.ArchivedOriginal, path, false);
        }
        if (File.Exists(holding))
        {
            if (!SameBytes(holding, item.ArchivedOriginal))
                throw new IOException("Staged shortcut differs from its backup; manual recovery required.");
            File.Delete(holding);
        }
        item.IsManaged = false;
        Store.Write(settings);
    }
}
