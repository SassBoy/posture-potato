using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PosturePotato;

/// <summary>Settings apply the moment you change them; there is no Save button.</summary>
public partial class SettingsWindow : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private Settings S => _app.Settings;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        ShowValues();

        SourceInitialized += (_, _) => Native.RoundCorners(new WindowInteropHelper(this).Handle);
        Potato.UsePortraitFace();
        Loaded += async (_, _) =>
        {
            Potato.StartIdle();
            await Task.Delay(350);
            Potato.Wave();
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        _app.Scheduler.Changed += RefreshStatus;
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Closed += (_, _) =>
        {
            _app.Scheduler.Changed -= RefreshStatus;
            _statusTimer.Stop();
            Potato.StopIdle();
        };
        RefreshStatus();
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

    private void RefreshStatus() => StatusText.Text = _app.Scheduler.Status;

    private void Apply()
    {
        _app.SettingsChanged(); // clamps, saves, reschedules
        ShowValues();
    }

    private void IntervalDown_Click(object sender, RoutedEventArgs e) { S.IntervalMinutes -= 5; Apply(); }
    private void IntervalUp_Click(object sender, RoutedEventArgs e) { S.IntervalMinutes += 5; Apply(); }
    private void HideDown_Click(object sender, RoutedEventArgs e) { S.AutoHideSeconds -= 15; Apply(); }
    private void HideUp_Click(object sender, RoutedEventArgs e) { S.AutoHideSeconds += 15; Apply(); }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        S.QuietDuringFullscreen = FullscreenBox.IsChecked == true;
        S.PauseWhenAway = AwayBox.IsChecked == true;
        Apply();
    }

    private void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(AutostartBox.IsChecked == true); }
        catch (Exception ex) { StatusText.Text = $"Couldn't change startup: {ex.Message}"; }
        AutostartBox.IsChecked = Autostart.IsEnabled;
    }

    private void CheckNow_Click(object sender, RoutedEventArgs e) => _app.ShowReminder();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
