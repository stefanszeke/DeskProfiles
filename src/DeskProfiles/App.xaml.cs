using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DeskProfiles;

public partial class App : System.Windows.Application
{
    // Per-Windows-user, per-logon-session lock. Keep the handle for the full app lifetime.
    private Mutex? instanceMutex;
    private bool ownsInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                       ?? Environment.UserName;
            var mutexName = @"Local\DeskProfiles-" + user.Replace('\\', '_');
            instanceMutex = new Mutex(initiallyOwned: false, name: mutexName);
            try { ownsInstanceMutex = instanceMutex.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstanceMutex = true; }
            if (!ownsInstanceMutex)
            {
                System.Windows.MessageBox.Show(
                    "DeskProfiles is already running. Open it from the system tray.\n\n" +
                    "Only one instance can manage the Windows Desktop folder at a time.",
                    "DeskProfiles already running", MessageBoxButton.OK, MessageBoxImage.Information);
                instanceMutex.Dispose();
                instanceMutex = null;
                Shutdown(0);
                return;
            }
        }
        catch (Exception ex)
        {
            instanceMutex?.Dispose();
            instanceMutex = null;
            System.Windows.MessageBox.Show(
                "Unable to acquire the Desktop safety lock. DeskProfiles will not start.\n\n" + ex.Message,
                "DeskProfiles startup", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;
        Diagnostics.Info("Application starting, version 0.2.2; single-instance lock acquired");
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // This is a last-chance best effort for normal WPF shutdown, not a substitute
        // for the recovery marker (hard process termination never invokes OnExit).
        if (ownsInstanceMutex)
        {
            try { WholeDesktopFolders.Restore(); }
            catch (Exception ex) { Diagnostics.Error("Desktop restore during process shutdown failed; recovery record retained", ex); }
            try { instanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
            ownsInstanceMutex = false;
        }
        instanceMutex?.Dispose();
        instanceMutex = null;
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Error("Unhandled UI exception", e.Exception);
        System.Windows.MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nLog: {Diagnostics.LogPath}",
            "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
