using Avalonia.Threading;
using Microsoft.Win32;

namespace PosturePotato;

/// <summary>
/// Decides when the potato pops up. Keeps an absolute due time and checks it on a slow tick,
/// so sleep, lock and clock changes can't stretch or skip a reminder.
/// </summary>
public sealed class Scheduler : IDisposable
{
    private static readonly TimeSpan AwayAfter = TimeSpan.FromMinutes(5);
    /// <summary>A gap this long between 15 s ticks means the PC was asleep.</summary>
    private static readonly TimeSpan SleptAfter = TimeSpan.FromMinutes(2);

    private readonly Settings _settings;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _lastResetUtc, _lastTickUtc;
    private bool _locked, _away;

    public DateTime NextDueUtc { get; private set; }
    public DateTime? PausedUntilUtc { get; private set; }
    public bool IsPaused => PausedUntilUtc is not null;
    /// <summary>Set by the app while a reminder is on screen.</summary>
    public bool Showing { get; set; }

    public event Action? Due;
    public event Action? Changed;
    /// <summary>Raised when the session locks or the PC sleeps: hide anything on screen.</summary>
    public event Action? WentAway;

    public Scheduler(Settings settings)
    {
        _settings = settings;
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Reset();
        _lastTickUtc = DateTime.UtcNow;
        _timer.Start();
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (OperatingSystem.IsWindows())
        {
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
    }

    // ----- tray actions -----

    public void PauseFor(TimeSpan span) => PauseUntil(DateTime.UtcNow + span);

    public void PauseUntilTomorrow()
    {
        var morning = DateTime.Today.AddHours(6);
        if (morning <= DateTime.Now) morning = morning.AddDays(1);
        PauseUntil(morning.ToUniversalTime());
    }

    private void PauseUntil(DateTime utc)
    {
        PausedUntilUtc = utc;
        Changed?.Invoke();
    }

    public void Resume()
    {
        PausedUntilUtc = null;
        Reset();
    }

    /// <summary>Interval changed in settings: keep the time already waited.</summary>
    public void IntervalChanged()
    {
        var due = _lastResetUtc.AddMinutes(_settings.IntervalMinutes);
        var soonest = DateTime.UtcNow.AddMinutes(1);
        NextDueUtc = due < soonest ? soonest : due;
        Changed?.Invoke();
    }

    // ----- status text for tray + settings -----

    public string Status
    {
        get
        {
            if (PausedUntilUtc is { } p)
            {
                var local = p.ToLocalTime();
                return local.Date == DateTime.Today ? $"Paused until {local:t}" : $"Paused until tomorrow {local:t}";
            }
            if (_locked) return "Paused while your PC is locked";
            if (_away) return "Waiting for you to come back";
            if (Showing) return "Checking in now";
            var mins = (int)Math.Ceiling((NextDueUtc - DateTime.UtcNow).TotalMinutes);
            var when = mins <= 1 ? "in a minute" : mins < 60 ? $"in {mins} min" : $"at {NextDueUtc.ToLocalTime():t}";
            return $"Next check {when}";
        }
    }

    // ----- internals -----

    /// <summary>Start a fresh interval from now.</summary>
    public void Reset()
    {
        _lastResetUtc = DateTime.UtcNow;
        NextDueUtc = _lastResetUtc.AddMinutes(_settings.IntervalMinutes);
        Changed?.Invoke();
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        bool slept = now - _lastTickUtc > SleptAfter;
        _lastTickUtc = now;

        // Linux reports lock state by polling; Windows sends events (see below).
        if (Platform.IsLocked is bool locked)
        {
            if (locked && !_locked) GoAway();
            else if (!locked && _locked) ComeBack();
        }
        if (slept && !_locked)
        {
            // Woke from sleep without a lock screen: like coming back to the desk.
            _away = false;
            Reset();
            return;
        }

        if (_locked || Showing) return;

        if (PausedUntilUtc is { } until)
        {
            if (now < until) return;
            Resume();
            return;
        }

        if (_settings.PauseWhenAway && Platform.IdleTime is { } idleFor)
        {
            bool idle = idleFor >= AwayAfter;
            if (idle && !_away) { _away = true; Changed?.Invoke(); }
            if (idle) return;
            if (_away) { _away = false; Reset(); return; } // you got up and moved: that counts
        }

        if (now < NextDueUtc) return;
        if (_settings.QuietDuringFullscreen && Platform.IsUserBusy) return; // retry next tick
        Due?.Invoke();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                OnUi(GoAway);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                OnUi(ComeBack);
                break;
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) OnUi(GoAway);
        else if (e.Mode == PowerModes.Resume) OnUi(ComeBack);
    }

    private void GoAway()
    {
        _locked = true;
        WentAway?.Invoke();
        Changed?.Invoke();
    }

    private void ComeBack()
    {
        if (!_locked) return;
        _locked = false;
        _away = false;
        Reset();
    }

    private static void OnUi(Action a) => Dispatcher.UIThread.Post(a);
}
