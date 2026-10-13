using System.IO;
using System.Text.Json;
namespace DeskProfiles;
public static class Store
{
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskProfiles");
    public static readonly string SettingsPath = Path.Combine(DirectoryPath, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static Settings Read()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) { throw new InvalidOperationException($"Cannot read {SettingsPath}. Existing data was not overwritten.", ex); }
    }
    public static void Write(Settings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        if (File.Exists(SettingsPath)) File.Replace(temp, SettingsPath, SettingsPath + ".bak");
        else File.Move(temp, SettingsPath);
    }
}
