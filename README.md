# DeskProfiles — Layout Management & Stability (experimental)

Based on the latest corrected Smart Fallback + Topmost Loader source.

## Changes
- Global Shortcut Visibility navigation (assignments remain global and independent of monitor setup).
- Select saved layouts per profile; rename, delete, reset-to-inheritance, or copy onto the connected monitor setup with coordinate remapping and overwrite confirmation.
- After automatic monitor-change restoration, two delayed verify/correct passes address Explorer rearranging icons. The checks never persist fallback positions.
- Existing shortcut discovery, managed visibility, hotkeys and independent topmost loader preserved.

## Safety and tests
Backup `%LOCALAPPDATA%\DeskProfiles` and exit the tray process before replacing source. Run `dotnet build` from `src/DeskProfiles`. The WPF Windows build could not be executed in this Linux environment.

Copying a layout to a different monitor setup is supported only when SmartMonitorLayouts can map positions. This is a deliberate safety constraint.

To test: Open Settings → Shortcut Visibility → select original profile again; delete a disposable saved layout; confirm layouts for other setups remain. Test changing A+B → B-only, with auto-restore enabled, and wait ~5 seconds before evaluating arrangement.


## Quit lifecycle hotfix
- Closes the standalone topmost loader on Exit instead of only hiding it.
- Stops monitor and shortcut timers and disposes watchers/tray/hotkeys.
- Sets WPF ShutdownMode=OnMainWindowClose and requests explicit shutdown after cleanup.
- No changes to saved layouts, shortcut visibility, or monitor restoration.
- Monitor reflow after a display transition remains under investigation.

## Ignored monitor placement hotfix
- Profile layout lookup now excludes positions whose current desktop coordinates lie on an ignored display (including spacedesk), relocating those icons to free grid cells on the non-ignored primary monitor for this restore only.
- Inheritance translates targets using the full Windows virtual-desktop origin, including ignored displays, so negative-origin spacedesk screens do not shift positions.
- Saved layouts and shortcut assignments are not changed. The exit hotfix is retained.
- Limitation: physical monitor matching still uses display geometry; this is a placement safety fix, not hardware-ID matching.
