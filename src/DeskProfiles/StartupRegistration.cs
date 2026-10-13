using Microsoft.Win32;
using System.IO;

namespace DeskProfiles;

/// <summary>Per-user sign-in startup (no administrator rights or scheduled task).</summary>
public static class StartupRegistration
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeskProfiles";

    public static bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
        return run?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
            existing?.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        // Use the built app executable, never 'dotnet run' or a relative working directory.
        string executable = Path.Combine(AppContext.BaseDirectory, "DeskProfiles.exe");
        if (!File.Exists(executable))
            throw new FileNotFoundException("Cannot find DeskProfiles.exe. Build or publish the app before enabling startup.", executable);

        using var run = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
            ?? throw new InvalidOperationException("Cannot open the current user's Windows startup registry key.");
        run.SetValue(ValueName, $"\"{executable}\" --startup", RegistryValueKind.String);
    }
}
