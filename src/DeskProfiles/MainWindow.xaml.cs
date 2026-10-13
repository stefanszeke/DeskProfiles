using System.Windows;
using System.IO;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using WpfKey = System.Windows.Input.Key;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using Button = System.Windows.Controls.Button;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace DeskProfiles;

public partial class MainWindow : Window
{
    readonly Settings settings;
    HotkeyManager? hotkeys;
    Forms.NotifyIcon? tray;
    bool reallyExit;
    bool resumingSavedWholeDesktop;
    bool recordingHotkey;
    bool switchingProfile;
    SwitchLoadingWindow? switchLoadingWindow;
    FileSystemWatcher? desktopWatcher;
    readonly HashSet<string> newDesktopShortcuts = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer shortcutWatchTimer = new() { Interval = TimeSpan.FromMilliseconds(850) };
    readonly DispatcherTimer shortcutReconcileTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly HashSet<string> knownDesktopFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> discoveredForProfile = new(StringComparer.OrdinalIgnoreCase);
    bool reconcilingDesktop;
    readonly Dictionary<string, int> missingVisibleManagedChecks = new(StringComparer.OrdinalIgnoreCase);
    DateTime lastShortcutReconcileUtc = DateTime.UtcNow;
    Profile? recordingProfile;

    readonly DispatcherTimer monitorTimer;
    int monitorTransitionGeneration;
    string currentTopology;
    string pendingTopology;
    DateTime pendingSince;
    List<IconPosition>? undo;
    bool loading = true;
    static bool SystemProfile(Profile profile) => profile.IsOriginalDesktop;
    void EnsureOriginalProfile()
    {
        var system = settings.Profiles.FirstOrDefault(p => p.IsOriginalDesktop);
        if (system == null) settings.Profiles.Insert(0, new Profile { Name = "Original Desktop", IsOriginalDesktop = true });
        else system.Name = "Original Desktop";
    }
    Profile Active => settings.Profiles.First(p => p.Name == settings.ActiveProfile);
    Profile Selected => (ProfileList.SelectedItem as ProfileRow)?.Profile ?? Active;

    sealed class ProfileRow
    {
        public required Profile Profile { get; init; }
        public string Name => Profile.Name;
        public Visibility ActiveVisibility { get; init; }
    }
    sealed record LayoutRow(string Key, string Title, string Detail);
    public sealed record MonitorRow(string DeviceName, string Description, bool IsIgnored);
    bool updatingMonitors;
    bool updatingWallpaper;
    sealed record ShortcutRow(ManagedShortcut Item, string Description);
    string Key() => MonitorIdentity.Key(settings.IgnoredMonitors);
    Layout? FindExactLayout(Profile profile)
    {
        if (profile.Layouts.TryGetValue(currentTopology, out var direct)) return direct;
        return profile.Layouts.Values.Where(l => MonitorIdentity.MatchKey(l.MonitorKey, settings.IgnoredMonitors) == currentTopology)
            .OrderByDescending(l => l.SavedUtc).FirstOrDefault();
    }

    Layout? FindLayout(Profile profile)
    {
        var layout = FindExactLayout(profile)
            ?? SmartMonitorLayouts.TryCreate(profile, currentTopology, settings.IgnoredMonitors)?.Layout;
        return layout == null ? null : EligibleMonitorPlacement.Constrain(layout, settings.IgnoredMonitors);
    }

