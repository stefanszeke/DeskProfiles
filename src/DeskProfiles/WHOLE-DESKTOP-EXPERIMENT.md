# DeskProfiles experimental whole-Desktop switching

This is an **opt-in, system-wide experiment**, not a fully validated replacement for the original shortcut engine.

**Before testing:** Make a full backup of the actual Windows Desktop directory and `%LOCALAPPDATA%\DeskProfiles`. Close the separate PoC and restore its original Windows Desktop path first. Do not enable if OneDrive Desktop backup is in use (the experiment actively refuses obvious OneDrive paths). Keep only nonessential shortcuts in the test; do not create/move/save real files while redirected.

## Operation

Settings > Desktop shortcut management > **EXPERIMENT: switch Windows Desktop folder as a unit**.

- Confirm the risk dialog. The app records the current original Desktop path in `%LOCALAPPDATA%\DeskProfiles\whole-desktop-experiment\recovery.json` BEFORE redirecting.
- Per-profile folders under `%LOCALAPPDATA%\DeskProfiles\whole-desktop-experiment\profiles\` contain **copies** of the enrolled `.lnk`/`.url` shortcut backups. Shared items appear in all profiles. The original Desktop folder and original enrollment archives are not moved/deleted by this engine.
- Explorer is notified via the shell APIs tested in the PoC. The regular loading indicator and saved layout restoration still run; switching speed has **not been verified**.
- Turning the checkbox off, pressing **Restore original Windows Desktop now**, or exiting via tray **Exit DeskProfiles** restores the original path and notifies Explorer. Simply closing the main window to tray does not restore it.
- On next launch, the app checks the recovery marker **before starting desktop watchers** and restores the original Desktop path after an interrupted/crashed session. It does *not* automatically re-enable the experiment.
- If restoration fails, the app preserves the marker and, on ordinary Exit, refuses to shut down. Do not delete the recovery file.

**Caveat:** Windows Explorer can reset desktop icon positions when switching folder views. Profile layouts may need restoration after the redirect. A missing item is a real failure, not a cosmetic log.

## Suggested test

1. Quit DeskProfiles; back up Desktop and DeskProfiles data; run the new version and check Settings.
2. Enable the experiment while Games is active; confirm normal *nonmanaged* Desktop files disappear from view but remain in their original folder.
3. Switch `Games -> Clean -> Work -> Games`, check actual desktop contents, layout and load indicator, and save logs.
4. Disable the experiment via checkbox; confirm original files appear. Exit from tray, reopen and confirm original Desktop is used.
5. Test a clean exit and restart once before attempting monitor changes.

If a private profile folder contains unexpected files from an earlier experiment, DeskProfiles now **keeps them in place and logs a warning** instead of blocking profile activation. Such items may remain visible in that profile. This is a compatibility measure, not automatic cleanup. Open `%LOCALAPPDATA%\DeskProfiles\whole-desktop-experiment\profiles\` to inspect them after restoring your original Desktop. Don't delete anything blindly. Known filename collisions, modified managed shortcuts, and unverified junctions still block switching rather than risk overwriting user data.

This package does not contain generated `bin` or `obj` content. It was statically inspected but could not be compiled/run on Windows in the packaging environment.


## Shared original Desktop folders (experimental)

In Shortcut Visibility, use **Add a folder from original Desktop...** to enroll one real, first-level directory (such as `desktop`). It is marked shared by default, and profile checkboxes allow restricting its visibility. On the next switch into a whole-Desktop profile, DeskProfiles creates a directory junction pointing to the *original* folder. Your files remain in the original location; no contents are copied or moved.

Safety rules: only first-level non-reparse directories are eligible; an existing destination entry is never overwritten; only a validated matching junction can be removed when no longer assigned. Do not recursively delete a junction through third-party tools. Disable the whole-Desktop experiment and restore the original Desktop before uninstalling, migrating settings, or cleaning the private experimental folder. Back up important data before experimenting; this is not yet tested on Windows in this environment.
