using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace PosturePotato;

/// <summary>
/// The tray icon. Windows: the taskbar's hidden icons. Linux: any StatusNotifier tray
/// (KDE, Xfce, Cinnamon, GNOME with the AppIndicator extension). Click: Settings. Menu: actions.
/// </summary>
internal sealed class Tray : IDisposable
{
    private readonly App _app;
    private readonly TrayIcon _icon;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(30) };
    private NativeMenuItem _status = new();
    private bool _builtPaused;

    public Tray(App app)
    {
        _app = app;
        var image = OperatingSystem.IsWindows()
            ? IconFactory.BuildIco(IconFactory.TraySizes) // the shell picks the size for the DPI
            : IconFactory.RenderPng(64);
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(new MemoryStream(image)),
            ToolTipText = "Posture Potato",
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => _app.OpenSettings();
        Rebuild();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _icon });

        _refresh.Tick += (_, _) => Refresh();
        _refresh.Start();
    }

    public void Refresh()
    {
        var s = _app.Scheduler;
        if (_builtPaused != s.IsPaused) Rebuild();
        _status.Header = s.Status;
        var text = "Posture Potato\n" + s.Status;
        _icon.ToolTipText = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>The menu's shape changes with pause state, so it's rebuilt rather than patched.</summary>
    private void Rebuild()
    {
        var s = _app.Scheduler;
        _builtPaused = s.IsPaused;
        _status = new NativeMenuItem(s.Status) { IsEnabled = false };

        var menu = new NativeMenu();
        menu.Add(_status);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Check now", _app.ShowReminder));
        if (s.IsPaused)
        {
            menu.Add(Item("Resume reminders", s.Resume));
        }
        else
        {
            var pause = new NativeMenu();
            pause.Add(Item("For 30 min", () => s.PauseFor(TimeSpan.FromMinutes(30))));
            pause.Add(Item("For 1 hour", () => s.PauseFor(TimeSpan.FromHours(1))));
            pause.Add(Item("Until tomorrow", s.PauseUntilTomorrow));
            menu.Add(new NativeMenuItem("Pause reminders") { Menu = pause });
        }
        menu.Add(Item("Settings", _app.OpenSettings));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Quit Posture Potato", _app.Quit));
        menu.Opening += (_, _) => _status.Header = _app.Scheduler.Status;
        _icon.Menu = menu;
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _refresh.Stop();
        _icon.IsVisible = false;
        _icon.Dispose();
    }
}
