using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace DeskProfiles;
public partial class MainWindow : Window
{
    readonly Settings settings;
    readonly DispatcherTimer monitorTimer;
    string currentTopology;
    string pendingTopology;
    DateTime pendingSince;
    List<IconPosition>? undo;
    bool loading = true;
    Profile Active => settings.Profiles.First(p => p.Name == settings.ActiveProfile);

    public MainWindow()
    {
        InitializeComponent();
        try { settings = Store.Read(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        if (settings.Profiles.Count == 0) settings.Profiles.Add(new Profile());
        if (!settings.Profiles.Any(p => p.Name == settings.ActiveProfile)) settings.ActiveProfile = settings.Profiles[0].Name;
        currentTopology = pendingTopology = MonitorIdentity.Key();
        pendingSince = DateTime.UtcNow;
        ReloadProfiles();
        AutoRestoreBox.IsChecked = settings.AutoRestore;
        ShowMonitors();
        loading = false;
        Log("Ready. Save a layout separately under each display configuration.");
        monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        monitorTimer.Tick += MonitorTimer_Tick;
        monitorTimer.Start();
    }

    void ReloadProfiles()
    {
        ProfileCombo.ItemsSource = null;
        ProfileCombo.ItemsSource = settings.Profiles;
        ProfileCombo.SelectedItem = Active;
    }
    void Persist() => Store.Write(settings);
    void ShowMonitors()
    {
        TopologyText.Text = MonitorIdentity.Description();
        TopologyKeyText.Text = currentTopology;
    }
    void Log(string message)
    {
        Diagnostics.Info(message);
        LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogText.ScrollToEnd();
    }
    void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Name for the new profile:", "New profile", "Work").Trim();
        if (name.Length == 0) return;
        if (settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) { System.Windows.MessageBox.Show("Profile already exists."); return; }
        settings.Profiles.Add(new Profile { Name = name });
        settings.ActiveProfile = name;
        Persist(); ReloadProfiles(); Log($"Profile '{name}' created. Existing desktop files were not changed.");
    }
    void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || ProfileCombo.SelectedItem is not Profile profile || profile.Name == settings.ActiveProfile) return;
        settings.ActiveProfile = profile.Name;
        try { Persist(); Log($"Switched to profile '{profile.Name}'. Use Restore to apply its icon layout."); }
        catch (Exception ex) { Error(ex); }
    }
    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var captured = DesktopIcons.Capture();
            if (captured.Count == 0) throw new InvalidOperationException("No desktop icons found. Nothing was saved.");
            Active.Layouts[currentTopology] = new Layout { MonitorKey = currentTopology, Icons = captured };
            Persist();
            Log($"Saved {captured.Count} icon positions for '{Active.Name}' under this monitor setup.");
        }
        catch (Exception ex) { Error(ex); }
    }
    void Restore_Click(object sender, RoutedEventArgs e) => RestoreCurrent();
    void RestoreCurrent()
    {
        try
        {
            if (!Active.Layouts.TryGetValue(currentTopology, out var layout))
            {
                Log($"No saved layout for '{Active.Name}' in this monitor configuration. Nothing changed.");
                return;
            }
            var snapshot = DesktopIcons.Capture();
            if (snapshot.Count == 0) throw new InvalidOperationException("Current icons could not be captured. Restore aborted.");
            // Undo is taken before every restore. Never overwrite a saved layout on monitor changes.
            var applied = DesktopIcons.Restore(layout.Icons);
            undo = snapshot;
            Log($"Restored {applied} of {layout.Icons.Count} saved icon positions. Undo is available.");
        }
        catch (Exception ex) { Error(ex); }
    }
    void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (undo == null) { Log("No successful restore to undo in this session."); return; }
        try
        {
            var applied = DesktopIcons.Restore(undo);
            undo = null;
            Log($"Undo restored {applied} icon positions.");
        }
        catch (Exception ex) { Error(ex); }
    }
    void AutoRestore_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || settings == null) return;
        settings.AutoRestore = AutoRestoreBox.IsChecked == true;
        try { Persist(); Log(settings.AutoRestore ? "Auto-restore enabled." : "Auto-restore disabled."); }
        catch (Exception ex) { Error(ex); }
    }
    void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        string topology;
        try { topology = MonitorIdentity.Key(); }
        catch (Exception ex) { Diagnostics.Error("Monitor polling", ex); return; }
        if (topology != pendingTopology) { pendingTopology = topology; pendingSince = DateTime.UtcNow; return; }
        if (topology == currentTopology || DateTime.UtcNow - pendingSince < TimeSpan.FromSeconds(4)) return;
        currentTopology = topology;
        ShowMonitors();
        Log("Stable monitor configuration change detected.");
        if (settings.AutoRestore) RestoreCurrent();
    }
    void Error(Exception ex)
    {
        Diagnostics.Error("Operation failed", ex);
        LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}{Environment.NewLine}");
        LogText.ScrollToEnd();
        System.Windows.MessageBox.Show($"{ex.Message}\n\nFull details written to:\n{Diagnostics.LogPath}", "DeskProfiles error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
