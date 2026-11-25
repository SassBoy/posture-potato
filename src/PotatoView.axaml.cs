using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PosturePotato;

/// <summary>
/// The pixel-art mascot. Runs its own 12 fps frame clock while on screen; windows only call verbs.
/// Every motion moves in whole art pixels so the sprite never smears.
/// </summary>
public partial class PotatoView : UserControl
{
    public static readonly StyledProperty<int> ArtScaleProperty =
        AvaloniaProperty.Register<PotatoView, int>(nameof(ArtScale), 3);

    /// <summary>Screen pixels per art pixel at 100% scaling (rounded per monitor DPI).</summary>
    public int ArtScale
    {
        get => GetValue(ArtScaleProperty);
        set => SetValue(ArtScaleProperty, value);
    }

    static PotatoView()
    {
        ArtScaleProperty.Changed.AddClassHandler<PotatoView>((v, _) => v.UpdateSize());
    }

    private readonly WriteableBitmap _bmp = new(new PixelSize(PotatoArt.W, PotatoArt.H), new Vector(96, 96),
        PixelFormat.Bgra8888, AlphaFormat.Unpremul);
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 12) };
    private readonly List<(long due, TaskCompletionSource tcs)> _waiters = new();
    private readonly Random _rng = new();
    private TopLevel? _top;

    private PotatoPose _pose = new() { Eyes = EyeState.Open, Mouth = MouthState.Meh };
    private long _t;
    private bool _idle;
    private int _squashFx, _blinkLeft, _waveLeft, _sparkleLeft;
    private long _nextBlink;

    public PotatoView()
    {
        InitializeComponent();
        Img.Source = _bmp;
        _clock.Tick += (_, _) => Tick();
        UpdateSize();
        Render();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        if (_top != null) _top.ScalingChanged += OnScalingChanged;
        UpdateSize();
        Render();
        _clock.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_top != null) _top.ScalingChanged -= OnScalingChanged;
        _top = null;
        _clock.Stop();
        ReleaseWaiters();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateSize();

    private static bool ReducedMotion => Platform.ReducedMotion;

    // ---------- sizing: whole device pixels per art pixel ----------

    private void UpdateSize()
    {
        if (Img == null) return;
        double dpi = _top?.RenderScaling ?? 1;
        int phys = Math.Max(1, (int)Math.Round(ArtScale * dpi, MidpointRounding.AwayFromZero));
        double px = phys / dpi;
        Img.Width = PotatoArt.W * px;
        Img.Height = PotatoArt.H * px;
        Img.Margin = new Thickness(0, 0, 0, -(PotatoArt.H - PotatoArt.SoilRow) * px);
        Width = PotatoArt.W * px;
        Height = PotatoArt.SoilRow * px;
    }

    // ---------- frame clock ----------

    private void Tick()
    {
        _t++;
        if (_idle)
        {
            _pose.Sprout = (_t / 4 % 4) switch { 1 => 1, 3 => -1, _ => 0 };
            if (_pose.Eyes is EyeState.Open or EyeState.Closed)
            {
                if (_blinkLeft > 0)
                {
                    if (--_blinkLeft == 0) _pose.Eyes = EyeState.Open;
                }
                else if (_t >= _nextBlink)
                {
                    _pose.Eyes = EyeState.Closed;
                    _blinkLeft = 2;
                    _nextBlink = _t + (_rng.Next(5) == 0 ? 5 : _rng.Next(30, 62)); // sometimes a double blink
                }
            }
        }
        if (_waveLeft > 0)
        {
            _waveLeft--;
            _pose.Arms = _waveLeft == 0 ? ArmState.Down : (_waveLeft / 2 % 2 == 0 ? ArmState.WaveA : ArmState.WaveB);
        }
        if (_sparkleLeft > 0) _pose.Sparkle = --_sparkleLeft == 0 ? 0 : 1 + (int)(_t / 2 % 2);

        foreach (var w in _waiters.Where(w => w.due <= _t).ToList())
        {
            _waiters.Remove(w);
            w.tcs.TrySetResult();
        }
        Render();
    }

    private unsafe void Render()
    {
        var pose = _pose;
        bool breathing = _idle && _squashFx == 0 && pose.Lift == 0;
        pose.Squash = _squashFx + (breathing && _t / 10 % 2 == 1 ? -1 : 0);
        var px = PotatoArt.Render(pose); // 0xAARRGGBB little-endian = B, G, R, A
        using (var fb = _bmp.Lock())
        {
            fixed (uint* src = px)
            {
                for (int y = 0; y < PotatoArt.H; y++)
                    Buffer.MemoryCopy(src + y * PotatoArt.W, (byte*)fb.Address + y * fb.RowBytes, fb.RowBytes, PotatoArt.W * 4);
            }
        }
        Img?.InvalidateVisual();
    }

    private Task Frames(int n)
    {
        if (!_clock.IsEnabled || n <= 0) return Task.CompletedTask;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiters.Add((_t + n, tcs));
        return tcs.Task;
    }

    private void ReleaseWaiters()
    {
        foreach (var w in _waiters) w.tcs.TrySetResult();
        _waiters.Clear();
    }

    /// <summary>Step a value through keyframes, one frame each.</summary>
    private async Task Play(int[] frames, Action<int> set)
    {
        if (ReducedMotion) { set(frames[^1]); Render(); return; }
        foreach (int v in frames)
        {
            set(v);
            await Frames(1);
        }
    }

    // ---------- verbs ----------

    /// <summary>Hide below the soil line so the entrance springs up from nothing.</summary>
    public void PrepareEntrance()
    {
        if (!ReducedMotion) _pose.Lift = 40;
        Render();
    }

    /// <summary>Springs up out of the soil, slightly slouched.</summary>
    public async Task PlayEntranceAsync(bool slouched = true)
    {
        ResetFace();
        _pose.Lean = slouched ? -2 : 0;
        await Play(new[] { 34, 26, 18, 10, 4, 0, -3, -4, -2, 0 }, v => _pose.Lift = v);
        await Play(new[] { 1, 2, 1, 0 }, v => _squashFx = v);
    }

    public async Task PlayExitAsync()
    {
        _idle = false;
        await Play(new[] { 1, 0, -2, -1, 4, 11, 20, 30, 42 }, v => _pose.Lift = v);
    }

    public void StartIdle()
    {
        if (_idle) return;
        _idle = !ReducedMotion;
        _nextBlink = _t + _rng.Next(15, 40);
    }

    public void StopIdle() => _idle = false;

    /// <summary>A little wave with the right arm.</summary>
    public void Wave()
    {
        if (!ReducedMotion && _pose.Arms == ArmState.Down) _waveLeft = 14;
    }

    /// <summary>Straightens up with a springy wobble, hops, cheers.</summary>
    public async Task CelebrateAsync()
    {
        _waveLeft = 0;
        _pose.Eyes = EyeState.Happy;
        _pose.Mouth = MouthState.Smile;
        _pose.Arms = ArmState.Cheer;
        _sparkleLeft = ReducedMotion ? 0 : 16;
        var straighten = Play(new[] { -1, 1, 2, 2, 1, 0, -1, 0 }, v => _pose.Lean = v);
        var hop = Play(new[] { 1, -2, -5, -7, -8, -8, -7, -5, -2, 0 }, v => _pose.Lift = v);
        await Task.WhenAll(straighten, hop);
        await Play(new[] { 2, 1, 0 }, v => _squashFx = v);
        await Frames(4);
    }

    /// <summary>Open eyes plus a smile: the face for the app icon and settings header.</summary>
    public void UsePortraitFace()
    {
        _pose.Eyes = EyeState.Open;
        _pose.Mouth = MouthState.Smile;
        Render();
    }

    private void ResetFace()
    {
        _pose.Eyes = EyeState.Open;
        _pose.Mouth = MouthState.Meh;
        _pose.Arms = ArmState.Down;
        _pose.Sparkle = 0;
        _sparkleLeft = _waveLeft = _blinkLeft = 0;
    }
}
