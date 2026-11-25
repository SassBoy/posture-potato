using System.Diagnostics;
using Avalonia.Threading;

namespace PosturePotato;

/// <summary>Tiny time-based tweens for the window choreography (fades, slides, countdown).</summary>
internal static class Tween
{
    public static double CubicOut(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>Calls <paramref name="step"/> with 0..1 over the duration, then completes.</summary>
    public static Task Run(TimeSpan duration, Action<double> step, Func<double, double>? ease = null, TimeSpan delay = default)
    {
        if (Platform.ReducedMotion || duration <= TimeSpan.Zero)
        {
            step(1);
            return Task.CompletedTask;
        }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
        timer.Tick += (_, _) =>
        {
            var elapsed = clock.Elapsed - delay;
            if (elapsed < TimeSpan.Zero) return;
            double t = Math.Min(1, elapsed / duration);
            step(ease?.Invoke(t) ?? t);
            if (t >= 1)
            {
                timer.Stop();
                tcs.TrySetResult();
            }
        };
        timer.Start();
        return tcs.Task;
    }
}

/// <summary>A pausable countdown (hover holds it) that reports remaining fraction 1..0.</summary>
internal sealed class Countdown
{
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly TimeSpan _length;

    public event Action<double>? Progress;
    public event Action? Elapsed;

    public Countdown(TimeSpan length)
    {
        _length = length;
        _timer.Tick += (_, _) =>
        {
            double left = Math.Max(0, 1 - _clock.Elapsed / _length);
            Progress?.Invoke(left);
            if (left <= 0)
            {
                Stop();
                Elapsed?.Invoke();
            }
        };
    }

    public void Start() { _clock.Start(); _timer.Start(); }
    public void Pause() => _clock.Stop();
    public void Resume() { if (_timer.IsEnabled) _clock.Start(); }
    public void Stop() { _clock.Stop(); _timer.Stop(); }
}
