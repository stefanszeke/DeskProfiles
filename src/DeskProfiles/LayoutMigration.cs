using System.IO;
using System.Text.Json;

namespace DeskProfiles;

public static class LayoutMigration
{
    public static int Migrate(Settings settings)
    {
        var groups = settings.Profiles.SelectMany(p => p.Layouts.Keys.Select(k => (Profile: p, Key: k)))
            .Where(x => MonitorIdentity.NormalizeKey(x.Key) != x.Key).ToList();
        if (groups.Count == 0) return 0;

        // Backup FIRST. No existing layout is deleted even if two old keys collapse
        // into the same canonical key. The full original JSON can be recovered.
        Directory.CreateDirectory(Store.DirectoryPath);
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
        string backupDir = Path.Combine(Store.DirectoryPath, "migration-backups", stamp);
        Directory.CreateDirectory(backupDir);
        if (File.Exists(Store.SettingsPath))
            File.Copy(Store.SettingsPath, Path.Combine(backupDir, "settings-before-migration.json"));
        else
            File.WriteAllText(Path.Combine(backupDir, "settings-before-migration.json"),
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

        int merged = 0;
        foreach (var profile in settings.Profiles)
        {
            var grouped = profile.Layouts.Select(kv => new
            {
                Key = MonitorIdentity.NormalizeKey(kv.Key),
                OriginalKey = kv.Key,
                Layout = kv.Value
            }).GroupBy(item => item.Key, StringComparer.Ordinal).ToList();
            var migrated = new Dictionary<string, Layout>(StringComparer.Ordinal);
            foreach (var group in grouped)
            {
                // Most recent saved layout wins, with the originals still safely archived.
                var chosen = group.OrderByDescending(x => x.Layout.SavedUtc).First();
                chosen.Layout.MonitorKey = group.Key;
                migrated[group.Key] = chosen.Layout;
                if (group.Count() > 1) merged += group.Count() - 1;
            }
            profile.Layouts = migrated;
        }
        Store.Write(settings);
        Diagnostics.Info($"Monitor key migration: {groups.Count} legacy keys normalized, {merged} duplicate entries merged. Original data: {backupDir}");
        return merged;
    }
}
