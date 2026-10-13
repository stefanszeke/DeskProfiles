# DeskProfiles v0.2.4

Updates: compact buttons/panels and **Settings → Monitors** with per-display **Ignore** option for matching saved layouts. Ignoring does NOT disable or change a monitor in Windows. Settings persist at `%LOCALAPPDATA%\DeskProfiles\settings.json`.

**Important limitations**
- This release identifies ignored displays by Windows `Screen.DeviceName` (`\\.\DISPLAY9`, etc.). Windows can renumber devices after reconnecting, especially virtual displays. Recheck the Ignore settings after reconnection.
- Icon coordinates remain Windows virtual-desktop coordinates. If connecting an ignored screen shifts other screens' origins, icon positions may need a fresh save. It works best when spacedesk is placed to the right of your main displays.
- Ignoring a screen changes matching behavior; it does not delete or migrate the original saved layout entries. Old layouts are searched for compatible non-ignored geometry and new saves use the filtered key. Original settings backups are created automatically by Store.Write as `settings.json.bak`; make a separate copy before upgrading.
- If an old 3-monitor layout matches the same filtered setup as a 2-monitor layout, the exact 2-monitor key takes precedence.

Start with `dotnet run` from `src/DeskProfiles`. Test with auto-restore OFF first. Make a backup of `%LOCALAPPDATA%\DeskProfiles\settings.json` before testing.

# DeskProfiles v0.2 — Profile UI Preview

C# WPF/.NET 8 Windows 11 desktop icon layout manager. This update changes the interface, keeping the existing icon capture/restore implementation and `%LOCALAPPDATA%\DeskProfiles\settings.json` format.

## Run

From `src/DeskProfiles` on Windows 11 with the .NET 8 SDK:

```powershell
dotnet clean
dotnet run
```

## New UI

- Profile list in left sidebar; green dot marks active profile.
- Clicking a profile only **selects** it for viewing; **Activate profile** changes active profile.
- Per-profile saved-monitor layout list and current-setup status.
- Profile creation, rename, and delete (confirmation; cannot remove the last profile).
- Save/Restore/Undo operate on the **active profile**; disabled while inspecting an inactive profile.
- Existing automatic monitor restoration and local diagnostics are preserved.

## Safety

- No swapping or moving desktop files. No desktop icon hiding or wallpaper changes yet.
- Profile deletion removes that profile's saved layouts from the app settings, **not Windows desktop files**.
- Back up `%LOCALAPPDATA%\DeskProfiles\settings.json` before upgrading. Store uses a `.bak` file on each successful write.
- Untested on Windows in this environment; retain your Git v0.1.3 commit as rollback.
- If something fails, see `%LOCALAPPDATA%\DeskProfiles\logs`.

## Testing

1. Save/Restore/Undo in an existing profile with auto-restore off.
2. Select another profile: nothing should change on your desktop.
3. Activate it explicitly; test its separate layouts.
4. Confirm your existing layouts still appear and all three monitor configurations work.

## Next

Real per-profile icon visibility, then wallpapers; neither implemented in v0.2.

## v0.2.2 profile switching fix

Selecting a profile in the sidebar only displays its details. Press **Activate profile** to switch to it. Activation now restores its saved icon positions for the current monitor topology automatically, if a saved layout exists. It does not automatically save the previous profile, preventing unwanted overwrites. If no layout exists for that profile and monitor configuration, activation leaves the icons untouched; arrange them and press Save current layout. Use Undo restore to reverse the last applied positions. Automatic restoration on monitor change remains independently configurable.

## v0.2.2 UI organization

- Profiles: saved icon layouts and profile actions, unchanged from v0.2.1.
- Settings: global automatic monitor restore setting and links to the local data/logs folders.
- Activity & Logs: session activity, current log, and logs directory.
- Startup, tray, wallpapers, and per-profile icon visibility are **not implemented**.
- Select a profile to go to the Profiles page; activating still applies its saved layout.

## v0.2.4 monitor topology fix

- Keys are now based on sorted screen coordinates, resolution and the **primary flag**. Transient Windows `DISPLAY1`/`DISPLAY2` numbering no longer changes the key.
- The current monitor section explicitly marks `(PRIMARY)` in Extended mode.
- Saved monitor layouts have readable summaries rather than internal keys.
- Existing legacy keys are migrated automatically on startup. The complete original settings file is backed up first in `%LOCALAPPDATA%\DeskProfiles\migration-backups\<timestamp>`. When two layouts collide, the most recently saved one is used, and the original is preserved in the backup.
- Different screen positions, resolution, or primary-display settings deliberately remain different layouts.
- Migration has not been run on a real Windows machine in this environment. Test all three monitor modes with automatic restore disabled first.


## v0.2.5 — profile wallpapers
Select a profile and choose Wallpaper → Browse, then select a style. Wallpaper preferences are stored per profile, not per monitor layout. The Windows wallpaper changes when the profile is activated or when you press Apply now on the active profile. Disabling or clearing the option does not reset Windows' wallpaper. Image files are referenced by path and are not copied; keep them in a permanent location.

The initial wallpaper backend uses the Windows user wallpaper setting; it applies one image/style globally, rather than separate images for individual monitors. Errors are logged in the existing logs directory.


## v0.2.5.1 fixes
- Fix `List<string>.Length` compilation error in monitor layout descriptions (`.Count`).
- Make wallpaper style dropdown text readable by using dark text on a light popup.
- Wallpaper disabled continues to leave Windows wallpaper unchanged.


## v0.2.7 — global shortcuts and tray

Select a profile, press **Record shortcut**, then press a combination including Ctrl, Alt or Win (e.g. Alt+Shift+1). Press Esc to cancel; Clear removes it. Shortcuts work while DeskProfiles is running, including when hidden. If registration fails due to a conflict, no new binding is saved. A global shortcut activates its target profile without focusing the app.

Closing the window hides it to the system tray. Double-click tray icon to show it; right-click and choose **Exit DeskProfiles** to actually quit. Windows startup is not enabled. This is a Windows-only source build and still needs compilation and runtime testing.

## v0.2.7 — Start with Windows

In **Settings → Startup & tray**, enable **Start DeskProfiles when I sign in to Windows**. This registers the current build's `DeskProfiles.exe` in the current user's `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` registry key with a `--startup` argument. Windows starts the app hidden in the tray; global profile shortcuts work without opening the window. Disable the checkbox to remove the entry. No admin permissions required.

**If you move or rebuild to a new output path**, disable and re-enable startup from the new build so Windows points to the new executable. For daily use, publish to a stable directory instead of relying on `bin\Debug`. No profile data or layouts are migrated or deleted. The hotkey property is `Profile.Hotkey`; the WPF Key enum is aliased `WpfKey`.
