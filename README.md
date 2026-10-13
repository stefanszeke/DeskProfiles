# DeskProfiles — Smart Monitor Layouts (experimental)

Based on the latest working deletion/navigation repair build. Includes its shortcut discovery, managed-deletion cleanup and selective icon-position switching repair.

## New

- Exact profile + monitor-topology layouts always take precedence, unchanged.
- If the active profile has no saved layout for the current setup, DeskProfiles can infer positions from saved layouts that include compatible displays (matching resolution, with primary designation as a preference).
- Positions are translated between virtual-desktop origins so adding a monitor to the left doesn't shift icons incorrectly.
- If only one display remains, missing icons are placed in free approximate desktop-grid cells. **These fallback placements are not saved**; returning to the previous configuration restores its own original layout.
- The profile layout page shows `Inherited` and provides **Copy inherited to this setup**. This creates an independent override for the current display setup; regular **Save current layout** records the desktop's current arrangement.
- Saved monitor configuration layouts are never rewritten during inheritance or fallback.

## Important limitation

The current settings file identifies monitors by *geometry and primary flag*, not hardware identity/EDID. Two monitors of the same resolution cannot always be reliably distinguished as A vs B. This release preserves compatibility and does **not** migrate or rewrite existing monitor keys; hardware identity matching should be a separate future update.

The automatic B-only fallback is an approximate safe arrangement, not an exact physical-monitor transfer. Some icons may need manual placement. If you save after arranging, that becomes the independent B-only layout.

## Quick test

1. Back up `%LOCALAPPDATA%\\DeskProfiles` and commit the current working source in Git.
2. Exit DeskProfiles via the system tray -> Exit, then copy this source over your current project.
3. Build on Windows using `dotnet build`, then `dotnet run`.
4. Test a profile with an existing A+B saved layout. Disconnect A and leave B only; check whether inherited/fallback positions are used. Do not press Save during the initial test.
5. Reconnect A; verify the original A+B layout still restores.
6. Return to B only, arrange icons as desired and click Save current layout to create a B-only override.

Not compiled or run under Windows in this environment; XAML was parsed and source was reviewed.


## Top-left fallback and independent progress
- In B-only fallback with no exact layout, missing-monitor icons fill unoccupied slots top-to-bottom then left-to-right, starting at the top-left. Directly inherited icons keep their original positions. Nothing is persisted until Save.
- Profile switching uses an independent topmost progress window even if the main window is minimized to tray. The window does not steal keyboard focus and closes after the switch. This does not bypass Windows full-screen exclusive displays or secure desktop.
- No change to the shortcut discovery or selective restoration engine.
