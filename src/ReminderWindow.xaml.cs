using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PosturePotato;

public enum ReminderResult { Done, Missed }

/// <summary>The overlay card. Never takes keyboard focus; mouse only.</summary>
public partial class ReminderWindow : Window
{
    private static readonly string[] Tips =
    {
        "Feet flat, shoulders loose, screen at eye level.",
        "Sit back so the chair holds your lower back.",
        "Ears over shoulders, chin tucked a little.",
        "Drop your shoulders and unclench your jaw.",
        "Elbows near 90°, wrists straight.",
        "Hips back in the seat, knees level with them.",
        "Look across the room for 20 seconds.",
    };
    private static int _tipIndex = new Random().Next(Tips.Length);

    private readonly Settings _settings;
    private AnimationClock? _countdown;
    private bool _answered;

    public event Action<ReminderResult>? Finished;

    public ReminderWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();

        TipText.Text = Tips[_tipIndex++ % Tips.Length];
        Potato.PrepareEntrance();

        SourceInitialized += (_, _) =>
        {
            Native.MakeOverlay(new WindowInteropHelper(this).Handle);
            Place();
        };
        ContentRendered += (_, _) => { Place(); _ = EnterAsync(); };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Card.MouseEnter += (_, _) => _countdown?.Controller?.Pause();
        Card.MouseLeave += (_, _) => _countdown?.Controller?.Resume();
    }

    /// <summary>Bottom-right corner of the monitor you're working on, without activating.</summary>
    private void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var work = Native.ActiveWorkArea();
        Native.GetWindowRect(hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        int margin = (int)(6 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, work.Right - w - margin, work.Bottom - h, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private async Task EnterAsync()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        CardShift.Y = 18;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
        CardShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        await Task.Delay(140);
        await Potato.PlayEntranceAsync(slouched: true);
        Potato.StartIdle();
        StartCountdown();
        await Task.Delay(250);
        if (!_answered) Potato.Wave();
    }

    private void StartCountdown()
    {
        var a = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(_settings.AutoHideSeconds));
        _countdown = a.CreateClock();
        _countdown.Completed += (_, _) => { if (_countdown.CurrentProgress >= 1) Answer(ReminderResult.Missed); };
        CountdownScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _countdown);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Answer(ReminderResult.Done);

    private async void Answer(ReminderResult result)
    {
        if (_answered) return;
        _answered = true;
        _countdown?.Controller?.Pause();
        Actions.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        Actions.BeginAnimation(OpacityProperty, fade);
        CountdownTrack.BeginAnimation(OpacityProperty, fade);

        if (result == ReminderResult.Done)
        {
            TitleText.Text = "Looking good";
            TipText.Text = $"Next check in {Settings.FormatMinutes(_settings.IntervalMinutes)}.";
            await Potato.CelebrateAsync();
            await Task.Delay(700);
        }

        Finished?.Invoke(result);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { BeginTime = TimeSpan.FromMilliseconds(120) });
        await Potato.PlayExitAsync();
        await Task.Delay(150);
        Close();
    }

    /// <summary>Hide at once (PC locked, app quitting).</summary>
    public void Dismiss()
    {
        if (_answered) return;
        _answered = true;
        Finished?.Invoke(ReminderResult.Missed);
        Close();
    }
}
