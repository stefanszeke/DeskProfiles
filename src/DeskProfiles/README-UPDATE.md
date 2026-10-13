# DeskProfiles – Original Desktop layout + Desktop Items UI update

## Changes
- Original Desktop is a protected profile, but now supports Save / Restore icon layout and automatic layout restore when activated. First activate Original Desktop before clicking Save.
- The Desktop Items page lists **real folders** first, followed by a compact one-line shortcut matrix. Glyphs: ↗ app/file, ▣ folder shortcut, ◎ internet shortcut. Hover to see source/type.
- Private profile folders now remove inactive **managed shortcut copies** only when they match archived originals byte-for-byte. Unknown and modified files remain untouched. This should fix the unchecked-but-visible desktop.lnk leftover.
- Whole-Desktop mode defaults to enabled for new installations, while respecting an explicitly saved disabled setting. On every launch the original Desktop is recovered first; if enabled in settings the selected profile is then reactivated. User can turn off this mode or select Original Desktop.
- No new junction creation. The old verified-junction cleanup behavior is unchanged.

## Testing
Back up original Desktop and `%LOCALAPPDATA%\DeskProfiles`, exit the running app, then build the new project. Test Original Desktop -> Save -> Games -> Original Desktop for layout persistence. Uncheck `desktop` for Games and confirm the inactive managed .lnk is absent. Test the folder/app/web labels and that whole-desktop mode resumes after a normal app exit/relaunch.

## Safety
Whole-Desktop mode changes the Windows Desktop known-folder location. Real original files are not moved, but are hidden from desktop view while a private profile is active. **Restore original Windows Desktop now** remains in Settings. If the app crashes, the next startup attempts original Desktop recovery first. A preexisting off setting remains off.

This source was checked for XML validity and package integrity but was not compiled or run on Windows in this environment.
