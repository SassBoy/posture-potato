using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PosturePotato;

/// <summary>Settings apply the moment you change them; there is no Save button.</summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private Settings S => _app.Settings;

    public SettingsWindow() : this((App)Application.Current!) { } // previewer

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        UsePlatformChrome();
        ShowValues();

        Potato.UsePortraitFace();
        Opened += async (_, _) =>
        {
            Platform.RoundCorners(this);
            Potato.StartIdle();
            await Task.Delay(350);
            Potato.Wave();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Header.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        if (_app.Scheduler != null)
        {
            _app.Scheduler.Changed += RefreshStatus;
            Closed += (_, _) => _app.Scheduler.Changed -= RefreshStatus;
        }
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Closed += (_, _) =>
        {
            _statusTimer.Stop();
            Potato.StopIdle();
        };
        RefreshStatus();
    }

    /// <summary>
    /// No OS title bar; the header is the drag area. Windows keeps the DWM frame (shadow,
    /// rounded corners). Linux X11 can't extend into the frame, so the window is frameless
    /// and transparent and we draw the rounded card ourselves.
    /// </summary>
    private void UsePlatformChrome()
    {
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = 0;
            return;
        }
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Frame.CornerRadius = new CornerRadius(10);
        Frame.BorderThickness = new Thickness(1);
        AutostartBox.Content = "Start when I log in";
        FooterText.Text = "Closing this keeps me in your tray. Reopen me from your app menu.";
        QuitButton.IsVisible = true; // GNOME has no tray by default, so offer a way out here too
    }

    private void ShowValues()
    {
        IntervalText.Text = Settings.FormatMinutes(S.IntervalMinutes);
        HideText.Text = S.AutoHideSeconds < 60 || S.AutoHideSeconds % 60 != 0
            ? $"{S.AutoHideSeconds} s"
            : Settings.FormatMinutes(S.AutoHideSeconds / 60);
        FullscreenBox.IsChecked = S.QuietDuringFullscreen;
        AwayBox.IsChecked = S.PauseWhenAway;
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    private void RefreshStatus() => StatusText.Text = _app.Scheduler?.Status ?? "";

    private void Apply()
    {
        Saved(_app.SettingsChanged()); // clamps, saves, reschedules
        ShowValues();
    }

    private void Saved(bool ok)
    {
        if (!ok) StatusText.Text = "Couldn't save settings (see startup.log)";
    }

    private void IntervalDown_Click(object? sender, RoutedEventArgs e) { S.IntervalMinutes -= 5; Apply(); }
    private void IntervalUp_Click(object? sender, RoutedEventArgs e) { S.IntervalMinutes += 5; Apply(); }
    private void HideDown_Click(object? sender, RoutedEventArgs e) { S.AutoHideSeconds -= 15; Apply(); }
    private void HideUp_Click(object? sender, RoutedEventArgs e) { S.AutoHideSeconds += 15; Apply(); }

    private void Toggle_Click(object? sender, RoutedEventArgs e)
    {
        S.QuietDuringFullscreen = FullscreenBox.IsChecked == true;
        S.PauseWhenAway = AwayBox.IsChecked == true;
        Apply();
    }

    private void Autostart_Click(object? sender, RoutedEventArgs e)
    {
        bool on = AutostartBox.IsChecked == true;
        try
        {
            Autostart.Set(on); // an explicit "on" also clears a disable set in the OS startup settings
            Log.Write($"start at login turned {(on ? "on" : "off")} in Settings");
            S.StartAtLogin = on;
            Saved(_app.SettingsChanged());
        }
        catch (Exception ex) { StatusText.Text = $"Couldn't change startup: {ex.Message}"; }
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    private void CheckNow_Click(object? sender, RoutedEventArgs e) => _app.ShowReminder();
    private void Quit_Click(object? sender, RoutedEventArgs e) => _app.Quit();
    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