    void CopyInheritedLayout_Click(object sender, RoutedEventArgs e)
    {
        if (switchingProfile || !ReferenceEquals(Selected, Active)) return;
        if (FindExactLayout(Active) != null)
        {
            Log("This profile already has a saved layout for the current monitor setup. Nothing copied.");
            return;
        }
        var inherited = SmartMonitorLayouts.TryCreate(Active, currentTopology, settings.IgnoredMonitors);
        if (inherited == null)
        {
            Log("No compatible saved monitor positions are available to copy.");
            return;
        }
        try
        {
            Active.Layouts[currentTopology] = new Layout
            {
                MonitorKey = currentTopology,
                Icons = inherited.Layout.Icons.Select(i => new IconPosition { Pidl = i.Pidl, X = i.X, Y = i.Y }).ToList()
            };
            Persist();
            Log($"Copied {inherited.Layout.Icons.Count} inherited icon positions into an independent layout for this monitor setup. Use Save after arranging any remaining icons.");
            RefreshDetails();
        }
        catch (Exception ex) { Error(ex); }
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
        // Repair an interrupted experiment BEFORE loading settings, watchers or desktop positions.
        var recoveryMessage = WholeDesktopFolders.RecoverAtStartup();
        InitializeComponent();
        if (recoveryMessage.Length > 0) Diagnostics.Info("[WHOLE DESKTOP] " + recoveryMessage);
        try { settings = Store.Read(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Error);
            throw;
        }
        LayoutMigration.Migrate(settings);
        EnsureOriginalProfile();
        if (settings.Profiles.Count == 0) settings.Profiles.Add(new Profile());
        if (!settings.Profiles.Any(p => p.Name == settings.ActiveProfile)) settings.ActiveProfile = settings.Profiles[0].Name;
        currentTopology = pendingTopology = Key();
        pendingSince = DateTime.UtcNow;
        ReloadProfiles(Active);
        AutoRestoreBox.IsChecked = settings.AutoRestore;
        ShortcutSwitchingBox.IsChecked = settings.EnableShortcutVisibilitySwitching;
        FolderStagingBox.IsChecked = settings.UseFolderStaging;
        // On startup the recovery marker is handled first. Re-enable the user's saved
        // whole-folder preference after the window and desktop observers are initialized.
        bool resumeWholeDesktop = settings.UseWholeDesktopFolders;
        WholeDesktopBox.IsChecked = false;
        if (resumeWholeDesktop)
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (reallyExit || switchingProfile) return;
                resumingSavedWholeDesktop = true;
                try { WholeDesktopBox.IsChecked = true; }
                finally { resumingSavedWholeDesktop = false; }
            }), DispatcherPriority.Background);
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
        AutoManageShortcutBox.IsChecked = settings.AutoManageNewShortcuts;
        loading = false;
        RefreshMonitors();
        RefreshDetails();
        Log("Ready. Existing profile layouts have been loaded.");
        monitorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        monitorTimer.Tick += MonitorTimer_Tick;
        monitorTimer.Start();
        Loaded += (_, _) => { InitializeTrayAndHotkeys(); try { InventoryDesktop(); RefreshShortcuts(); } catch (Exception ex) { Diagnostics.Error("Desktop inventory", ex); } StartShortcutWatcher(); StartDesktopReconciliation(); };
        shortcutWatchTimer.Tick += NewShortcutTimer_Tick;
        shortcutReconcileTimer.Tick += (_, _) => ReconcileDesktopInventory();
    }

    void ShowPage(string page)
    {
        ProfilesHeader.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        ProfilesPage.Visibility = page == "profiles" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ActivityPage.Visibility = page == "activity" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = page == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
    }

    void SettingsPage_Click(object sender, RoutedEventArgs e) => ShowPage("settings");
    void ShortcutsPage_Click(object sender, RoutedEventArgs e) { RefreshShortcuts(); ShowPage("shortcuts"); }
    void ProfileList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // SelectionChanged does not fire when the already-selected row is clicked again.
        // Allow navigation back from Settings/Activity without choosing another profile first.
        if (SettingsPage.Visibility == Visibility.Visible || ActivityPage.Visibility == Visibility.Visible || ShortcutsPage.Visibility == Visibility.Visible)
            ShowPage("profiles");
    }
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
        ProfileSubtitle.Text = SystemProfile(profile) ? "Protected system profile · actual Windows Desktop folder" :
            active ? "Active desktop profile" : "Viewing profile settings · not active";
        ProfileStateText.Text = active ? "This profile is active. Save and Restore will use its layouts." :
            $"‘{Active.Name}’ is currently active. Click Activate to use ‘{profile.Name}’.";
        ActivateButton.IsEnabled = !active && (!SystemProfile(profile) || settings.UseWholeDesktopFolders);
        RenameButton.IsEnabled = !SystemProfile(profile);
        DeleteButton.IsEnabled = !SystemProfile(profile) && settings.Profiles.Count(p => !p.IsOriginalDesktop) > 1;

        SaveButton.IsEnabled = active;
        RestoreButton.IsEnabled = active && FindLayout(profile) != null;
        UndoButton.IsEnabled = undo != null;
        TopologyText.Text = MonitorIdentity.Description(settings.IgnoredMonitors);
        var exactLayout = FindExactLayout(profile);
        var inheritedLayout = exactLayout == null
            ? SmartMonitorLayouts.TryCreate(profile, currentTopology, settings.IgnoredMonitors) : null;
        LayoutStatusText.Text = exactLayout != null
            ? $"Saved for this setup · {exactLayout.Icons.Count} icons · {exactLayout.SavedUtc.ToLocalTime():g}"
            : inheritedLayout != null
                ? $"Inherited · {inheritedLayout.Layout.Icons.Count} positions across {inheritedLayout.MatchedMonitors} matched monitor(s). Save to override."
                : "No compatible layout for this monitor configuration.";
        CopyInheritedButton.IsEnabled = active && !SystemProfile(profile) && exactLayout == null && inheritedLayout != null;
        RefreshWallpaper();
        RefreshShortcuts();
        HotkeyText.Text = string.IsNullOrWhiteSpace(profile.Hotkey) ? "None" : profile.Hotkey;
        HotkeyHint.Text = "Available globally while DeskProfiles is running.";
        string? priorLayoutKey = (SavedLayoutsList.SelectedItem as LayoutRow)?.Key;
        var rows = profile.Layouts.Select(entry =>
        {
            var l = entry.Value;
            bool current = MonitorIdentity.MatchKey(l.MonitorKey, settings.IgnoredMonitors) == currentTopology;
            string label = profile.LayoutNames.TryGetValue(entry.Key, out var name) && !string.IsNullOrWhiteSpace(name)
                ? name : (current ? "Current monitor setup" : "Saved monitor setup");
            return new LayoutRow(entry.Key, (current ? "● " : "") + label,
                $"{MonitorIdentity.DescribeKey(l.MonitorKey)}  ·  {l.Icons.Count} icons  ·  {l.SavedUtc.ToLocalTime():g}");
        }).OrderByDescending(r => r.Key == currentTopology).ThenBy(r => r.Title).ToList();
        SavedLayoutsList.ItemsSource = rows;
        SavedLayoutsList.SelectedItem = rows.FirstOrDefault(r => r.Key == priorLayoutKey);
        UpdateLayoutButtons();
    }


    LayoutRow? SelectedSavedLayout => SavedLayoutsList.SelectedItem as LayoutRow;

    void SavedLayoutsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateLayoutButtons();
    void UpdateLayoutButtons()
    {
        if (LayoutCopyButton == null) return;
        var row = SelectedSavedLayout;
        bool allowed = row != null && !switchingProfile;
        bool isCurrent = allowed && MonitorIdentity.MatchKey(row!.Key, settings.IgnoredMonitors) == currentTopology;
        LayoutCopyButton.IsEnabled = allowed && !isCurrent && ReferenceEquals(Selected, Active);
        LayoutRenameButton.IsEnabled = allowed;
        LayoutDeleteButton.IsEnabled = allowed;
        LayoutResetButton.IsEnabled = allowed && isCurrent;
    }

    void LayoutRename_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedSavedLayout;
        if (row == null || switchingProfile) return;
        var profile = Selected;
        var existing = profile.LayoutNames.TryGetValue(row.Key, out var name) ? name : "";
        var next = Microsoft.VisualBasic.Interaction.InputBox("Name for this monitor layout:", "Rename layout", existing).Trim();
        if (next.Length == 0 || next == existing) return;
        try { profile.LayoutNames[row.Key] = next; Persist(); RefreshDetails(); Log($"Renamed layout to '{next}'."); }
        catch (Exception ex) { Error(ex); }
    }

    void LayoutCopy_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedSavedLayout;
        if (row == null || switchingProfile || !ReferenceEquals(Selected, Active)) return;
        var profile = Selected;
        if (!profile.Layouts.TryGetValue(row.Key, out var source)) return;
        bool replacing = FindExactLayout(profile) != null;
        var prompt = replacing ? "Replace the CURRENT setup's saved layout with a copy of the selected layout?" :
            "Create an independent layout for the CURRENT monitor setup from the selected layout?";
        if (System.Windows.MessageBox.Show(prompt + "\n\nYour source layout will not change.", "Copy monitor layout",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var donor = new Profile();
            donor.Layouts[row.Key] = source;
            var mapped = SmartMonitorLayouts.TryCreate(donor, currentTopology, settings.IgnoredMonitors);
            if (mapped == null || mapped.Layout.Icons.Count == 0)
            {
                Log("Cannot safely map the selected layout to this monitor setup. Nothing copied."); return;
            }
            profile.Layouts[currentTopology] = new Layout { MonitorKey = currentTopology,
                Icons = mapped.Layout.Icons.Select(i => new IconPosition { Pidl = i.Pidl, X = i.X, Y = i.Y }).ToList() };
            Persist(); RefreshDetails();
            Log($"Copied {mapped.Layout.Icons.Count} positions to current monitor setup; source preserved.");
        }
        catch (Exception ex) { Error(ex); }
    }

    void DeleteSavedLayout(string key)
    {
        var profile = Selected;
        if (!profile.Layouts.TryGetValue(key, out var layout)) return;
        if (System.Windows.MessageBox.Show($"Delete saved positions for this monitor setup in '{profile.Name}'?\n\nNo shortcuts or other layouts will be deleted. A settings backup is kept.",
            "Delete saved monitor layout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { profile.Layouts.Remove(key); profile.LayoutNames.Remove(key); Persist(); RefreshDetails();
              Log($"Deleted saved monitor layout ({layout.Icons.Count} icon positions) for '{profile.Name}'."); }
        catch (Exception ex) { profile.Layouts[key] = layout; Error(ex); }
    }
    void LayoutDelete_Click(object sender, RoutedEventArgs e)
    {
        if (switchingProfile || SelectedSavedLayout is not { } row) return;
        DeleteSavedLayout(row.Key);
    }
    void LayoutReset_Click(object sender, RoutedEventArgs e)
    {
        if (switchingProfile || SelectedSavedLayout is not { } row) return;
        if (MonitorIdentity.MatchKey(row.Key, settings.IgnoredMonitors) != currentTopology) return;
        DeleteSavedLayout(row.Key); // Removing the exact override restores automatic inheritance.
    }

    ManagedShortcut? SelectedShortcut => (ShortcutList.SelectedItem as ShortcutRow)?.Item;

    void RefreshShortcuts()
    {
        var selectedId = SelectedShortcut?.Id;
        var profile = Selected;
        ShortcutList.ItemsSource = null;
        ShortcutList.ItemsSource = settings.ManagedShortcuts.Select(item =>
            new ShortcutRow(item, item.Name + (item.IsManaged ? "  [MANAGED]" : "  [Library only]") + (item.Shared ? "  [Shared]" : profile.ShortcutIds.Contains(item.Id) ? "  [This profile]" : "  [Not assigned]"))).ToList();
        ShortcutList.SelectedItem = ShortcutList.Items.Cast<ShortcutRow>().FirstOrDefault(x => x.Item.Id == selectedId);
        ShortcutStatusText.Text = $"{settings.ManagedShortcuts.Count} library items; {settings.ManagedShortcuts.Count(x => x.IsManaged)} managed on Desktop. Unmanaged originals remain visible everywhere.";
        ShortcutSelectionButtons();
        RenderShortcutMatrix();
        RenderOriginalFolders();
        // Folder junction management retired; original Desktop is now a protected profile.
    }

    void RenderOriginalFolders()
    {
        OriginalFoldersPanel.Children.Clear();
        try
        {
            var path = WholeDesktopFolders.OriginalDesktopPath;
            var dirs = Directory.Exists(path) ? Directory.GetDirectories(path).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
            foreach (var dir in dirs)
            {
                var row = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(4, 3, 4, 3) };
                row.Children.Add(new TextBlock { Text = "📁", Width = 27, FontSize = 14 });
                row.Children.Add(new TextBlock { Text = Path.GetFileName(dir), FontWeight = FontWeights.SemiBold, Width = 225, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = dir });
                row.Children.Add(new TextBlock { Text = "Real folder · Original only", Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11 });
                OriginalFoldersPanel.Children.Add(row);
            }
            OriginalFoldersStatus.Text = $"{dirs.Count} real folder(s) at {path}. Open Original Desktop to access them. No junctions or copies are created.";
        }
        catch (Exception ex) { OriginalFoldersStatus.Text = "Unable to list original folders: " + ex.Message; }
    }

    bool updatingShortcutMatrix;
    bool updatingFolderMatrix;

    // Inventory only copies shortcut files into our private catalog; desktop files are untouched.
    void InventoryDesktop()
    {
        var desktop = ManagedDesktop.UserDesktop;
        int discovered = 0;
        foreach (var path in ShortcutLibrary.ScanDesktop())
        {
            if (!string.Equals(Path.GetDirectoryName(path), desktop, StringComparison.OrdinalIgnoreCase)) continue;
            if (settings.ManagedShortcuts.Any(i => i.IsManaged &&
                string.Equals(i.DesktopFileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                if (ShortcutLibrary.AlreadyImported(settings, path)) continue;
                ShortcutLibrary.Import(settings, path);
                discovered++;
            }
            catch (Exception ex) { Diagnostics.Error("Inventory shortcut " + path, ex); }
        }
        int cleaned = CleanupMissingUnmanagedDesktopShortcuts();
        if (discovered > 0 || cleaned > 0) Persist();
        if (discovered > 0) Log($"Found {discovered} existing desktop shortcut(s); left unmanaged.");
        if (cleaned > 0) Log($"[DISCOVERY] Removed {cleaned} stale unmanaged catalog entry/entries.");
    }

    // Never delete managed records: their desktop copies can legitimately be absent in another profile.
    // Also preserve imported shortcuts originating outside the personal desktop.
    int CleanupMissingUnmanagedDesktopShortcuts()
    {
        var missing = settings.ManagedShortcuts.Where(item => !item.IsManaged
            && !string.IsNullOrWhiteSpace(item.SourcePath)
            && string.Equals(Path.GetDirectoryName(item.SourcePath), ManagedDesktop.UserDesktop, StringComparison.OrdinalIgnoreCase)
            && !File.Exists(item.SourcePath)).ToList();
        foreach (var item in missing)
        {
            settings.ManagedShortcuts.Remove(item);
            foreach (var profile in settings.Profiles) profile.ShortcutIds.Remove(item.Id);
            Diagnostics.Info($"[DISCOVERY] Missing shortcut removed from catalog: {item.Name}");
        }
        return missing.Count;
    }

    void ShortcutInventoryRefresh_Click(object sender, RoutedEventArgs e)
    {
        try { InventoryDesktop(); RefreshShortcuts(); }
        catch (Exception ex) { Error(ex); }
    }

    static string ShortcutKind(ManagedShortcut item)
    {
        if (item.DesktopFileName.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return "Web shortcut";
        // WScript.Shell reads shortcut metadata only; it does not execute the target.
        try
        {
            string file = File.Exists(item.ArchivedOriginal) ? item.ArchivedOriginal : item.SourcePath;
            if (!File.Exists(file)) return "App/file shortcut";
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return "App/file shortcut";
            object shell = Activator.CreateInstance(type)!;
            try
            {
                var link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { file });
                if (link == null) return "App/file shortcut";
                try
                {
                    var target = link.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, link, null) as string;
                    return !string.IsNullOrWhiteSpace(target) && Directory.Exists(target) ? "Folder shortcut" : "App/file shortcut";
                }
                finally { if (System.Runtime.InteropServices.Marshal.IsComObject(link)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); }
            }
            finally { if (System.Runtime.InteropServices.Marshal.IsComObject(shell)) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return "App/file shortcut"; }
    }

    void RenderShortcutMatrix()
    {
        if (updatingShortcutMatrix) return;
        updatingShortcutMatrix = true;
        try
        {
            ShortcutMatrix.Children.Clear();
            int count = settings.Profiles.Count;
            // Widths allow horizontal scrolling for lots of profiles.
            var heading = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            heading.Children.Add(new TextBlock { Text = "Item (hover for type / source)", Width = 230, FontWeight = FontWeights.SemiBold });
            foreach (var profile in settings.Profiles.Where(p => !SystemProfile(p)))
                heading.Children.Add(new TextBlock { Text = profile.Name, Width = 96, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold,
                    ToolTip = profile.Name });
            ShortcutMatrix.Children.Add(heading);
            int rowIndex = 0;
            foreach (var item in settings.ManagedShortcuts.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                var line = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0, 0, 0, 2),
                    Background = (rowIndex++ % 2 == 0) ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(29, 40, 56)) : System.Windows.Media.Brushes.Transparent };
                string kind = ShortcutKind(item);
                string status = item.IsManaged ? "Managed" : "Unmanaged · always visible";
                string glyph = kind == "Folder shortcut" ? "▣" : kind == "Web shortcut" ? "◎" : "↗";
                var label = new StackPanel { Orientation = WpfOrientation.Horizontal, Width = 230, Margin = new Thickness(5,3,0,3) };
                label.Children.Add(new TextBlock { Text = glyph, Width = 22, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = System.Windows.Media.Brushes.LightSkyBlue });
                label.Children.Add(new TextBlock { Text = item.Name, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    TextTrimming = TextTrimming.CharacterEllipsis, Width = 194, VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = kind + " · " + status + "\n" + item.SourcePath });
                line.Children.Add(label);
                foreach (var profile in settings.Profiles.Where(p => !SystemProfile(p)))
                {
                    // Unmanaged files stay on Desktop in all profiles, regardless of saved assignments.
                    bool enabled = item.Shared || profile.ShortcutIds.Contains(item.Id);
                    // Indeterminate is intentionally NOT assigned. Unmanaged shortcuts remain visible
                    // until explicitly enrolled; a checked box would incorrectly suggest sharing.
                    var box = new System.Windows.Controls.CheckBox { IsThreeState = false,
                        IsChecked = item.IsManaged && enabled, Width = 96,
                        HorizontalContentAlignment = WpfHorizontalAlignment.Center, HorizontalAlignment = WpfHorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Show {item.Name} in {profile.Name}" };
                    box.Checked += (_, _) => SetShortcutVisibility(item, profile, true);
                    box.Unchecked += (_, _) => SetShortcutVisibility(item, profile, false);
                    line.Children.Add(box);
                }
                ShortcutMatrix.Children.Add(line);
            }
            ShortcutInventoryStatus.Text = $"{settings.ManagedShortcuts.Count} shortcuts · {settings.ManagedShortcuts.Count(i => i.IsManaged)} managed. ↗ app/file · ▣ folder shortcut · ◎ web. Hover for details. Unmanaged shortcuts remain visible until enrolled.";
        }
        finally { updatingShortcutMatrix = false; }
    }

    void FolderInventory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = WholeDesktopFolders.OriginalDesktopPath;
            using var dialog = new Forms.FolderBrowserDialog
            {
                Description = "Select one existing folder directly on your ORIGINAL Desktop to share between profiles.",
                SelectedPath = root,
                ShowNewFolderButton = false
            };
            if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
            var path = Path.GetFullPath(dialog.SelectedPath);
            if (!WholeDesktopFolders.IsSafeOriginalFolder(path, root))
            {
                System.Windows.MessageBox.Show("Only ordinary folders directly inside your original Desktop may be enrolled. Nested folders and existing links are excluded.",
                    "Folder not eligible", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (settings.ManagedFolders.Any(f => string.Equals(f.OriginalPath, path, StringComparison.OrdinalIgnoreCase)))
            { Log("[FOLDERS] This folder is already enrolled."); return; }
            settings.ManagedFolders.Add(new ManagedFolder { Name = Path.GetFileName(path.TrimEnd('\\')), OriginalPath = path, Shared = true });
            Persist(); RenderFolderMatrix();
            Log($"[FOLDERS] Enrolled '{Path.GetFileName(path)}' as shared; applies next switch.");
        }
        catch (Exception ex) { Error(ex); }
    }

    void RenderFolderMatrix()
    {
        if (updatingFolderMatrix) return;
        updatingFolderMatrix = true;
        try
        {
            FolderMatrix.Children.Clear();
            var heading = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0,0,0,6) };
            heading.Children.Add(new TextBlock { Text = "Folder", Width = 230, FontWeight = FontWeights.SemiBold });
            foreach (var profile in settings.Profiles)
                heading.Children.Add(new TextBlock { Text = profile.Name, Width = 96, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.SemiBold });
            FolderMatrix.Children.Add(heading);
            foreach (var folder in settings.ManagedFolders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var line = new StackPanel { Orientation = WpfOrientation.Horizontal, Margin = new Thickness(0,0,0,4) };
                line.Children.Add(new TextBlock { Text = folder.Name + (folder.Shared ? " [Shared]" : ""), Width = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = folder.OriginalPath });
                foreach (var profile in settings.Profiles)
                {
                    var box = new System.Windows.Controls.CheckBox { IsChecked = folder.Shared || profile.FolderIds.Contains(folder.Id),
                        Width = 96, HorizontalContentAlignment = WpfHorizontalAlignment.Center, HorizontalAlignment = WpfHorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Show folder '{folder.Name}' in {profile.Name}" };
                    box.Checked += (_, _) => SetFolderVisibility(folder, profile, true);
                    box.Unchecked += (_, _) => SetFolderVisibility(folder, profile, false);
                    line.Children.Add(box);
                }
                FolderMatrix.Children.Add(line);
            }
            FolderInventoryStatus.Text = $"{settings.ManagedFolders.Count} enrolled folder(s). Original directories stay in place. This applies only in whole-Desktop experiment mode.";
        }
        finally { updatingFolderMatrix = false; }
    }

    void SetFolderVisibility(ManagedFolder folder, Profile profile, bool visible)
    {
        if (updatingFolderMatrix || switchingProfile) return;
        if (folder.Shared)
        {
            folder.Shared = false;
            foreach (var p in settings.Profiles)
                if (!p.FolderIds.Contains(folder.Id)) p.FolderIds.Add(folder.Id);
        }
        if (visible)
        {
            if (!profile.FolderIds.Contains(folder.Id)) profile.FolderIds.Add(folder.Id);
        }
        else profile.FolderIds.Remove(folder.Id);
        if (settings.Profiles.All(p => p.FolderIds.Contains(folder.Id))) folder.Shared = true;
        Persist(); RenderFolderMatrix();
        Log($"[FOLDERS] '{folder.Name}' visibility updated; applies next profile switch.");
    }

    void SetShortcutVisibility(ManagedShortcut item, Profile profile, bool visible)
    {
        if (updatingShortcutMatrix) return;
        if (!item.IsManaged)
        {
            // Imported shortcut from elsewhere can't be enrolled unless its original is on this user's desktop.
            string source = item.SourcePath;
            if (!File.Exists(source) || !string.Equals(Path.GetDirectoryName(source), ManagedDesktop.UserDesktop, StringComparison.OrdinalIgnoreCase))
            {
                Log($"'{item.Name}' is library-only. To change its desktop visibility, place a shortcut on your personal Desktop and refresh.");
                RenderShortcutMatrix();
                return;
            }
            if (System.Windows.MessageBox.Show(
                    $"Manage '{item.Name}'?\n\nDeskProfiles will back up the shortcut and control where it appears. The shortcut target is never moved.\n\nYou can recover managed shortcuts from this page.",
                    "Manage desktop shortcut", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            { RenderShortcutMatrix(); return; }
            try
            {
                // Before enrollment, the actual shortcut is visible everywhere.
                item.Shared = true;
                ManagedDesktop.Enroll(settings, item, source);
            }
            catch (Exception ex) { RenderShortcutMatrix(); Error(ex); return; }
        }
        var oldShared = item.Shared;
        var oldIds = settings.Profiles.ToDictionary(p => p, p => p.ShortcutIds.Contains(item.Id));
        try
        {
            // Turn shared status into explicit per-profile assignments to allow a single edit.
            if (item.Shared)
            {
                item.Shared = false;
                foreach (var p in settings.Profiles)
                    if (!p.ShortcutIds.Contains(item.Id)) p.ShortcutIds.Add(item.Id);
            }
            if (visible)
            {
                if (!profile.ShortcutIds.Contains(item.Id)) profile.ShortcutIds.Add(item.Id);
            }
            else profile.ShortcutIds.Remove(item.Id);
            if (settings.Profiles.Where(p => !SystemProfile(p)).All(p => p.ShortcutIds.Contains(item.Id))) item.Shared = true;
            Persist();
            UpdateManagedShortcuts();
            RenderShortcutMatrix();
            Log($"'{item.Name}' visibility for '{profile.Name}': {(visible ? "On" : "Off")}");
        }
        catch (Exception ex)
        {
            item.Shared = oldShared;
            foreach (var kv in oldIds)
            {
                kv.Key.ShortcutIds.Remove(item.Id);
                if (kv.Value) kv.Key.ShortcutIds.Add(item.Id);
            }
            RenderShortcutMatrix(); Error(ex);
        }
    }

    void ShortcutsRestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show("Restore every managed shortcut to the Desktop and stop managing its visibility? Existing unrelated desktop files will not be overwritten.",
            "Restore managed shortcuts", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        int restored = 0, failed = 0;
        foreach (var item in settings.ManagedShortcuts.Where(i => i.IsManaged).ToList())
        {
            try { ManagedDesktop.Release(settings, item); restored++; }
            catch (Exception ex) { failed++; Diagnostics.Error("Restore " + item.Name, ex); Log($"Could not restore '{item.Name}': {ex.Message}"); }
        }
        RefreshShortcuts();
        Log($"Restored {restored} managed shortcuts; {failed} need manual review.");
    }

    void ShortcutSelectionButtons()
    {
        var item = SelectedShortcut;
        ShortcutAssignButton.IsEnabled = item != null && !item.Shared;
        ShortcutSharedButton.IsEnabled = item != null;
        ShortcutManageButton.IsEnabled = item != null && !item.IsManaged && !string.IsNullOrWhiteSpace(item.SourcePath) && File.Exists(item.SourcePath);
        if (item is null) return;
        ShortcutAssignButton.Content = Selected.ShortcutIds.Contains(item.Id) ? "Remove from this profile" : "Show in this profile";
        ShortcutSharedButton.Content = item.Shared ? "Stop sharing globally" : "Set shared";
    }

    void ShortcutList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShortcutSelectionButtons();

    sealed class ScanEntry
    {
        public bool Selected { get; set; } = true;
        public required string Path { get; init; }
        public string Label => System.IO.Path.GetFileName(Path) + "   —   " + System.IO.Path.GetDirectoryName(Path);
    }

    void ShortcutScan_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var found = ShortcutLibrary.ScanDesktop();
            var entries = found.Where(path => !ShortcutLibrary.AlreadyImported(settings, path))
                .Select(path => new ScanEntry { Path = path }).ToList();
            if (entries.Count == 0)
            {
                Log($"Desktop scan complete: {found.Count} shortcuts found; none new to import.");
                System.Windows.MessageBox.Show("No new desktop shortcuts found. Already-imported shortcuts are excluded.",
                    "Scan desktop", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new Window
            {
                Title = "Scan desktop — select shortcuts",
                Owner = this,
                Width = 690, Height = 495, MinWidth = 450, MinHeight = 330,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 30, 42))
            };
            var panel = new DockPanel { Margin = new Thickness(16) };
            dialog.Content = panel;
            var heading = new TextBlock
            {
                Text = $"Found {found.Count} desktop shortcuts, {entries.Count} not yet imported.\nSelect shortcuts to add to '{Selected.Name}'. Existing desktop items are untouched.",
                Foreground = System.Windows.Media.Brushes.White, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12)
            };
            DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(actions, Dock.Bottom); panel.Children.Add(actions);
            var all = new Button { Content = "Select all", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
            var none = new Button { Content = "Select none", Margin = new Thickness(0, 0, 16, 0), Padding = new Thickness(10, 6, 10, 6) };
            var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6), IsCancel = true };
            var import = new Button { Content = "Import selected", Padding = new Thickness(14, 6, 14, 6), IsDefault = true };
            actions.Children.Add(all); actions.Children.Add(none); actions.Children.Add(cancel); actions.Children.Add(import);
            var list = new StackPanel();
            var checkboxes = entries.Select(entry =>
            {
                var box = new System.Windows.Controls.CheckBox
                {
                    Content = entry.Label, IsChecked = true, Foreground = System.Windows.Media.Brushes.White,
                    Margin = new Thickness(4, 6, 4, 6), Tag = entry
                };
                list.Children.Add(box);
                return box;
            }).ToList();
            all.Click += (_, _) => { foreach (var cb in checkboxes) cb.IsChecked = true; };
            none.Click += (_, _) => { foreach (var cb in checkboxes) cb.IsChecked = false; };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            import.Click += (_, _) => dialog.DialogResult = true;
            panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            if (dialog.ShowDialog() != true) return;

            var selectedPaths = checkboxes.Where(cb => cb.IsChecked == true)
                .Select(cb => ((ScanEntry)cb.Tag).Path).ToList();
            if (selectedPaths.Count == 0) { Log("Scan cancelled: no shortcuts selected."); return; }
            var profile = Selected;
            var imported = new List<ManagedShortcut>();
            var failures = new List<string>();
            foreach (var path in selectedPaths)
            {
                ManagedShortcut? item = null;
                try
                {
                    if (ShortcutLibrary.AlreadyImported(settings, path)) continue;
                    item = ShortcutLibrary.Import(settings, path);
                    profile.ShortcutIds.Add(item.Id);
                    Persist();
                    imported.Add(item);
                }
                catch (Exception ex)
                {
                    if (item != null)
                    {
                        settings.ManagedShortcuts.Remove(item);
                        profile.ShortcutIds.Remove(item.Id);
                        // Delete only the new private copy after its settings write failed.
                        try { File.Delete(ShortcutLibrary.PathFor(item)); } catch { }
                    }
                    failures.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
                    Diagnostics.Error("Scan import failed: " + path, ex);
                }
            }
            RefreshShortcuts();
            Log($"Desktop scan: imported {imported.Count} shortcuts into '{profile.Name}'; {failures.Count} failures. Original desktop items unchanged.");
            if (failures.Count > 0)
                System.Windows.MessageBox.Show(string.Join(Environment.NewLine, failures.Take(10)),
                    "Shortcut import issues", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { Error(ex); }
    }

    void ShortcutImport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import shortcut into DeskProfiles library",
            Filter = "Windows shortcuts (*.lnk;*.url)|*.lnk;*.url",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        ManagedShortcut? added = null;
        try
        {
            added = ShortcutLibrary.Import(settings, dialog.FileName);
            Persist();
            Log($"Imported shortcut '{added.Name}' to private library; original unchanged.");
            RefreshShortcuts();
        }
        catch (Exception ex)
        {
            if (added != null) settings.ManagedShortcuts.Remove(added);
            Error(ex);
        }
    }

    void ShortcutFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Create a library shortcut pointing to this folder (no files moved)" };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        ManagedShortcut? added = null;
        try
        {
            added = ShortcutLibrary.AddFolder(settings, dialog.SelectedPath);
            Persist();
            Log($"Created library shortcut to folder '{dialog.SelectedPath}'; folder unchanged.");
            RefreshShortcuts();
        }
        catch (Exception ex)
        {
            if (added != null) settings.ManagedShortcuts.Remove(added);
            Error(ex);
        }
    }

    void ShortcutAssign_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedShortcut;
        if (item == null || item.Shared) return;
        var profile = Selected;
        bool wasAssigned = profile.ShortcutIds.Contains(item.Id);
        if (wasAssigned) profile.ShortcutIds.Remove(item.Id);
        else profile.ShortcutIds.Add(item.Id);
        try { Persist(); UpdateManagedShortcuts(); RefreshShortcuts(); Log($"'{item.Name}' {(wasAssigned ? "unassigned from" : "assigned to")} '{profile.Name}'."); }
        catch (Exception ex)
        {
            if (wasAssigned) profile.ShortcutIds.Add(item.Id); else profile.ShortcutIds.Remove(item.Id);
            Error(ex);
        }
    }

    void ShortcutShared_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedShortcut;
        if (item == null) return;
        item.Shared = !item.Shared;
        try { Persist(); UpdateManagedShortcuts(); Log($"'{item.Name}' global sharing: {item.Shared}."); RefreshShortcuts(); }
        catch (Exception ex) { item.Shared = !item.Shared; Error(ex); }
    }

    void ShortcutRemove_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedShortcut;
        if (item == null) return;
        if (System.Windows.MessageBox.Show($"Remove '{item.Name}' from the DeskProfiles library?\n\nOriginal files and existing desktop shortcuts will remain unchanged.",
                "Remove catalog entry", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (item.IsManaged)
        {
            try { ManagedDesktop.Release(settings, item); }
            catch (Exception ex) { Error(ex); return; }
        }
        var assigned = settings.Profiles.Where(p => p.ShortcutIds.Contains(item.Id)).ToList();
        settings.ManagedShortcuts.Remove(item);
        foreach (var p in assigned) p.ShortcutIds.Remove(item.Id);
        try { Persist(); RefreshShortcuts(); Log($"Removed '{item.Name}' from catalog (source and library file retained)."); }
        catch (Exception ex)
        {
            settings.ManagedShortcuts.Add(item);
            foreach (var p in assigned) p.ShortcutIds.Add(item.Id);
            Error(ex);
        }
    }

    void UpdateManagedShortcuts()
    {
        if (settings.UseWholeDesktopFolders) return;
        lastShortcutReconcileUtc = DateTime.UtcNow;
        if (!settings.EnableShortcutVisibilitySwitching)
        {
            Diagnostics.Info("[BASELINE] Shortcut reconciliation SKIPPED (disabled in Settings).");
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var lines = ManagedDesktop.Reconcile(settings, Active).ToList();
        foreach (var line in lines) Log(line);
        Diagnostics.Info($"[BASELINE] Shortcut reconciliation: {watch.ElapsedMilliseconds}ms; mode={(settings.UseFolderStaging ? "folder-staging" : "original")}; operations={lines.Count}; managed={settings.ManagedShortcuts.Count(i => i.IsManaged)}");
    }

    void ShortcutManage_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedShortcut;
        if (item == null) return;
        if (System.Windows.MessageBox.Show($"Start managing '{item.Name}'?\n\nDeskProfiles will make a backup of the existing desktop shortcut and then show/hide only this .lnk/.url file based on profile assignments. Its target is untouched.\n\nBackups: %LOCALAPPDATA%\\DeskProfiles\\shortcut-originals", "Manage shortcut", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { ManagedDesktop.Enroll(settings, item, item.SourcePath); UpdateManagedShortcuts(); RefreshShortcuts(); Log($"Enrolled '{item.Name}' for profile visibility."); }
        catch (Exception ex) { Error(ex); }
    }

    void ShortcutSwitching_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        bool previous = settings.EnableShortcutVisibilitySwitching;
        try
        {
            settings.EnableShortcutVisibilitySwitching = ShortcutSwitchingBox.IsChecked == true;
            Persist();
            Log($"[BASELINE] Shortcut visibility switching: {(settings.EnableShortcutVisibilitySwitching ? "ENABLED" : "DISABLED")}; shortcut files unchanged by toggle.");
        }
        catch (Exception ex)
        {
            settings.EnableShortcutVisibilitySwitching = previous;
            ShortcutSwitchingBox.IsChecked = previous;
            Error(ex);
        }
    }

    void FolderStaging_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || switchingProfile) return;
        bool previous = settings.UseFolderStaging;
        try
        {
            settings.UseFolderStaging = FolderStagingBox.IsChecked == true;
            Persist();
            Log($"[FOLDER] Experimental shortcut holding: {(settings.UseFolderStaging ? "ENABLED" : "DISABLED")}; will apply at next switch.");
        }
        catch (Exception ex)
        {
            settings.UseFolderStaging = previous;
            FolderStagingBox.IsChecked = previous;
            Error(ex);
        }
    }

    void WholeDesktop_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        if (switchingProfile) { WholeDesktopBox.IsChecked = settings.UseWholeDesktopFolders; return; }
        bool enable = WholeDesktopBox.IsChecked == true;
        if (enable && !resumingSavedWholeDesktop && System.Windows.MessageBox.Show(
            "EXPERIMENT: Windows will treat a private profile folder as your actual Desktop.\n\n" +
            "Your ORIGINAL desktop folder is left untouched, but its ordinary files and folders temporarily disappear from view. " +
            "Programs using Desktop may write into the experiment folder. Do not use OneDrive Desktop sync. " +
            "DeskProfiles restores the original location when you disable this setting or exit.\n\n" +
            "Backup your Desktop first. Continue?", "System-wide Desktop redirect experiment",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        { WholeDesktopBox.IsChecked = false; return; }
        try
        {
            if (enable)
            {
                // Shut down desktop observers before changing the known folder.
                shortcutWatchTimer.Stop(); shortcutReconcileTimer.Stop();
                desktopWatcher?.Dispose(); desktopWatcher = null;
                if (SystemProfile(Active))
                {
                    // The first activation must write a recovery marker; then navigate back.
                    var first = settings.Profiles.FirstOrDefault(p => !SystemProfile(p)) ?? throw new InvalidOperationException("Create a normal profile first.");
                    WholeDesktopFolders.Switch(settings, first);
                    WholeDesktopFolders.SwitchToOriginal();
                }
                else WholeDesktopFolders.Switch(settings, Active);
                Log("[WHOLE DESKTOP] Activated Desktop folder for '" + Active.Name + "'.");
            }
            else
            {
                WholeDesktopFolders.Restore();
                StartShortcutWatcher(); StartDesktopReconciliation();
                Log("[WHOLE DESKTOP] Original Windows Desktop restored.");
            }
            settings.UseWholeDesktopFolders = enable;
            Persist();
        }
        catch (Exception ex)
        {
            try { WholeDesktopFolders.Restore(); } catch (Exception restoreEx) { Diagnostics.Error("Emergency Desktop restore failed", restoreEx); }
            settings.UseWholeDesktopFolders = false;
            WholeDesktopBox.IsChecked = false;
            Error(ex);
        }
    }

    void RestoreOriginalDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WholeDesktopFolders.Restore();
            settings.UseWholeDesktopFolders = false;
            WholeDesktopBox.IsChecked = false;
            Persist();
            Log("[WHOLE DESKTOP] Original path restored by explicit command.");
        }
        catch (Exception ex) { Error(ex); }
    }

    void AutoManageShortcuts_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        bool enabled = AutoManageShortcutBox.IsChecked == true;
        if (enabled && !settings.AutoManageNewShortcuts && System.Windows.MessageBox.Show(
            "Automatically manage NEW .lnk/.url shortcuts created on your personal Desktop?\n\nExisting shortcuts will stay unmanaged. New shortcuts are backed up, assigned to the active profile, and hidden in other profiles. Real files/folders are never moved.",
            "Automatic shortcut management", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        { AutoManageShortcutBox.IsChecked = false; return; }
        try { settings.AutoManageNewShortcuts = enabled; Persist(); Log($"[DISCOVERY] Auto-manage new shortcuts: {enabled}; visibility switching={settings.EnableShortcutVisibilitySwitching}"); }
        catch (Exception ex) { Error(ex); }
    }

    // Baseline BEFORE watcher starts. Anything already on Desktop is never auto-claimed.
    void StartDesktopReconciliation()
    {
        foreach (var path in EnumeratePersonalDesktopShortcuts()) knownDesktopFiles.Add(path);
        shortcutReconcileTimer.Start();
        Diagnostics.Info($"[DISCOVERY] Baseline {knownDesktopFiles.Count} desktop shortcuts; poll every 2s; autoManage={settings.AutoManageNewShortcuts}; visibility={settings.EnableShortcutVisibilitySwitching}.");
    }

    List<string> EnumeratePersonalDesktopShortcuts()
    {
        try
        {
            return Directory.EnumerateFiles(ManagedDesktop.UserDesktop, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) { Diagnostics.Error("Desktop reconciliation scan", ex); return new List<string>(); }
    }

    void ReconcileDesktopInventory()
    {
        if (settings.UseWholeDesktopFolders) return;
        if (reconcilingDesktop || switchingProfile) return;
        reconcilingDesktop = true;
        try
        {
            var current = EnumeratePersonalDesktopShortcuts().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var created = current.Except(knownDesktopFiles, StringComparer.OrdinalIgnoreCase).ToList();
            var removed = knownDesktopFiles.Except(current, StringComparer.OrdinalIgnoreCase).ToList();
            // Count only stable observations; switching and its async Explorer events are handled separately.
            knownDesktopFiles.Clear();
            knownDesktopFiles.UnionWith(current);
            int stale = CleanupMissingUnmanagedDesktopShortcuts();
            int deletedManaged = CleanupManuallyDeletedManagedShortcuts(current);
            if (stale > 0 || deletedManaged > 0)
            {
                Persist();
                RefreshShortcuts();
                if (stale > 0) Log($"[DISCOVERY] Removed {stale} deleted unmanaged shortcut(s) from the list.");
                if (deletedManaged > 0) Log($"[DISCOVERY] Removed {deletedManaged} manually deleted managed shortcut(s) from the catalog; backup files preserved.");
            }
            if (removed.Count > 0)
                Diagnostics.Info($"[DISCOVERY] Desktop files disappeared={removed.Count}; stale unmanaged={stale} (managed files may be hidden by profiles)");
            foreach (var path in created)
            {
                // The switcher creates managed desktop copies. They are not new shortcuts.
                if (IsAlreadyManagedDesktopCopy(path)) continue;
                Diagnostics.Info($"[DISCOVERY] Poll found new file {Path.GetFileName(path)}; autoManage={settings.AutoManageNewShortcuts}");
                QueueNewShortcut(path);
            }
        }
        catch (Exception ex) { Diagnostics.Error("Desktop reconciliation", ex); }
        finally { reconcilingDesktop = false; }
    }

    // A managed shortcut hidden by switching must never be removed from the catalog.
    // Only a shortcut expected to be visible in the ACTIVE profile may be considered deleted.
    // Two consecutive scans + switch grace period protect asynchronous Explorer updates.
    int CleanupManuallyDeletedManagedShortcuts(HashSet<string> current)
    {
        if (!settings.EnableShortcutVisibilitySwitching ||
            DateTime.UtcNow - lastShortcutReconcileUtc < TimeSpan.FromSeconds(3)) return 0;
        int removed = 0;
        foreach (var item in settings.ManagedShortcuts.Where(i => i.IsManaged).ToList())
        {
            if (string.IsNullOrWhiteSpace(item.DesktopFileName)) continue;
            string path = Path.Combine(ManagedDesktop.UserDesktop, item.DesktopFileName);
            bool shouldBeVisible = item.Shared || Active.ShortcutIds.Contains(item.Id);
            if (!shouldBeVisible || current.Contains(path) || File.Exists(path))
            {
                missingVisibleManagedChecks.Remove(path);
                continue;
            }
            int checks = missingVisibleManagedChecks.GetValueOrDefault(path) + 1;
            missingVisibleManagedChecks[path] = checks;
            if (checks < 2) continue;
            // The desktop file was removed outside DeskProfiles while its profile was active.
            // Only drop ownership references; retain the archived original for recovery.
            settings.ManagedShortcuts.Remove(item);
            foreach (var profile in settings.Profiles) profile.ShortcutIds.Remove(item.Id);
            missingVisibleManagedChecks.Remove(path);
            Diagnostics.Info($"[DISCOVERY] User-deleted visible managed shortcut: {item.DesktopFileName}; archived backup preserved at {item.ArchivedOriginal}");
            removed++;
        }
        return removed;
    }

    bool IsAlreadyManagedDesktopCopy(string path) =>
        settings.ManagedShortcuts.Any(i => i.IsManaged &&
            string.Equals(i.DesktopFileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));

    void StartShortcutWatcher()
    {
        try
        {
            desktopWatcher = new FileSystemWatcher(ManagedDesktop.UserDesktop)
            { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
            desktopWatcher.Created += (_, e) => QueueNewShortcut(e.FullPath);
            desktopWatcher.Renamed += (_, e) => QueueNewShortcut(e.FullPath);
            desktopWatcher.Deleted += (_, e) => Dispatcher.BeginInvoke(() => {
                if (switchingProfile) return;
                int stale = CleanupMissingUnmanagedDesktopShortcuts();
                if (stale > 0) { Persist(); RefreshShortcuts(); Log($"[DISCOVERY] Deleted shortcut removed from catalog: {Path.GetFileName(e.FullPath)}"); }
            });
            desktopWatcher.Error += (_, e) => Diagnostics.Error("Desktop watcher overflow", e.GetException());
            Log("Desktop shortcut watcher running. Only shortcuts created after startup are eligible for auto-management.");
        }
        catch (Exception ex) { Diagnostics.Error("Shortcut watcher unavailable", ex); }
    }

    void QueueNewShortcut(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (IsAlreadyManagedDesktopCopy(path)) return;
            // Capture ownership when discovered, not when a later debounce completes.
            if (!discoveredForProfile.ContainsKey(path)) discoveredForProfile[path] = settings.ActiveProfile;
            newDesktopShortcuts.Add(path);
            Diagnostics.Info($"[DISCOVERY] Queued {Path.GetFileName(path)}; owner={discoveredForProfile[path]}; autoManage={settings.AutoManageNewShortcuts}; visibility={settings.EnableShortcutVisibilitySwitching}");
            if (!shortcutWatchTimer.IsEnabled) shortcutWatchTimer.Start();
        });
    }

    void NewShortcutTimer_Tick(object? sender, EventArgs e)
    {
        if (switchingProfile) return; // Keep pending batch, retry on next tick.
        shortcutWatchTimer.Stop();
        var paths = newDesktopShortcuts.ToList(); newDesktopShortcuts.Clear();
        Diagnostics.Info($"[DISCOVERY] Processing batch={paths.Count}; autoManage={settings.AutoManageNewShortcuts}; visibility={settings.EnableShortcutVisibilitySwitching}");
        foreach (var path in paths)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (!File.Exists(path) || IsAlreadyManagedDesktopCopy(path)) continue;
                var owner = settings.Profiles.FirstOrDefault(p => p.Name == discoveredForProfile.GetValueOrDefault(path)) ?? Active;
                var existing = settings.ManagedShortcuts.FirstOrDefault(i => !i.IsManaged &&
                    !string.IsNullOrWhiteSpace(i.SourcePath) &&
                    string.Equals(Path.GetFullPath(i.SourcePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
                bool imported = false;
                if (existing == null)
                {
                    // Do not rely on content hashes here: different shortcuts can have identical bytes.
                    existing = ShortcutLibrary.Import(settings, path);
                    imported = true;
                }
                if (settings.AutoManageNewShortcuts && settings.EnableShortcutVisibilitySwitching)
                {
                    if (!owner.ShortcutIds.Contains(existing.Id)) owner.ShortcutIds.Add(existing.Id);
                    ManagedDesktop.Enroll(settings, existing, path);
                    UpdateManagedShortcuts();
                    Log($"[DISCOVERY] Automatically assigned '{existing.Name}' to '{owner.Name}'.");
                }
                else
                {
                    Persist();
                    if (imported) Log($"[DISCOVERY] Added unmanaged shortcut '{existing.Name}' to the list (automatic assignment disabled).");
                }
                RefreshShortcuts();
            }
            catch (IOException ex) when (File.Exists(path) && timer.ElapsedMilliseconds < 2000)
            {
                // Windows can expose a shortcut before the creating process finishes writing it.
                Diagnostics.Info($"[DISCOVERY] Retrying busy shortcut {Path.GetFileName(path)}: {ex.Message}");
                newDesktopShortcuts.Add(path);
                if (!shortcutWatchTimer.IsEnabled) shortcutWatchTimer.Start();
                continue;
            }
            catch (Exception ex) { Diagnostics.Error("Auto-manage shortcut " + path, ex); Log("[DISCOVERY] Import failed: " + ex.Message); }
            finally
            {
                if (!newDesktopShortcuts.Contains(path)) discoveredForProfile.Remove(path);
                Diagnostics.Info($"[DISCOVERY] Processed {Path.GetFileName(path)} in {timer.ElapsedMilliseconds}ms");
            }
        }
    }

    void RefreshWallpaper()
    {
        updatingWallpaper = true;
        try
        {
            var profile = Selected;
            UseWallpaperBox.IsChecked = profile.UseCustomWallpaper;
            WallpaperPathBox.Text = profile.WallpaperPath;
            var desired = profile.WallpaperStyle;
            WallpaperStyleBox.SelectedItem = WallpaperStyleBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Content?.ToString(), desired, StringComparison.OrdinalIgnoreCase))
                ?? WallpaperStyleBox.Items[0];
        }
        finally { updatingWallpaper = false; }
    }
    void WallpaperEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (loading || updatingWallpaper) return;
        var profile = Selected;
        var previous = profile.UseCustomWallpaper;
        try
        {
            profile.UseCustomWallpaper = UseWallpaperBox.IsChecked == true;
            Persist();
            Log($"Wallpaper {(profile.UseCustomWallpaper ? "enabled" : "disabled")} for '{profile.Name}'. Apply on activation or with Apply now.");
        }
        catch (Exception ex) { profile.UseCustomWallpaper = previous; RefreshWallpaper(); Error(ex); }
    }
    void WallpaperBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose profile wallpaper",
            Filter = "Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var profile = Selected;
        var oldPath = profile.WallpaperPath;
        var oldEnabled = profile.UseCustomWallpaper;
        try
        {
            profile.WallpaperPath = dialog.FileName;
            profile.UseCustomWallpaper = true;
            Persist(); RefreshWallpaper();
            Log($"Selected wallpaper for '{profile.Name}'. It will be applied on activation or with Apply now.");
        }
        catch (Exception ex) { profile.WallpaperPath = oldPath; profile.UseCustomWallpaper = oldEnabled; RefreshWallpaper(); Error(ex); }
    }
    void WallpaperStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || updatingWallpaper || WallpaperStyleBox.SelectedItem is not ComboBoxItem item) return;
        var profile = Selected;
        var old = profile.WallpaperStyle;
        try { profile.WallpaperStyle = item.Content?.ToString() ?? "Fill"; Persist(); }
        catch (Exception ex) { profile.WallpaperStyle = old; RefreshWallpaper(); Error(ex); }
    }
    void WallpaperApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Selected.UseCustomWallpaper) { Log("Wallpaper disabled for this profile. Windows wallpaper unchanged."); return; }
            if (!ReferenceEquals(Selected, Active))
            {
                Log("Activate this profile before applying its wallpaper.");
                return;
            }
            WallpaperService.Apply(Active);
            Log($"Applied wallpaper for '{Active.Name}'.");
        }
        catch (Exception ex) { Error(ex); }
    }
    void WallpaperClear_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        var oldPath = profile.WallpaperPath;
        var oldEnabled = profile.UseCustomWallpaper;
        try
        {
            profile.WallpaperPath = "";
            profile.UseCustomWallpaper = false;
            Persist(); RefreshWallpaper();
            Log($"Cleared wallpaper preference for '{profile.Name}'. Windows wallpaper unchanged.");
        }
        catch (Exception ex) { profile.WallpaperPath = oldPath; profile.UseCustomWallpaper = oldEnabled; RefreshWallpaper(); Error(ex); }
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

    void SetSwitchStage(string message)
    {
        switchLoadingWindow?.SetStage(message);
    }

    void Activate_Click(object sender, RoutedEventArgs e) => _ = ActivateProfileAsync(Selected);

    // Called from both the UI and global hotkeys. Never allow two switches to overlap.
    void ActivateProfile(Profile profile) => _ = ActivateProfileAsync(profile);

    async System.Threading.Tasks.Task ActivateProfileAsync(Profile profile)
    {
        if (switchingProfile || ReferenceEquals(profile, Active)) return;
        if (SystemProfile(profile) && !settings.UseWholeDesktopFolders)
        {
            Log("Enable the whole-Desktop experiment before activating Original Desktop.");
            return;
        }
        switchingProfile = true;
        monitorTransitionGeneration++; // Cancel any pending monitor restoration before profile activation.
        lastShortcutReconcileUtc = DateTime.UtcNow;
        try
        {
            switchLoadingWindow ??= new SwitchLoadingWindow();
            switchLoadingWindow.Begin(profile.Name);
        }
        catch (Exception ex) { Diagnostics.Error("Could not show switch loader", ex); }
        Log($"Switch started: {Active.Name} -> {profile.Name} ({currentTopology})");
        var previous = settings.ActiveProfile;
        List<IconPosition>? before = null;
        bool committed = false;
        var trace = new SwitchTrace(previous, profile.Name, currentTopology);
        trace.Note($"Managed={settings.ManagedShortcuts.Count(i => i.IsManaged)}; desired={settings.ManagedShortcuts.Count(i => i.IsManaged && (i.Shared || profile.ShortcutIds.Contains(i.Id)))}; savedLayoutIcons={FindLayout(profile)?.Icons.Count ?? 0}");
        try
        {
            // Render the overlay before synchronous Shell calls begin.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            trace.Next("Pre-switch capture and validation");
            var layout = FindLayout(profile);
            if (layout != null && FindExactLayout(profile) == null)
                trace.Note($"Smart monitor inheritance: {layout.Icons.Count} icon position(s) from compatible saved monitor layout(s).");
            if (layout != null)
            {
                before = DesktopIcons.Capture();
                if (before.Count == 0) throw new InvalidOperationException("Cannot capture desktop icon positions; switch cancelled.");
            }
            if (Key() != currentTopology) throw new InvalidOperationException("Monitor configuration is changing; please retry when it settles.");
            settings.ActiveProfile = profile.Name;
            Persist();
            committed = true;
            trace.Next("Shortcut reconcile");
            SetSwitchStage("Updating desktop shortcuts");
            if (settings.UseWholeDesktopFolders)
            {
                var retainedEntries = WholeDesktopFolders.Switch(settings, profile);
                trace.Note("[WHOLE DESKTOP] Redirected Desktop folder and notified Explorer.");
                foreach (var entry in retainedEntries)
                    Log($"[WHOLE DESKTOP] Extra item retained in profile folder: '{entry}'. It may appear in this profile; review it manually if unwanted.");
            }
            else if (!SystemProfile(profile)) UpdateManagedShortcuts();
            // Experimental fast path: a profile with no layout needs no icon positioning.
            // When a layout exists, positioning only needs its eligible icons to be present.
            // Do not wait for unrelated hidden items to disappear before attempting a restore.
            // Fall back to the original conservative readiness poll if required items are missing.
            trace.Next("Explorer readiness");
            SetSwitchStage("Checking Windows Explorer");
            bool ready = true;
            string readinessMode = "not-needed";
            if (layout != null && settings.EnableShortcutVisibilitySwitching && !SystemProfile(profile))
            {
                var hiddenForReadiness = settings.ManagedShortcuts
                    .Where(i => i.IsManaged && !i.Shared && !profile.ShortcutIds.Contains(i.Id))
                    .Select(i => i.DesktopFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
                var required = layout.Icons.Where(i => !hiddenForReadiness.Any(n => DesktopIcons.KeyMatchesFilename(i.Pidl, n))).ToList();
                try
                {
                    var available = DesktopIcons.Capture().Select(i => i.Pidl)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (available.Count > 0 && required.All(i => available.Contains(i.Pidl)))
                        readinessMode = "optimistic-all-target-icons-present";
                    else
                    {
                        readinessMode = "conservative-fallback";
                        SetSwitchStage("Waiting for Windows Explorer");
                        ready = await WaitForManagedShortcutsAsync(profile, TimeSpan.FromSeconds(4));
                    }
                }
                catch (Exception ex)
                {
                    Diagnostics.Error("Optimistic Explorer probe", ex);
                    readinessMode = "conservative-fallback-after-error";
                    ready = await WaitForManagedShortcutsAsync(profile, TimeSpan.FromSeconds(4));
                }
            }
            if (SystemProfile(profile) && layout != null)
            {
                readinessMode = "original-desktop-icons";
                ready = false;
                var until = DateTime.UtcNow + TimeSpan.FromSeconds(4);
                while (DateTime.UtcNow < until)
                {
                    if (Key() != currentTopology) break;
                    var visibleIds = DesktopIcons.Capture().Select(p => p.Pidl).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (layout.Icons.All(p => visibleIds.Contains(p.Pidl))) { ready = true; break; }
                    await System.Threading.Tasks.Task.Delay(100);
                }
            }
            trace.Note($"Explorer ready={ready}; mode={readinessMode}; shortcutVisibilityEnabled={settings.EnableShortcutVisibilitySwitching}");
            if (!ready) Log("Warning: Explorer icon inventory did not settle; only currently visible icons will be positioned.");
            trace.Next("Layout restoration and verification");
            if (layout != null)
            {
                SetSwitchStage("Restoring icon positions");
                // Hidden managed shortcuts may still have historical positions in this saved layout.
                // They are not restoration failures. Never delete those saved entries.
                var hiddenFiles = settings.EnableShortcutVisibilitySwitching && !SystemProfile(profile)
                    ? settings.ManagedShortcuts.Where(i => i.IsManaged && !i.Shared && !profile.ShortcutIds.Contains(i.Id))
                        .Select(i => i.DesktopFileName).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray()
                    : Array.Empty<string>();
                var eligible = layout.Icons.Where(i => !hiddenFiles.Any(n => DesktopIcons.KeyMatchesFilename(i.Pidl, n))).ToList();
                trace.Note($"Layout eligible={eligible.Count}/{layout.Icons.Count}; intentionallyHidden={layout.Icons.Count - eligible.Count}");
                int matched = 0;
                int missing = 0;
                int tries = 0;
                // Only retry icons still visible and out of place. A missing Explorer item
                // cannot be fixed with SelectAndPositionItems until Explorer creates it.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (Key() != currentTopology) { Log("Monitor changed during switch; stopped position restoration."); break; }
                    var current = DesktopIcons.Capture().ToDictionary(i => i.Pidl, StringComparer.OrdinalIgnoreCase);
                    var visible = eligible.Where(i => current.ContainsKey(i.Pidl)).ToList();
                    var wrong = visible.Where(i => Math.Abs(current[i.Pidl].X - i.X) > 2 || Math.Abs(current[i.Pidl].Y - i.Y) > 2).ToList();
                    missing = eligible.Count - visible.Count;
                    matched = visible.Count - wrong.Count;
                    if (wrong.Count == 0) break;
                    tries++;
                    int applied = DesktopIcons.Restore(wrong);
                    trace.Note($"Position attempt {tries}: eligible={eligible.Count} visible={visible.Count} wrong={wrong.Count} applied={applied} missing={missing}");
                    await System.Threading.Tasks.Task.Delay(250);
                }
                // Final verification after the last Explorer update.
                var finalPositions = DesktopIcons.Capture().ToDictionary(i => i.Pidl, StringComparer.OrdinalIgnoreCase);
                matched = eligible.Count(i => finalPositions.TryGetValue(i.Pidl, out var p) && Math.Abs(p.X - i.X) <= 2 && Math.Abs(p.Y - i.Y) <= 2);
                missing = eligible.Count(i => !finalPositions.ContainsKey(i.Pidl));
                // Reuse the just-captured verification snapshot; another Shell enumeration
                // here added work and could disagree if Explorer updated between reads.
                foreach (var line in DesktopIcons.DescribeLayoutDifferences(eligible, finalPositions.Values.ToList())) trace.Note(line);
                trace.Note($"Layout verified={matched}/{eligible.Count}; missingUnexpectedly={missing}; deliberatelyHidden={layout.Icons.Count - eligible.Count}; restoreAttempts={tries}");
                Log($"Layout: {matched}/{eligible.Count} visible expected icons positioned; {missing} unexpectedly missing; {layout.Icons.Count - eligible.Count} intentionally hidden.");
            }
            else Log($"No saved layout for '{profile.Name}' on this monitor setup; icon positions unchanged.");
            trace.Next("Wallpaper");
            SetSwitchStage("Applying wallpaper");
            if (profile.UseCustomWallpaper)
            {
                try { WallpaperService.Apply(profile); }
                catch (Exception ex) { Diagnostics.Error("Wallpaper apply failed", ex); Log("Wallpaper warning: " + ex.Message); }
            }
            undo = before; // Retain PRE-SWITCH snapshot; retries must not replace it.
            ReloadProfiles(profile);
            RefreshDetails();
            // Yield once for WPF's paint queue; avoid an unconditional 350ms pause
            // after the switch operations and layout verification have finished.
            trace.Next("UI completion");
            SetSwitchStage("Finishing");
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Log($"Activated '{profile.Name}'." + (before != null ? " Undo preserves the pre-switch positions." : ""));
        }
        catch (Exception ex)
        {
            trace.Note($"ERROR: {ex.GetType().Name}: {ex.Message}");
            Diagnostics.Error("Profile switch failed", ex);
            if (!committed) settings.ActiveProfile = previous;
            // If already committed, keep the active profile in sync with persisted settings;
            // changing metadata back without reversing shortcut operations is unsafe.
            Log("Profile switch error: " + ex.Message);
            try { ReloadProfiles(Active); } catch { }
            System.Windows.MessageBox.Show(ex.Message + "\n\nSee Activity & Logs for details.", "DeskProfiles", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            trace.Finish();
            switchLoadingWindow?.Hide();
            switchingProfile = false;
            RefreshDetails();
        }
    }

    async System.Threading.Tasks.Task<bool> WaitForManagedShortcutsAsync(Profile profile, TimeSpan timeout)
    {
        var managed = settings.ManagedShortcuts.Where(i => i.IsManaged && !string.IsNullOrWhiteSpace(i.DesktopFileName)).ToList();
        var desired = managed.Where(i => i.Shared || profile.ShortcutIds.Contains(i.Id))
            .Select(i => i.DesktopFileName).ToList();
        var hidden = managed.Where(i => !i.Shared && !profile.ShortcutIds.Contains(i.Id))
            .Select(i => i.DesktopFileName).ToList();
        var start = DateTime.UtcNow;
        int stable = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            if (Key() != currentTopology) return false;
            try
            {
                var icons = DesktopIcons.Capture();
                bool visibleExpected = desired.All(name => icons.Any(i => DesktopIcons.KeyMatchesFilename(i.Pidl, name)));
                bool absentExpected = hidden.All(name => !icons.Any(i => DesktopIcons.KeyMatchesFilename(i.Pidl, name)));
                if (visibleExpected && absentExpected && icons.Count > 0) stable++; else stable = 0;
                if (stable >= 2) return true;
            }
            catch (Exception ex) { Diagnostics.Error("Explorer readiness probe", ex); stable = 0; }
            await System.Threading.Tasks.Task.Delay(150);
        }
        return false;
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        var profile = Selected;
        if (SystemProfile(profile)) return;
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
        if (settings.Profiles.Count(p => !SystemProfile(p)) < 2) return;
        var profile = Selected;
        if (SystemProfile(profile)) return;
        if (System.Windows.MessageBox.Show($"Delete profile '{profile.Name}' and its saved layouts?\n\nDesktop files and icons will NOT be deleted.",
                "Delete profile", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var oldActive = settings.ActiveProfile;
        int index = settings.Profiles.IndexOf(profile);
        try
        {
            settings.Profiles.Remove(profile);
            if (oldActive == profile.Name) settings.ActiveProfile = settings.Profiles[0].Name;
            Persist(); hotkeys?.Sync(settings.Profiles); undo = null; ReloadProfiles(Active); Log($"Deleted profile '{profile.Name}' (saved layouts removed). Desktop files unchanged.");
        }
        catch (Exception ex)
        { settings.Profiles.Insert(index, profile); settings.ActiveProfile = oldActive; ReloadProfiles(profile); Error(ex); }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (switchingProfile) return;
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
        if (switchingProfile) return;
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
        if (switchingProfile) return;
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

    void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (loading) return;
        bool enabled = StartWithWindowsBox.IsChecked == true;
        try
        {
            StartupRegistration.SetEnabled(enabled);
            Log(enabled ? "Start with Windows enabled (launch hidden in tray)." : "Start with Windows disabled.");
        }
        catch (Exception ex)
        {
            StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
            Error(ex);
        }
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
        if (reallyExit) return;
        string topology;
        try { topology = Key(); }
        catch (Exception ex) { Diagnostics.Error("Monitor polling", ex); return; }
        if (topology != pendingTopology)
        {
            pendingTopology = topology;
            pendingSince = DateTime.UtcNow;
            // Invalidate any pending delayed restore as soon as a new display change is seen.
            monitorTransitionGeneration++;
            return;
        }
        if (switchingProfile || topology == currentTopology ||
            DateTime.UtcNow - pendingSince < TimeSpan.FromSeconds(4)) return;
        currentTopology = topology;
        undo = null;
        RefreshMonitors();
        RefreshDetails();
        Log("Stable monitor configuration change detected.");
        if (settings.AutoRestore)
            _ = RestoreAfterMonitorSettlesAsync(topology, ++monitorTransitionGeneration);
    }

    async System.Threading.Tasks.Task RestoreAfterMonitorSettlesAsync(string expectedTopology, int generation)
    {
        // Explorer can move icons AFTER WM_DISPLAYCHANGE and after monitor geometry stabilizes.
        // Sample icon positions before restoring instead of racing Explorer with immediate writes.
        bool Current() => !reallyExit && !switchingProfile && settings.AutoRestore &&
            generation == monitorTransitionGeneration && expectedTopology == currentTopology &&
            Key() == expectedTopology;
        try
        {
            Log($"[MONITOR] Transition #{generation}: waiting for Explorer icon stability; topology={expectedTopology}");
            string? previousSignature = null;
            int consecutive = 0;
            var watch = Stopwatch.StartNew();
            // At least 1.5 seconds of observation, >=3 equal snapshots, bounded at 7 seconds.
            while (watch.Elapsed < TimeSpan.FromSeconds(7))
            {
                await System.Threading.Tasks.Task.Delay(350);
                if (!Current()) { Log($"[MONITOR] Transition #{generation} cancelled (display/profile/settings changed)."); return; }
                var icons = DesktopIcons.Capture();
                if (icons.Count == 0) { consecutive = 0; continue; }
                var signature = string.Join("|", icons.OrderBy(i => i.Pidl, StringComparer.OrdinalIgnoreCase)
                    .Select(i => $"{i.Pidl}:{i.X},{i.Y}"));
                consecutive = signature == previousSignature ? consecutive + 1 : 1;
                previousSignature = signature;
                if (watch.ElapsedMilliseconds >= 1500 && consecutive >= 3) break;
            }
            if (!Current()) return;
            Log($"[MONITOR] Transition #{generation}: observed positions for {watch.ElapsedMilliseconds}ms; stableSamples={consecutive}");
            var layout = FindLayout(Active);
            if (layout == null) { Log($"[MONITOR] No saved or inheritable layout for '{Active.Name}'."); return; }
            // FindLayout constrains ignored and disconnected monitor targets. Log the
            // complete current display geometry because the Shell view uses a
            // virtual-desktop-local coordinate system.
            var virtualOrigin = EligibleMonitorPlacement.VirtualOrigin();
            Log($"[MONITOR] Transition #{generation}: layoutSource={(FindExactLayout(Active) != null ? "exact" : "inherited")}; virtualOrigin=({virtualOrigin.Left},{virtualOrigin.Top}); screens=" +
                string.Join("; ", Forms.Screen.AllScreens.Select(screen =>
                    $"{screen.DeviceName} bounds={screen.Bounds} primary={screen.Primary} ignored={settings.IgnoredMonitors.Contains(screen.DeviceName, StringComparer.OrdinalIgnoreCase)}")));
            var snapshot = DesktopIcons.Capture();
            var applied = DesktopIcons.Restore(layout.Icons);
            undo = snapshot;
            Log($"[MONITOR] Transition #{generation}: restored {applied}/{layout.Icons.Count} icons after Explorer settled.");
            // Post-restore verification also protects against a late Explorer rearrangement.
            for (int check = 1; check <= 3; check++)
            {
                await System.Threading.Tasks.Task.Delay(check == 1 ? 700 : 950);
                if (!Current()) return;
                var actual = DesktopIcons.Capture().ToDictionary(p => p.Pidl, StringComparer.OrdinalIgnoreCase);
                var moved = layout.Icons.Where(p => actual.TryGetValue(p.Pidl, out var now) &&
                    (Math.Abs(now.X - p.X) > 2 || Math.Abs(now.Y - p.Y) > 2)).ToList();
                int missing = layout.Icons.Count(p => !actual.ContainsKey(p.Pidl));
                Log($"[MONITOR] Transition #{generation}: verify #{check}: moved={moved.Count}, missing={missing}");
                if (check == 1 || (check == 3 && moved.Count > 0))
                {
                    foreach (var expected in moved)
                    {
                        var observed = actual[expected.Pidl];
                        var label = expected.Pidl.StartsWith("name:", StringComparison.OrdinalIgnoreCase)
                            ? expected.Pidl[5..] : "legacy:" + expected.Pidl[..Math.Min(18, expected.Pidl.Length)];
                        Log($"[MONITOR] Transition #{generation}: [ICON] MOVED '{label}' target=({expected.X},{expected.Y}) actual=({observed.X},{observed.Y}) delta=({observed.X - expected.X},{observed.Y - expected.Y})");
                    }
                }
                if (moved.Count > 0)
                {
                    DesktopIcons.Restore(moved);
                    Log($"[MONITOR] Transition #{generation}: corrected {moved.Count} moved icon(s) (not saved).");
                }
            }
            Log($"[MONITOR] Transition #{generation}: completed.");
        }
        catch (Exception ex) { Diagnostics.Error("Monitor transition stability restore", ex); }
    }

    void InitializeTrayAndHotkeys()
    {
        try
        {
            hotkeys = new HotkeyManager(this, p => ActivateProfile(p), message => Log(message));
            hotkeys.Sync(settings.Profiles);
            tray = new Forms.NotifyIcon
            {
                Text = "DeskProfiles",
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                ContextMenuStrip = new Forms.ContextMenuStrip()
            };
            tray.DoubleClick += (_, _) => ShowFromTray();
            tray.ContextMenuStrip.Items.Add("Open DeskProfiles", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
            tray.ContextMenuStrip.Items.Add("Exit DeskProfiles", null, (_, _) => Dispatcher.Invoke(() => { reallyExit = true; Close(); }));
            Log("Global shortcuts registered where available. Closing the window minimizes to tray.");
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase)))
            {
                // Wait until tray is created before hiding. The app stays running and hotkeys remain registered.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Hide();
                    ShowInTaskbar = false;
                    Log("Started with Windows; window hidden in system tray.");
                }), DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex) { Error(ex); }
    }

    void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!reallyExit)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        // The switch loader is a standalone WPF Window. Hiding it is not enough:
        // WPF's default shutdown mode waits until *all* windows have closed.
        monitorTransitionGeneration++;
        monitorTimer.Stop();
        shortcutWatchTimer.Stop();
        shortcutReconcileTimer.Stop();
        // On normal exit, restore Windows' original Desktop path BEFORE process shutdown.
        try { WholeDesktopFolders.Restore(); }
        catch (Exception ex)
        {
            Diagnostics.Error("Cannot restore original Windows Desktop on exit", ex);
            System.Windows.MessageBox.Show("Windows Desktop restoration FAILED. DeskProfiles will remain open so you can use 'Restore original Windows Desktop now' in Settings.\n\n" + ex.Message, "Desktop recovery required", MessageBoxButton.OK, MessageBoxImage.Error);
            reallyExit = false;
            e.Cancel = true;
            return;
        }
        desktopWatcher?.Dispose();
        desktopWatcher = null;
        hotkeys?.Dispose();
        hotkeys = null;
        if (tray != null)
        {
            tray.Visible = false;
            tray.Dispose();
            tray = null;
        }
        if (switchLoadingWindow != null)
        {
            switchLoadingWindow.Close();
            switchLoadingWindow = null;
        }
        // Explicitly exit instead of depending on OnLastWindowClose with tray/loader windows.
        Dispatcher.BeginInvoke(new Action(() => System.Windows.Application.Current.Shutdown()),
            DispatcherPriority.ContextIdle);
    }

    void HotkeyRecord_Click(object sender, RoutedEventArgs e)
    {
        recordingProfile = Selected;
        recordingHotkey = true;
        HotkeyRecordButton.Content = "Press keys...";
        HotkeyHint.Text = "Press your shortcut (Esc cancels). Example: Alt + Shift + 1";
        PreviewKeyDown += RecordHotkey_KeyDown;
        Focus();
        Keyboard.Focus(HotkeyRecordButton);
    }

    void StopHotkeyRecording()
    {
        recordingHotkey = false;
        recordingProfile = null;
        PreviewKeyDown -= RecordHotkey_KeyDown;
        HotkeyRecordButton.Content = "Record shortcut";
        RefreshDetails();
    }

    void RecordHotkey_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!recordingHotkey || recordingProfile == null) return;
        e.Handled = true;
        var key = e.Key == WpfKey.System ? e.SystemKey : e.Key;
        if (key == WpfKey.Escape) { StopHotkeyRecording(); return; }
        if (key is WpfKey.LeftShift or WpfKey.RightShift or WpfKey.LeftCtrl or WpfKey.RightCtrl or WpfKey.LeftAlt or WpfKey.RightAlt or WpfKey.LWin or WpfKey.RWin) return;
        var mods = Keyboard.Modifiers;
        if ((mods & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0)
        {
            HotkeyHint.Text = "Include Ctrl, Alt or Win (Shift alone isn't sufficient).";
            return;
        }
        var candidate = HotkeyManager.Format(mods, key);
        var target = recordingProfile;
        var conflict = settings.Profiles.FirstOrDefault(p => !ReferenceEquals(p, target) && p.Hotkey.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        if (conflict != null) { HotkeyHint.Text = $"Already assigned to '{conflict.Name}'. Press another shortcut or Esc."; return; }
        var previous = target.Hotkey;
        try
        {
            // First test the OS registration. On failure, put back the old shortcut.
            target.Hotkey = candidate;
            if (hotkeys != null && !hotkeys.Sync(settings.Profiles, out var failure))
            {
                target.Hotkey = previous;
                hotkeys.Sync(settings.Profiles);
                HotkeyHint.Text = $"Unavailable: {failure}. Press another shortcut or Esc.";
                return;
            }
            Persist();
            Log($"Assigned {candidate} to '{target.Name}'.");
            StopHotkeyRecording();
        }
        catch (Exception ex)
        {
            target.Hotkey = previous;
            hotkeys?.Sync(settings.Profiles);
            StopHotkeyRecording();
            Error(ex);
        }
    }

    void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (recordingHotkey) StopHotkeyRecording();
        var target = Selected;
        var previous = target.Hotkey;
        try
        {
            target.Hotkey = "";
            hotkeys?.Sync(settings.Profiles);
            Persist();
            RefreshDetails();
            Log($"Cleared shortcut for '{target.Name}'.");
        }
        catch (Exception ex) { target.Hotkey = previous; hotkeys?.Sync(settings.Profiles); Error(ex); }
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
