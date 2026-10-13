using System.Windows;
using System.IO;
using System.Diagnostics;
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
    Profile Selected => (ProfileList.SelectedItem as ProfileRow)?.Profile ?? Active;

    sealed class ProfileRow
    {
        public required Profile Profile { get; init; }
        public string Name => Profile.Name;
        public Visibility ActiveVisibility { get; init; }
    }
    sealed record LayoutRow(string Title, string Detail);
    public sealed record MonitorRow(string DeviceName, string Description, bool IsIgnored);
    bool updatingMonitors;
    string Key() => MonitorIdentity.Key(settings.IgnoredMonitors);
    Layout? FindLayout(Profile profile)
    {
        if (profile.Layouts.TryGetValue(currentTopology, out var direct)) return direct;
        // Historical keys may contain an optional ignored display. Never delete them.
        return profile.Layouts.Values.Where(l => MonitorIdentity.MatchKey(l.MonitorKey, settings.IgnoredMonitors) == currentTopology)
            .OrderByDescending(l => l.SavedUtc).FirstOrDefault();
    }
    void RefreshMonitors()
    {
        updatingMonitors = true;
        MonitorsList.ItemsSource = null;
        MonitorsList.ItemsSource = MonitorIdentity.AllDisplays().Select(m => new MonitorRow(
            m.DeviceName,
            $"{m.Bounds.Width}×{m.Bounds.Height}  ·  {(m.Primary ? "Primary" : "Secondary")}  ·  X={m.Bounds.X}, Y={m.Bounds.Y}",
            settings.IgnoredMonitors.Contains(m.DeviceName, StringComparer.OrdinalIgnoreCase))).ToList();
        updatingMonitors = false;
    }
    void Monitors_Refresh_Click(object sender, RoutedEventArgs e) => RefreshMonitors();
    void MonitorIgnore_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || updatingMonitors || sender is not System.Windows.Controls.CheckBox cb || cb.DataContext is not MonitorRow row) return;
        bool ignored = cb.IsChecked == true;
        var before = settings.IgnoredMonitors.ToList();
        if (ignored && MonitorIdentity.AllDisplays().All(m => m.DeviceName == row.DeviceName || settings.IgnoredMonitors.Contains(m.DeviceName)))
        {
            updatingMonitors = true;
            cb.IsChecked = false;
            updatingMonitors = false;
            Log("Cannot ignore every active display.");
            return;
        }
        try
        {
            if (ignored) settings.IgnoredMonitors.Add(row.DeviceName);
            else settings.IgnoredMonitors.RemoveAll(n => n.Equals(row.DeviceName, StringComparison.OrdinalIgnoreCase));
            Persist();
            currentTopology = pendingTopology = Key();
            pendingSince = DateTime.UtcNow;
            undo = null;
            RefreshDetails();
            Log($"{(ignored ? "Ignoring" : "Including")} {row.DeviceName} for layout matching. Display remains enabled in Windows.");
        }
        catch (Exception ex)
        {
            settings.IgnoredMonitors = before;
            updatingMonitors = true;
            cb.IsChecked = !ignored;
            updatingMonitors = false;
            Error(ex);
        }
    }

    public MainWindow()
    {
        InitializeComponent();
        try { settings = Store.Read(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        LayoutMigration.Migrate(settings);
        if (settings.Profiles.Count == 0) settings.Profiles.Add(new Profile());
        if (!settings.Profiles.Any(p => p.Name == settings.ActiveProfile)) settings.ActiveProfile = settings.Profiles[0].Name;
        currentTopology = pendingTopology = Key();
        pendingSince = DateTime.UtcNow;
        ReloadProfiles(Active);
        AutoRestoreBox.IsChecked = settings.AutoRestore;
        loading = false;
        RefreshMonitors();
        RefreshDetails();
        Log("Ready. Existing profile layouts have been loaded.");
        monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        monitorTimer.Tick += MonitorTimer_Tick;
        monitorTimer.Start();
    }

    void ShowPage(string page)
    {
        ProfilesHeader.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
    }

    void SettingsPage_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    void ActivityPage_Click(object sender, RoutedEventArgs e) => ShowPage("activity");

    void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex); }
    }
    void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Store.DirectoryPath);
    void OpenLogsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(Path.GetDirectoryName(Diagnostics.LogPath)!);
    void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Diagnostics.LogPath;
            if (!File.Exists(path)) { Log("No log file exists yet for today."); return; }
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) { Error(ex); }
    }
    void ClearActivity_Click(object sender, RoutedEventArgs e) => LogText.Clear();

    void ReloadProfiles(Profile? selected = null)
    {
        var target = selected ?? Selected;
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = settings.Profiles.Select(p => new ProfileRow
        {
            Profile = p,
            ActiveVisibility = p.Name == settings.ActiveProfile ? Visibility.Visible : Visibility.Collapsed
        }).ToList();
        ProfileList.SelectedItem = ProfileList.Items.Cast<ProfileRow>().FirstOrDefault(r => ReferenceEquals(r.Profile, target))
            ?? ProfileList.Items.Cast<ProfileRow>().First();
        if (!loading) RefreshDetails();
    }

    void Persist() => Store.Write(settings);

    void RefreshDetails()
    {
        var profile = Selected;
        bool active = ReferenceEquals(profile, Active);
        ProfileTitle.Text = profile.Name;
        ProfileSubtitle.Text = active ? "Active desktop profile" : "Viewing profile settings · not active";
        ProfileStateText.Text = active ? "This profile is active. Save and Restore will use its layouts." :
            $"‘{Active.Name}’ is currently active. Click Activate to use ‘{profile.Name}’.";
        ActivateButton.IsEnabled = !active;
        DeleteButton.IsEnabled = settings.Profiles.Count > 1;
        SaveButton.IsEnabled = active;
        RestoreButton.IsEnabled = active && FindLayout(profile) != null;
        UndoButton.IsEnabled = undo != null;
        TopologyText.Text = MonitorIdentity.Description(settings.IgnoredMonitors);
        LayoutStatusText.Text = FindLayout(profile) is { } layout
            ? $"Saved for this setup · {layout.Icons.Count} icons · {layout.SavedUtc.ToLocalTime():g}"
            : "No saved layout for this monitor configuration.";
        SavedLayoutsList.ItemsSource = profile.Layouts.Values.OrderByDescending(l => l.SavedUtc)
            .Select(l => new LayoutRow(MonitorIdentity.MatchKey(l.MonitorKey, settings.IgnoredMonitors) == currentTopology ? "● Current monitor setup" : "Saved monitor setup",
                $"{MonitorIdentity.DescribeKey(l.MonitorKey)}  ·  {l.Icons.Count} icons  ·  {l.SavedUtc.ToLocalTime():g}"))
            .ToList();
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
        if (settings.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { System.Windows.MessageBox.Show("Profile already exists."); return; }
        var profile = new Profile { Name = name };
        settings.Profiles.Add(profile);
        try { Persist(); ReloadProfiles(profile); ShowPage("profiles"); Log($"Created '{name}'. Select Activate when ready."); }
        catch (Exception ex) { Error(ex); }
    }

    void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading && ProfileList.SelectedItem != null) { RefreshDetails(); ShowPage("profiles"); }
    }

    void Activate_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        if (ReferenceEquals(profile, Active)) return;
        var previous = settings.ActiveProfile;
        try
        {
            // Capture the current positions before activating a different profile.
            // No saved layout is overwritten: Save is always an explicit action.
            List<IconPosition>? before = null;
            int applied = 0;
            if (FindLayout(profile) is { } layout)
            {
                before = DesktopIcons.Capture();
                if (before.Count == 0) throw new InvalidOperationException("Cannot capture current icons; profile switch aborted.");
                applied = DesktopIcons.Restore(layout.Icons);
                if (applied == 0) throw new InvalidOperationException("The selected layout matches no current icons; profile switch aborted.");
            }
            settings.ActiveProfile = profile.Name;
            undo = before;
            try { Persist(); }
            catch
            {
                settings.ActiveProfile = previous;
                if (before != null) DesktopIcons.Restore(before);
                throw;
            }
            ReloadProfiles(profile);
            Log(before is null
                ? $"Activated '{profile.Name}'; no saved layout for this monitor setup. Icon positions unchanged. Arrange and Save when ready."
                : $"Activated '{profile.Name}' and restored {applied} icon positions. Undo is available.");
        }
        catch (Exception ex) { settings.ActiveProfile = previous; ReloadProfiles(profile); Error(ex); }
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        var name = Microsoft.VisualBasic.Interaction.InputBox("New profile name:", "Rename profile", profile.Name).Trim();
        if (name.Length == 0 || name == profile.Name) return;
        if (settings.Profiles.Any(p => !ReferenceEquals(p, profile) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        { System.Windows.MessageBox.Show("Profile already exists."); return; }
        string previous = profile.Name;
        try
        {
            profile.Name = name;
            if (settings.ActiveProfile == previous) settings.ActiveProfile = name;
            Persist(); ReloadProfiles(profile); Log($"Renamed '{previous}' to '{name}'.");
        }
        catch (Exception ex)
        { profile.Name = previous; if (settings.ActiveProfile == name) settings.ActiveProfile = previous; ReloadProfiles(profile); Error(ex); }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (settings.Profiles.Count < 2) return;
        var profile = Selected;
        if (System.Windows.MessageBox.Show($"Delete profile '{profile.Name}' and its saved layouts?\n\nDesktop files and icons will NOT be deleted.",
                "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var oldActive = settings.ActiveProfile;
        int index = settings.Profiles.IndexOf(profile);
        try
        {
            settings.Profiles.Remove(profile);
            if (oldActive == profile.Name) settings.ActiveProfile = settings.Profiles[0].Name;
            Persist(); undo = null; ReloadProfiles(Active); Log($"Deleted profile '{profile.Name}' (saved layouts removed). Desktop files unchanged.");
        }
        catch (Exception ex)
        { settings.Profiles.Insert(index, profile); settings.ActiveProfile = oldActive; ReloadProfiles(profile); Error(ex); }
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
            RefreshDetails();
        }
        catch (Exception ex) { Error(ex); }
    }

    void Restore_Click(object sender, RoutedEventArgs e) => RestoreCurrent();
    void RestoreCurrent()
    {
        try
        {
            if (FindLayout(Active) is not { } layout)
            { Log($"No saved layout for '{Active.Name}' in this monitor configuration. Nothing changed."); return; }
            var snapshot = DesktopIcons.Capture();
            if (snapshot.Count == 0) throw new InvalidOperationException("Current icons could not be captured. Restore aborted.");
            var applied = DesktopIcons.Restore(layout.Icons);
            undo = snapshot;
            Log($"Restored {applied} of {layout.Icons.Count} saved icon positions for '{Active.Name}'. Undo is available.");
            RefreshDetails();
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
            RefreshDetails();
        }
        catch (Exception ex) { Error(ex); }
    }

    void AutoRestore_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        settings.AutoRestore = AutoRestoreBox.IsChecked == true;
        try { Persist(); Log(settings.AutoRestore ? "Auto-restore enabled." : "Auto-restore disabled."); }
        catch (Exception ex) { Error(ex); }
    }

    void MonitorTimer_Tick(object? sender, EventArgs e)
    {
        string topology;
        try { topology = Key(); }
        catch (Exception ex) { Diagnostics.Error("Monitor polling", ex); return; }
        if (topology != pendingTopology) { pendingTopology = topology; pendingSince = DateTime.UtcNow; return; }
        if (topology == currentTopology || DateTime.UtcNow - pendingSince < TimeSpan.FromSeconds(4)) return;
        currentTopology = topology;
        undo = null;
        RefreshMonitors();
        RefreshDetails();
        Log("Stable monitor configuration change detected.");
        if (settings.AutoRestore) RestoreCurrent();
    }

    void Error(Exception ex)
    {
        Diagnostics.Error("Operation failed", ex);
        LogText.AppendText($"[{DateTime.Now:HH:mm:ss}] ERROR: {ex.Message}{Environment.NewLine}");
        LogText.ScrollToEnd();
        System.Windows.MessageBox.Show($"{ex.Message}\n\nFull details written to:\n{Diagnostics.LogPath}",
            "DeskProfiles error", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
