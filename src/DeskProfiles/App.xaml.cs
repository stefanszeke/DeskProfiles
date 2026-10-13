using System.Windows;
using System.Windows.Threading;
namespace DeskProfiles;
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        Diagnostics.Info("Application starting, version 0.2.2");
        base.OnStartup(e);
    }
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Error("Unhandled UI exception", e.Exception);
        System.Windows.MessageBox.Show($"Unexpected error: {e.Exception.Message}\n\nLog: {Diagnostics.LogPath}", "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
