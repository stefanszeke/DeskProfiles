# DeskProfiles release-safety pass 1

This update is based on the last tested shortcut-conflict-recovery source, not the older layout-management ZIP.

- `App.xaml.cs` obtains a named, per-user Windows mutex **before** creating MainWindow. A second process displays a message and exits without attempting Desktop redirection or startup recovery.
- `App.OnExit` makes a best-effort original-Desktop restore when the application shuts down normally; the existing recovery marker remains the recovery strategy for hard termination.
- `MainWindow.Window_Closing` already restores the original Desktop before allowing Exit, closing the loader and tray resources. These behaviors are retained.
- `tools/Publish-Portable.ps1`: self-contained single-file `win-x64` (or `win-arm64`) Release publish.
- `tools/Restore-OriginalDesktop.ps1`: explicit fallback for manual recovery if the WPF executable cannot start. Checks marker and current target, requires typed confirmation, verifies redirected path, sends Shell notification. Only run while no DeskProfiles process is running.

## Test on Windows

1. Back up Desktop and `%LOCALAPPDATA%\DeskProfiles`; stop any existing DeskProfiles instances.
2. Build with `dotnet build`, then run `dotnet run`.
3. Try launching a second copy. It should refuse while the first copy remains running.
4. With Games active, exit from the tray; confirm the original Desktop returns. Restart; confirm the last Games profile and layout resume.
5. Test a process kill **only after the above succeeds and backups are verified**. On next launch recovery should first restore original Desktop, then resume saved profile.
6. Run `./tools/Publish-Portable.ps1` in PowerShell, after exiting the dev copy; launch output executable without `dotnet run`.

Do not uninstall or delete `%LOCALAPPDATA%\DeskProfiles` while the Desktop is redirected. An installer with safe uninstall is a future step.

**Validation:** Package / XML / source checked in Linux environment. No Windows build or live Explorer testing performed here.
