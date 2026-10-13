# DeskProfiles — Windows 11 WPF prototype (v0.1)

A **non-destructive first step toward a WorkTop replacement**. Keeps layout profiles associated with the active monitor configuration and optionally restores them after a stable monitor change.

## Prerequisites
- Windows 11; .NET 8 SDK (Windows x64). Install from https://dotnet.microsoft.com/download/dotnet/8.0
- PowerShell or Windows Terminal

## Run from source
```powershell
cd src\DeskProfiles
dotnet run
```

## Build as an EXE
```powershell
cd src\DeskProfiles
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ..\..\publish
```
Then open `publish\DeskProfiles.exe`.

## First test
1. **Exit WorkTop** before testing; two icon-management programs may conflict. (Keep WorkTop installed for now.)
2. Windows desktop right-click → View → uncheck **Auto arrange icons**. You may keep Align icons to grid on, but fine positioning can be snapped.
3. Set Win+P → **Extend**, position icons, choose `Personal`, click **Save current layout**.
4. Set Win+P → **Second screen only**, position icons, click **Save current layout**.
5. Toggle back and manually **Restore saved layout** each time. Test **Undo last restore**.
6. Only after verifying manual restore, enable the **Auto restore** checkbox. Monitor state must remain stable for ~4 seconds before it runs.

## Safety and behavior
- Never moves, deletes, renames, or copies Desktop files; never edits the Windows Desktop folder registry path.
- The app never saves layouts automatically (which could overwrite good positions with scrambled ones).
- A restore affects icon positions only, not files. It first captures a session-local undo snapshot.
- If it finds no saved layout for that exact monitor configuration/profile, it does nothing.
- Settings stored in `%LOCALAPPDATA%\DeskProfiles\settings.json` (with a `.bak` of the previous version after each change).
- Profiles in this version are **layout profiles**, not separate desktop *file* sets. Separate file isolation will be a subsequent, higher-risk milestone after this works reliably.
- Recycle Bin visibility is left alone. Save/restore only enumerates items visible to the Windows desktop view.
- Icons may not be restorable after renaming or removing them, or if Windows changes their shell PIDLs. This version matches identifiers from PIDLs, not display names, so renamed items are not guaranteed to match.
- The display identity uses `Screen.AllScreens` device name, coordinates, resolution, and primary flag. Windows may assign different display names after driver changes; those would be considered a different configuration. A later version should use `QueryDisplayConfig` plus stable monitor device paths/EDID identifiers.
- Restore operations are only attempted on the UI STA thread. No Explorer restart.
- If desktop auto-arrange is enabled, Windows may immediately override restored positions.

## Important: untested on this host
The source is packaged for Windows but **has not been compiled or run here**, as this environment lacks the .NET SDK and Windows shell. This is a prototype; interop behavior must be confirmed on your PC before depending on it. If there is a COM exception, report the full exception text and avoid enabling automatic restore.

## Planned milestones
- v0.2: Test/fix Shell COM interop, add robust display identity (`QueryDisplayConfig`), diagnostics, multi-snapshot history, tray icon and hotkeys.
- v0.3: Add **separate desktop files** per profile using an explicitly confirmed, reversible and backed-up transition. Do not swap Desktop registry paths blindly or touch OneDrive-controlled desktop paths without special handling.
- v0.4: Full tray UI, startup integration, crash recovery, profile-specific shortcuts and safeguards.

## Windows Shell API reference
Raymond Chen's Microsoft example: https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933

## v0.1.2 changes
- Fixes the incorrect `FindWindowSW` COM dispatch ID that caused `TargetParameterCountException` during Save.
- Improves visibility of checkbox and monitor information in the dark UI.
- Adds a persistent diagnostic log at `%LOCALAPPDATA%\DeskProfiles\logs\deskprofiles-YYYY-MM-DD.log`.
- Shows short errors on screen, with full stack traces in the log.

This Windows-only application has not been compiled or runtime-tested in this Linux environment. **Keep auto-restore unchecked until manual Save and Restore have been verified.**
