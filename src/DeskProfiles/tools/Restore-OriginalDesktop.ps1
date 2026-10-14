# Emergency recovery when DeskProfiles cannot start. No Desktop files are moved or deleted.
# Uses its persisted recovery marker; refuses unsafe/unrecognized paths.
$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'DeskProfiles\whole-desktop-experiment'
$stateFile = Join-Path $root 'recovery.json'
if (-not (Test-Path -LiteralPath $stateFile -PathType Leaf)) {
    Write-Host 'No recovery.json. No redirection record to restore.'
    exit 0
}
$state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
$original = [string]$state.OriginalDesktop
if (-not $original -or -not (Test-Path -LiteralPath $original -PathType Container)) {
    throw "Original Desktop path is missing or invalid: $original"
}
$target = [IO.Path]::GetFullPath($original).TrimEnd('\')
$confirmed = Read-Host "Restore Windows Desktop to '$target'? Type RESTORE to confirm"
if ($confirmed -cne 'RESTORE') { Write-Host 'Cancelled.'; exit 1 }
# SHSetKnownFolderPath P/Invoke via PowerShell Add-Type. No Explorer process restart.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DeskProfilesDesktopRecovery {
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
  public static extern int SHSetKnownFolderPath(ref Guid id, uint flags, IntPtr token, string path);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
  public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
  [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
  public static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr unused);
}
'@
$id = [Guid]'B4BFCC3A-DB2C-424C-B029-7FE99A87C641'
$ptr = [IntPtr]::Zero
$hr = [DeskProfilesDesktopRecovery]::SHGetKnownFolderPath([ref]$id,0,[IntPtr]::Zero,[ref]$ptr)
if ($hr -ne 0) { [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($hr) }
try { $current = [Runtime.InteropServices.Marshal]::PtrToStringUni($ptr) }
finally { [Runtime.InteropServices.Marshal]::FreeCoTaskMem($ptr) }
$allowedRoot = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
if (-not ($current.Equals($target,[StringComparison]::OrdinalIgnoreCase) -or
          $current.StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase))) {
    throw "Current Desktop '$current' is not original or an app-owned redirected path; refusing to overwrite it."
}
$hr = [DeskProfilesDesktopRecovery]::SHSetKnownFolderPath([ref]$id,0,[IntPtr]::Zero,$target)
if ($hr -ne 0) { [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($hr) }
$ptr = [IntPtr]::Zero
$hr = [DeskProfilesDesktopRecovery]::SHGetKnownFolderPath([ref]$id,0,[IntPtr]::Zero,[ref]$ptr)
if ($hr -ne 0) { [Runtime.InteropServices.Marshal]::ThrowExceptionForHR($hr) }
try { $actual = [Runtime.InteropServices.Marshal]::PtrToStringUni($ptr) }
finally { [Runtime.InteropServices.Marshal]::FreeCoTaskMem($ptr) }
if (-not $actual.Equals($target,[StringComparison]::OrdinalIgnoreCase)) { throw "Verification failed: $actual" }
[DeskProfilesDesktopRecovery]::SHChangeNotify(0x00001000,0x1005,$target,[IntPtr]::Zero)
[DeskProfilesDesktopRecovery]::SHChangeNotify(0x08000000,0x1000,$null,[IntPtr]::Zero)
Remove-Item -LiteralPath $stateFile -Force
Write-Host 'Original Desktop path restored. If the desktop display has not updated, click it and press F5.'
