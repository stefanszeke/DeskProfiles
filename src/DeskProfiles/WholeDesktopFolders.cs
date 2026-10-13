using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using System.Text;

namespace DeskProfiles;

// Experimental, opt-in Windows Desktop known-folder redirect. The original folder is not moved.
// The marker is persisted BEFORE any redirect so an interrupted switch can be rolled back.
internal static class WholeDesktopFolders
{
    private static readonly Guid DesktopId = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    private static readonly string Root = Path.Combine(Store.DirectoryPath, "whole-desktop-experiment");
    private static readonly string StateFile = Path.Combine(Root, "recovery.json");
    private sealed class State { public string OriginalDesktop { get; set; } = ""; public string LastTarget { get; set; } = ""; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHSetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, [MarshalAs(UnmanagedType.LPWStr)] string? path, IntPtr unused);
    private const int UpdateDir = 0x00001000, AssocChanged = 0x08000000;
    private const uint PathWFlush = 0x1005, IdListFlush = 0x1000;

    private static string GetPath()
    {
        var id = DesktopId;
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var ptr));
        try { return Marshal.PtrToStringUni(ptr) ?? throw new IOException("Desktop folder path is empty."); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }
    private static void SetPath(string path)
    {
        var id = DesktopId;
        Marshal.ThrowExceptionForHR(SHSetKnownFolderPath(ref id, 0, IntPtr.Zero, path));
        if (!Same(GetPath(), path)) throw new IOException("Windows did not accept the Desktop path: " + path);
    }
    private static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static bool OurTarget(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Root).TrimEnd('\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string ProfilePath(Profile profile) => Path.Combine(Root, "profiles", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(profile.Name))).Substring(0, 20));
    private static State? Read() => File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) : null;
    private static void Save(State value)
    {
        Directory.CreateDirectory(Root);
        var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value));
        File.Move(tmp, StateFile, true);
    }
    private static void Notify(string current, string previous)
    {
        foreach (var path in new[] { current, previous }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(path)) SHChangeNotify(UpdateDir, PathWFlush, path, IntPtr.Zero);
        SHChangeNotify(AssocChanged, IdListFlush, null, IntPtr.Zero);
    }

    public static bool IsRedirected => OurTarget(GetPath());

    // Always use the ORIGINAL location for enrollment, even while Windows Desktop is redirected.
    public static string OriginalDesktopPath => Read()?.OriginalDesktop ?? GetPath();

    // A folder can be enrolled only if it is an immediate child of the original Desktop.
    public static bool IsSafeOriginalFolder(string path, string originalDesktop)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (!Same(Path.GetDirectoryName(full) ?? "", originalDesktop) || !Directory.Exists(full)) return false;
            var info = new DirectoryInfo(full);
            return (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch { return false; }
    }

    private static void CreateFolderJunction(string junctionPath, string originalPath)
    {
        // PowerShell New-Item -ItemType Junction uses Windows junctions, not privileged symbolic links.
        // Base64-encode script to avoid command injection through file names.
        string script = "$ErrorActionPreference='Stop'; " +
            "New-Item -ItemType Junction -Path " + PsQuote(junctionPath) +
            " -Target " + PsQuote(originalPath) + " | Out-Null";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start) ?? throw new IOException("Could not start Windows PowerShell to create a junction.");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("Failed to link folder '" + Path.GetFileName(originalPath) + "': " + error);
        if (!JunctionTargets(junctionPath, originalPath))
            throw new IOException("Created folder link did not point to the original folder: " + junctionPath);
    }
    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool JunctionTargets(string candidate, string expectedPath)
    {
        try
        {
            var dir = new DirectoryInfo(candidate);
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) == 0 || string.IsNullOrEmpty(dir.LinkTarget)) return false;
            var linkTarget = dir.LinkTarget!;
            if (!Path.IsPathFullyQualified(linkTarget)) linkTarget = Path.GetFullPath(Path.Combine(dir.Parent!.FullName, linkTarget));
            return Same(linkTarget.Replace(@"\??\", "").Replace(@"\\?\", ""), expectedPath);
        }
        catch { return false; }
    }


    // Automatic repair runs before any watcher, Shell capture or reconciliation is started.
    public static string RecoverAtStartup()
    {
        var state = Read();
        if (state == null) return "";
        Restore();
        return "Restored original Windows Desktop after a previous interrupted folder experiment.";
    }

    // Navigate to the original Desktop without ending this experiment. The recovery marker
    // remains valid so later profile switches can redirect again and exit can always restore.
    public static void SwitchToOriginal()
    {
        var state = Read() ?? throw new IOException("Original Desktop recovery record is unavailable. Enable the experiment first.");
        if (string.IsNullOrWhiteSpace(state.OriginalDesktop) || !Directory.Exists(state.OriginalDesktop))
            throw new IOException("Original Desktop folder cannot be found: " + state.OriginalDesktop);
        var current = GetPath();
        if (!OurTarget(current) && !Same(current, state.OriginalDesktop))
            throw new IOException("Desktop location was changed externally: " + current);
        if (!Same(current, state.OriginalDesktop))
        {
            SetPath(state.OriginalDesktop);
            state.LastTarget = state.OriginalDesktop;
            Save(state);
            Notify(state.OriginalDesktop, current);
        }
    }

    // Safety: construct the complete target before changing the OS folder.
    public static IReadOnlyList<string> Switch(Settings settings, Profile profile)
    {
        if (profile.IsOriginalDesktop)
        {
            SwitchToOriginal();
            return Array.Empty<string>();
        }
        var target = ProfilePath(profile);
        var current = GetPath();
        var state = Read();
        if (state == null)
        {
            if (OurTarget(current)) throw new IOException("Desktop already points into the experimental folder but recovery state is missing. No redirect performed.");
            if (current.Contains("OneDrive", StringComparison.OrdinalIgnoreCase))
                throw new IOException("OneDrive-managed Desktop detected. This experiment is blocked to avoid sync/path conflicts.");
            state = new State { OriginalDesktop = current };
            Save(state);
        }
        else if (!Same(current, state.OriginalDesktop) && !OurTarget(current))
            throw new IOException("Another application changed the Windows Desktop path: " + current);

        Directory.CreateDirectory(target);
        var owned = settings.ManagedShortcuts.Where(x => x.IsManaged).ToList();
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in owned.Where(x => x.Shared || profile.ShortcutIds.Contains(x.Id)))
        {
            var name = item.DesktopFileName;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                !(name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(item.ArchivedOriginal))
                throw new IOException("Managed shortcut backup is invalid: " + item.Name);
            if (!expected.Add(name)) throw new IOException("Duplicate desktop filename: " + name);
            var path = Path.Combine(target, name);
            if (File.Exists(path) && !File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(item.ArchivedOriginal)))
                throw new IOException("Edited shortcut in experimental folder: " + path + ". Refusing to overwrite it.");
            if (!File.Exists(path)) File.Copy(item.ArchivedOriginal, path, false);
        }
        // Retired feature: remove ONLY previously enrolled, verified junction links.
        // This does not touch the original directories or links belonging to other tools.
        foreach (var folder in settings.ManagedFolders)
        {
            var link = Path.Combine(target, folder.Name);
            if (JunctionTargets(link, folder.OriginalPath))
            {
                Directory.Delete(link, recursive: false);
                Diagnostics.Info("[WHOLE DESKTOP] Retired verified shared-folder junction: " + link);
            }
        }
        // Remove stale *owned* shortcut COPIES from previous profiles only when they
        // byte-match our archived original. Never delete unknown or edited files.
        foreach (var item in owned.Where(i => !expected.Contains(i.DesktopFileName)))
        {
            var name = item.DesktopFileName;
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name)) continue;
            var stale = Path.Combine(target, name);
            if (File.Exists(stale) && !Directory.Exists(stale) &&
                File.Exists(item.ArchivedOriginal) &&
                File.ReadAllBytes(stale).SequenceEqual(File.ReadAllBytes(item.ArchivedOriginal)))
            {
                File.Delete(stale);
                Diagnostics.Info("[WHOLE DESKTOP] Removed archived-match stale shortcut copy: " + stale);
            }
        }
        // Unrecognized real files/folders are NEVER deleted or overwritten.
        var unexpected = Directory.EnumerateFileSystemEntries(target).Where(p => !expected.Contains(Path.GetFileName(p))).ToList();
        // Previous experimental versions may have left shortcuts in the profile folder.
        // An unknown entry is not ours to remove, overwrite, or move. Keep it in place
        // and report it to the caller instead of making the entire desktop unavailable.
        // Known destination collisions above are still rejected.
        var retained = unexpected.Select(Path.GetFileName).Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).ToList();

        if (!Same(current, target))
        {
            SetPath(target);
            state.LastTarget = target;
            Save(state);
            Notify(target, current);
        }
        return retained;
    }

    public static void Restore()
    {
        var state = Read();
        if (state == null)
        {
            if (OurTarget(GetPath())) throw new IOException("Desktop is redirected but recovery file is missing. Do not continue; restore Desktop path manually.");
            return;
        }
        if (string.IsNullOrWhiteSpace(state.OriginalDesktop) || !Directory.Exists(state.OriginalDesktop))
            throw new IOException("Original Windows Desktop folder cannot be found: " + state.OriginalDesktop);
        var current = GetPath();
        if (!OurTarget(current) && !Same(current, state.OriginalDesktop))
            throw new IOException("Desktop path changed externally; refusing to overwrite: " + current);
        if (!Same(current, state.OriginalDesktop)) SetPath(state.OriginalDesktop);
        Notify(state.OriginalDesktop, current);
        // Remove state ONLY after verifying the original path is restored.
        if (!Same(GetPath(), state.OriginalDesktop)) throw new IOException("Desktop restoration failed; recovery state retained.");
        File.Delete(StateFile);
    }
}
