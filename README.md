# DeskProfiles — targeted shortcut positioning repair (experimental)

Based on the previous icon-diagnostics baseline. This update does not change saved layout files or shortcut ownership.

Changes:
- Suppress false restoration failures for managed shortcuts intentionally hidden by the destination profile.
- Wait until managed shortcut names appear/disappear in the actual Explorer desktop icon inventory.
- Reposition only currently visible icons that are more than 2px from their saved location.
- Limit restore passes to three with short verification delays; no repeated retries of absent icons.
- Log eligible, hidden, missing, and verified icon counts.

Limitations:
- A newly created shortcut without any saved position still needs manual arrangement and Save Layout.
- The existing automatic discovery setting and behavior have not been changed.
- Explorer can still reflow desktop icons; test carefully with disposable shortcuts.
- Builds have not been compiled or run on Windows in this environment.

Exit DeskProfiles from the system tray before rebuilding. Back up `%LOCALAPPDATA%\DeskProfiles` before testing.
Run `dotnet build` then `dotnet run` on Windows.
