using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

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
    private readonly TranslateTransform _cardShift = new();
    private readonly ScaleTransform _countdownScale = new();
    private Countdown? _countdown;
    private bool _answered, _entered;

    public event Action<ReminderResult>? Finished;

    public ReminderWindow() : this(new Settings()) { } // previewer

    public ReminderWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        Card.RenderTransform = _cardShift;
        CountdownBar.RenderTransform = _countdownScale;

        TipText.Text = Tips[_tipIndex++ % Tips.Length];
        Potato.PrepareEntrance();

        Opened += (_, _) =>
        {
            Platform.MakeOverlay(this);
            Place();
            // Wait one layout pass so SizeToContent has settled before the entrance.
            Dispatcher.UIThread.Post(() => { Place(); if (!_entered) { _entered = true; _ = EnterAsync(); } },
                DispatcherPriority.Background);
        };
        ScalingChanged += (_, _) => Dispatcher.UIThread.Post(Place);
        PropertyChanged += (_, e) => { if (e.Property == ClientSizeProperty) Place(); };
        Card.PointerEntered += (_, _) => _countdown?.Pause();
        Card.PointerExited += (_, _) => _countdown?.Resume();
    }

    /// <summary>Bottom-right corner of the monitor you're working on.</summary>
    private void Place()
    {
        var area = Platform.WorkArea(this);
        double scale = RenderScaling;
        var size = PixelSize.FromSize(ClientSize, scale);
        int margin = (int)(6 * scale);
        var pos = new PixelPoint(area.Right - size.Width - margin, area.Bottom - size.Height);
        if (Position != pos) Position = pos;
    }

    private async Task EnterAsync()
    {
        _cardShift.Y = 18;
        Tween.Run(TimeSpan.FromMilliseconds(220), v => Root.Opacity = v);
        Tween.Run(TimeSpan.FromMilliseconds(260), v => _cardShift.Y = 18 * (1 - v), Tween.CubicOut);

        await Task.Delay(140);
        await Potato.PlayEntranceAsync(slouched: true);
        Potato.StartIdle();
        StartCountdown();
        await Task.Delay(250);
        if (!_answered) Potato.Wave();
    }

    private void StartCountdown()
    {
        _countdown = new Countdown(TimeSpan.FromSeconds(_settings.AutoHideSeconds));
        _countdown.Progress += left => _countdownScale.ScaleX = left;
        _countdown.Elapsed += () => Answer(ReminderResult.Missed);
        _countdown.Start();
    }

    private void Done_Click(object? sender, RoutedEventArgs e) => Answer(ReminderResult.Done);

    private async void Answer(ReminderResult result)
    {
        if (_answered) return;
        _answered = true;
        _countdown?.Stop();
        Actions.IsHitTestVisible = false;
        Tween.Run(TimeSpan.FromMilliseconds(180), v => { Actions.Opacity = 1 - v; CountdownTrack.Opacity = 1 - v; });

        if (result == ReminderResult.Done)
        {
            TitleText.Text = "Looking good";
            TipText.Text = $"Next check in {Settings.FormatMinutes(_settings.IntervalMinutes)}.";
            await Potato.CelebrateAsync();
            await Task.Delay(700);
        }

        Finished?.Invoke(result);
        Tween.Run(TimeSpan.FromMilliseconds(380), v => Root.Opacity = 1 - v, delay: TimeSpan.FromMilliseconds(120));
        await Potato.PlayExitAsync();
        await Task.Delay(150);
        Close();
    }

    /// <summary>Hide at once (PC locked, app quitting).</summary>
    public void Dismiss()
    {
        if (_answered) return;
        _answered = true;
        _countdown?.Stop();
        Finished?.Invoke(ReminderResult.Missed);
        Close();
    }
}
