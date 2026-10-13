# Original Desktop system profile + Desktop Items UI

Experimental Windows Desktop known-folder redirection. Backup your real Desktop and `%LOCALAPPDATA%\DeskProfiles` before installing.

- Original Desktop is a protected selectable profile in the sidebar. It points Explorer back to the original Windows Desktop folder without disabling whole-Desktop mode.
- Real files and folders appear there exactly as they exist on disk. Renaming/removing the system profile in DeskProfiles is disabled. Hotkeys are supported.
- Shortcut assignment matrix now identifies `.lnk` and `.url` items. Original Desktop is excluded from assignment columns because it is not managed by DeskProfiles.
- Real folders on Original Desktop appear in a separate, read-only list for clarity.
- The old folder junction feature is retired: new junctions are not made; already-enrolled junctions in experimental profile folders are removed only after validating their exact target. Unrecognized entries are preserved.
- Legacy folder enrollment data remains in settings for backwards compatibility, but is no longer applied to profiles.
- Only when whole-Desktop experiment is enabled can Original Desktop be activated. When disabled or exiting, the original location is restored.
- On startup an interrupted experiment is recovered, and the new mode is opt-in again.

Tests: enable experiment, switch Games -> Original Desktop -> Work -> Original Desktop, disable experiment, confirm actual Desktop and its files remain intact; inspect per-profile shortcuts and icon layouts. Exit/relaunch and check recovery. For safety do not use OneDrive Desktop redirection or save important files to temporary profile folders.

Source inspected and XAML parsed; Windows/.NET WPF build/runtime cannot be executed in this environment.
