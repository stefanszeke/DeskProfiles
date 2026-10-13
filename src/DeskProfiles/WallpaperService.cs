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
