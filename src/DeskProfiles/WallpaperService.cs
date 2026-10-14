using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DeskProfiles;

public static class WallpaperService
{
    private const uint SpiSetDeskWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, string path, uint flags);

    // Capture the user's Windows wallpaper while Original Desktop is active. This
    // baseline survives process exit/restart; never infer it from a Games/Work wallpaper.
    public static (string Path, string Style)? CaptureCurrent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var path = key?.GetValue("Wallpaper") as string;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var styleCode = key?.GetValue("WallpaperStyle")?.ToString() ?? "10";
        var tile = key?.GetValue("TileWallpaper")?.ToString() ?? "0";
        var style = tile == "1" ? "Tile" : styleCode switch
        {
            "6" => "Fit", "2" => "Stretch", "0" => "Center",
            "22" => "Span", _ => "Fill"
        };
        return (path, style);
    }

    public static void RestoreOriginal(Settings settings)
    {
        var original = settings.Profiles.FirstOrDefault(p => p.IsOriginalDesktop);
        if (original?.UseCustomWallpaper == true)
        {
            Apply(original);
            return;
        }
        if (!string.IsNullOrWhiteSpace(settings.OriginalWallpaperPath))
            Apply(new Profile { UseCustomWallpaper = true,
                WallpaperPath = settings.OriginalWallpaperPath,
                WallpaperStyle = settings.OriginalWallpaperStyle });
    }

    public static void Apply(Profile profile)
    {
        if (!profile.UseCustomWallpaper) return;
        var path = Environment.ExpandEnvironmentVariables(profile.WallpaperPath);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Wallpaper image is missing; Windows wallpaper was not changed.", path);

        var style = profile.WallpaperStyle switch
        {
            "Fit" => ("6", "0"),
            "Stretch" => ("2", "0"),
            "Center" => ("0", "0"),
            "Tile" => ("0", "1"),
            "Span" => ("22", "0"),
            _ => ("10", "0") // Fill
        };
        // Windows stores wallpaper display style in the per-user Desktop registry key.
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true)
            ?? throw new InvalidOperationException("Cannot access Windows wallpaper settings.");
        key.SetValue("WallpaperStyle", style.Item1, RegistryValueKind.String);
        key.SetValue("TileWallpaper", style.Item2, RegistryValueKind.String);
        if (!SystemParametersInfo(SpiSetDeskWallpaper, 0, path, SpifUpdateIniFile | SpifSendChange))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused to apply the wallpaper.");
    }
}
