# DeskProfiles — managed deletion and sidebar navigation repair

Based on discovery repair. Keeps current position restoration and creation auto-assignment.

- Deleted unmanaged shortcuts still removed automatically.
- A managed desktop shortcut that is *supposed to be visible* but was deleted manually is removed from catalog after two consecutive 2-second polls (and a 3-second grace after switches). Archived backup remains untouched.
- Managed shortcuts intentionally hidden by profile changes remain in the catalog.
- Re-click the selected profile to return from Settings or Activity.

Caution: If another program temporarily removes a visible managed shortcut for several seconds, the app may treat it as manual deletion. Backup your %LOCALAPPDATA%\DeskProfiles directory before testing.

Quit via system tray before copying files and run `dotnet build` then `dotnet run`. Build/runtime not verified on Windows here.
