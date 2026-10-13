using System.IO;
namespace DeskProfiles;
internal static class Diagnostics
{
    public static string LogPath => Path.Combine(Store.DirectoryPath, "logs", $"deskprofiles-{DateTime.Now:yyyy-MM-dd}.log");
    private static readonly object Sync = new();
    public static void Info(string message) => Write("INFO", message);
    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");
    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must never crash desktop management. */ }
    }
}
