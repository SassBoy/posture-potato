using System.Threading;
using System.Windows;
using System.Windows.Media.Animation;

namespace PosturePotato;

public partial class App : Application
{
    private const string MutexName = @"Local\PosturePotato.Instance";
    private const string ShowEventName = @"Local\PosturePotato.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private Settings _settings = null!;
    private TrayIcon? _tray;
    private ReminderWindow? _reminder;
    private SettingsWindow? _settingsWindow;

    public Scheduler Scheduler { get; private set; } = null!;
    public Settings Settings => _settings;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write($"crash: {a.ExceptionObject}");
        DispatcherUnhandledException += (_, a) => Log.Write($"crash (UI thread): {a.Exception}");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, a) => Log.Write($"unobserved task error: {a.Exception}");
        SessionEnding += (_, a) => Log.Write($"session ending ({a.ReasonSessionEnding})");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build helper: write the app icon from the drawn potato.
        if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
        {
            System.IO.File.WriteAllBytes(e.Args[1], IconFactory.BuildIco(IconFactory.AppSizes));
            Shutdown();
            return;
        }

        Log.Trim();
        Log.Write($"start  args=[{string.Join(' ', e.Args)}]  cwd={Environment.CurrentDirectory}  " +
                  $"since boot={TimeSpan.FromMilliseconds(Environment.TickCount64):h\\:mm\\:ss}");

        // Mascot motion doesn't need 60 fps; halves the cost of the idle animation.
        Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline),
            new FrameworkPropertyMetadata { DefaultValue = 30 });

        // Single instance: a second launch just opens Settings in the running one.
        _mutex = new Mutex(true, MutexName, out bool first);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            Log.Write("already running: asked that instance to open Settings, exiting");
            _showEvent.Set();
            Shutdown();
            return;
        }
        new Thread(() => { while (_showEvent.WaitOne()) Dispatcher.BeginInvoke(OpenSettings); })
            { IsBackground = true }.Start();

        _settings = Settings.Load(out bool firstRun);
        Log.Write(firstRun ? "first run: no settings yet, using defaults"
                           : $"settings loaded: every {_settings.IntervalMinutes} min, start with Windows {(_settings.StartWithWindows ? "on" : "off")}");
        if (firstRun) _settings.Save();
        Autostart.Sync(_settings.StartWithWindows);
        StartMenu.Ensure();

        Scheduler = new Scheduler(_settings);
        Scheduler.Due += ShowReminder;
        Scheduler.WentAway += () => _reminder?.Dismiss();
        _tray = new TrayIcon(this);
        Scheduler.Changed += () => _tray?.Refresh();
        Scheduler.Start();

        if (firstRun)
        {
            _tray.ShowHello();
            OpenSettings();
        }
        _tray.Refresh();
        TrimWhenIdle();
        if (e.Args.Contains("--check-now")) ShowReminder();
        if (e.Args.Contains("--show-menu")) _tray.ShowMenu(); // test helper
    }

    public void ShowReminder()
    {
        if (_reminder != null) return;
        Scheduler.Showing = true;
        _reminder = new ReminderWindow(_settings);
        _reminder.Finished += _ =>
        {
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
        await System.Threading.Tasks.Task.Delay(1500);
        if (_reminder == null && _settingsWindow == null) Native.TrimMemory();
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
        Log.Write("quit from the tray menu");
        _reminder?.Dismiss();
        Scheduler?.Dispose();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_settings != null) Log.Write($"exit code={e.ApplicationExitCode}"); // skips the --export-icon helper
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
