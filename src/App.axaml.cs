using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Win32;

namespace PosturePotato;

public partial class App : Application
{
    private Settings _settings = new();
    private Tray? _tray;
    private ReminderWindow? _reminder;
    private SettingsWindow? _settingsWindow;

    public Scheduler Scheduler { get; private set; } = null!;
    public Settings Settings => _settings;

    private IClassicDesktopStyleApplicationLifetime? Desktop => ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (Desktop is not { } desktop) return; // previewer
        var args = desktop.Args ?? Array.Empty<string>();

        Dispatcher.UIThread.UnhandledException += (_, e) => Log.Write($"crash (UI thread): {e.Exception}");
        desktop.ShutdownRequested += (_, _) => Log.Write("shutdown requested (signing out or shutting down)");
        desktop.Exit += (_, e) => Log.Write($"exit code={e.ApplicationExitCode}");
        if (OperatingSystem.IsWindows())
            SystemEvents.SessionEnding += (_, e) => Log.Write($"session ending ({e.Reason})");
        SingleInstance.ShowRequested += () => Dispatcher.UIThread.Post(OpenSettings);

        _settings = Settings.Load(out bool firstRun);
        Log.Write(firstRun ? $"first run: no settings yet, using defaults ({Settings.FilePath})"
                           : $"settings loaded: every {_settings.IntervalMinutes} min, start at login {(_settings.StartAtLogin ? "on" : "off")}");
        if (firstRun) _settings.Save();
        Autostart.Sync(_settings.StartAtLogin);
        Launcher.Ensure();
        Platform.Init();

        Scheduler = new Scheduler(_settings);
        Scheduler.Due += ShowReminder;
        Scheduler.WentAway += () => _reminder?.Dismiss();
        try { _tray = new Tray(this); }
        catch (Exception ex) { Log.Write($"tray icon unavailable: {ex.GetType().Name}: {ex.Message}"); }
        Scheduler.Changed += () => _tray?.Refresh();
        Scheduler.Start();

        if (firstRun) OpenSettings();
        _tray?.Refresh();
        TrimWhenIdle();
        if (args.Contains("--check-now")) ShowReminder();
        if (args.Contains("--settings")) OpenSettings();
    }

    public void ShowReminder()
    {
        if (_reminder != null) return;
        Log.Write("reminder shown");
        Scheduler.Showing = true;
        _reminder = new ReminderWindow(_settings);
        _reminder.Finished += result =>
        {
            Log.Write(result == ReminderResult.Done ? "reminder answered" : "reminder hidden unanswered");
            Scheduler.Showing = false;
            _reminder = null;
            Scheduler.Reset(); // answered or missed, the next check is one interval away
        };
        _reminder.Closed += (_, _) => TrimWhenIdle();
        _reminder.Show();
        _tray?.Refresh();
    }

    private async void TrimWhenIdle()
    {
        await Task.Delay(1500);
        if (_reminder == null && _settingsWindow == null) Platform.TrimMemory();
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => { _settingsWindow = null; TrimWhenIdle(); };
            _settingsWindow.Show();
        }
        _settingsWindow.Activate();
    }

    public bool SettingsChanged()
    {
        bool saved = _settings.Save();
        Scheduler.IntervalChanged();
        return saved;
    }

    public void Quit()
    {
        Log.Write("quit");
        _reminder?.Dismiss();
        Scheduler?.Dispose();
        _tray?.Dispose();
        _tray = null;
        Desktop?.Shutdown();
    }
}
