using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace PosturePotato;

/// <summary>
/// Everything the UI toolkit doesn't cover, behind one face. Windows uses Win32; Linux uses
/// X11 and D-Bus (see <see cref="LinuxDesktop"/>). Anything a desktop can't tell us comes
/// back as "unknown" and the matching feature simply stays out of the way.
/// </summary>
internal static class Platform
{
    public static string Describe() => OperatingSystem.IsWindows()
        ? $"Windows {Environment.OSVersion.Version}"
        : $"Linux ({LinuxDesktop.SessionDescription})";

    /// <summary>Start background probes (Linux polls D-Bus for idle and lock state).</summary>
    public static void Init()
    {
        if (OperatingSystem.IsLinux()) LinuxDesktop.Start();
    }

    /// <summary>Time since the last keyboard or mouse input, or null if the desktop won't say.</summary>
    public static TimeSpan? IdleTime => OperatingSystem.IsWindows() ? Win32.IdleTime : LinuxDesktop.IdleTime;

    /// <summary>True when something full-screen (or presentation mode) is in front.</summary>
    public static bool IsUserBusy => OperatingSystem.IsWindows() ? Win32.IsUserBusy : LinuxDesktop.IsFullscreenActive;

    /// <summary>Polled lock state (Linux). Null where lock arrives as events instead (Windows).</summary>
    public static bool? IsLocked => OperatingSystem.IsWindows() ? null : LinuxDesktop.IsLocked;

    private static bool? _reducedMotion;
    /// <summary>The OS "show animations" setting is off.</summary>
    public static bool ReducedMotion => _reducedMotion ??=
        OperatingSystem.IsWindows() ? !Win32.ClientAreaAnimation : LinuxDesktop.AnimationsDisabled;

    /// <summary>Work area (physical pixels) of the monitor you're using.</summary>
    public static PixelRect WorkArea(Window w)
    {
        if (OperatingSystem.IsWindows() && Win32.ActiveWorkArea() is { } r) return r;
        var screen = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary ?? w.Screens.All.FirstOrDefault();
        return screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);
    }

    /// <summary>Make the reminder a no-activate, always-on-top tool window.</summary>
    public static void MakeOverlay(Window w)
    {
        if (OperatingSystem.IsWindows() && w.TryGetPlatformHandle()?.Handle is { } h) Win32.MakeOverlay(h);
    }

    /// <summary>Windows 11 rounded corners and hairline border on a frameless normal window.</summary>
    public static void RoundCorners(Window w)
    {
        if (OperatingSystem.IsWindows() && w.TryGetPlatformHandle()?.Handle is { } h) Win32.RoundCorners(h);
    }

    /// <summary>Give memory back once nothing is on screen, so the app idles small.</summary>
    public static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        if (OperatingSystem.IsWindows()) Win32.EmptyWorkingSet();
    }
}

/// <summary>The handful of Win32 calls the toolkit doesn't expose.</summary>
internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    public static TimeSpan IdleTime
    {
        get
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref lii)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - lii.dwTime));
        }
    }

    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>True when Windows itself would hold back notifications: a full-screen app,
    /// a D3D game, presentation mode, or the screen saver / lock screen.</summary>
    public static bool IsUserBusy
    {
        get
        {
            if (SHQueryUserNotificationState(out int s) != 0) return false;
            return s is 1 /*not present*/ or 2 /*busy*/ or 3 /*D3D full screen*/ or 4 /*presentation*/;
        }
    }

    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);
    public static bool ClientAreaAnimation =>
        !SystemParametersInfo(0x1042 /*SPI_GETCLIENTAREAANIMATION*/, 0, out bool on, 0) || on;

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    /// <summary>Work area of the monitor holding the foreground window, else the one under the cursor.</summary>
    public static PixelRect? ActiveWorkArea()
    {
        IntPtr mon = MonitorFromWindow(GetForegroundWindow(), 0 /*null*/);
        if (mon == IntPtr.Zero && GetCursorPos(out var p)) mon = MonitorFromPoint(p, 2 /*nearest*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon == IntPtr.Zero || !GetMonitorInfo(mon, ref mi)) return null;
        var r = mi.rcWork;
        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    public static void MakeOverlay(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
    }

    [DllImport("psapi.dll", EntryPoint = "EmptyWorkingSet")] private static extern bool EmptyWorkingSetNative(IntPtr process);
    public static void EmptyWorkingSet() => EmptyWorkingSetNative(System.Diagnostics.Process.GetCurrentProcess().Handle);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
        int border = 0x0043373B; // COLORREF of the hairline #3B3743 (0x00BBGGRR)
        DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref border, sizeof(int));
    }
}
