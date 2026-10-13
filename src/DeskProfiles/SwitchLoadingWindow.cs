using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace DeskProfiles;

// Independent from MainWindow: stays visible for hotkey/tray switches when
// MainWindow is hidden or minimized. No Owner is set intentionally.
internal sealed class SwitchLoadingWindow : Window
{
    readonly TextBlock title;
    readonly TextBlock stage;

    internal SwitchLoadingWindow()
    {
        Title = "DeskProfiles — Switching";
        Width = 390;
        Height = 175;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = WpfBrushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var border = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(85, 113, 149)),
            Background = new SolidColorBrush(WpfColor.FromRgb(34, 48, 68)),
            Padding = new Thickness(25)
        };
        var content = new StackPanel();
        title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = WpfBrushes.White, Margin = new Thickness(0, 0, 0, 13) };
        stage = new TextBlock { FontSize = 13, Foreground = new SolidColorBrush(WpfColor.FromRgb(201, 215, 232)),
            Margin = new Thickness(0, 0, 0, 15) };
        var progress = new WpfProgressBar { Height = 7, IsIndeterminate = true };
        content.Children.Add(title);
        content.Children.Add(stage);
        content.Children.Add(progress);
        border.Child = content;
        Content = border;
    }

    internal void Begin(string profileName)
    {
        title.Text = $"Switching to {profileName}…";
        SetStage("Preparing desktop");
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Top + (work.Height - Height) / 2;
        if (!IsVisible) Show();
        else Topmost = true;
    }

    internal void SetStage(string text) => stage.Text = text;
}
