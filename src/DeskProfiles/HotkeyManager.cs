using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DeskProfiles;

public sealed class HotkeyManager : IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hWnd, int id);

    readonly nint hwnd;
    readonly HwndSource source;
    readonly Action<Profile> activate;
    readonly Action<string> log;
    readonly Dictionary<int, Profile> registered = new();
    public HotkeyManager(Window owner, Action<Profile> activate, Action<string> log)
    {
        this.activate = activate;
        this.log = log;
        hwnd = new WindowInteropHelper(owner).EnsureHandle();
        source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("Unable to access WPF window messages.");
        source.AddHook(WndProc);
    }
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }
    static bool Parse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = MOD_NOREPEAT;
        vk = 0;
        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }
        if ((modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)) == 0) return false;
        if (!Enum.TryParse<Key>(parts[^1], true, out var key)) return false;
        int code = KeyInterop.VirtualKeyFromKey(key);
        if (code == 0) return false;
        vk = (uint)code;
        return true;
    }
    public bool Sync(IEnumerable<Profile> profiles) => Sync(profiles, out _);
    public bool Sync(IEnumerable<Profile> profiles, out string failure)
    {
        failure = "";
        foreach (var id in registered.Keys.ToArray()) UnregisterHotKey(hwnd, id);
        registered.Clear();
        int nextId = 100;
        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Hotkey)) continue;
            if (!Parse(profile.Hotkey, out uint mods, out uint vk))
            {
                failure = $"Invalid shortcut '{profile.Hotkey}' for {profile.Name}";
                log(failure);
                return false;
            }
            if (!RegisterHotKey(hwnd, nextId, mods, vk))
            {
                failure = $"{profile.Hotkey} ({profile.Name}) is already registered or reserved by another app; Win32 error {Marshal.GetLastWin32Error()}";
                log(failure);
                return false;
            }
            registered[nextId++] = profile;
        }
        return true;
    }
    nint WndProc(nint hWnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && registered.TryGetValue((int)wParam, out var profile))
        {
            handled = true;
            try { activate(profile); }
            catch (Exception ex) { log($"Hotkey activation failed: {ex.Message}"); }
        }
        return 0;
    }
    public void Dispose()
    {
        foreach (var id in registered.Keys) UnregisterHotKey(hwnd, id);
        registered.Clear();
        source.RemoveHook(WndProc);
    }
}
