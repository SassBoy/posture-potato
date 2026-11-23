using System.Runtime.InteropServices;

namespace PosturePotato;

/// <summary>The handful of Win32 calls WPF doesn't expose.</summary>
internal static class Native
{
    // ----- idle + "is something full-screen?" -----

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

    // ----- windows -----

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x00000008;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    /// <summary>Work area (physical pixels) of the monitor you're using: the one holding the
    /// foreground window, else the one under the cursor.</summary>
    public static RECT ActiveWorkArea()
    {
        IntPtr mon = MonitorFromWindow(GetForegroundWindow(), 0 /*null*/);
        if (mon == IntPtr.Zero && GetCursorPos(out var p)) mon = MonitorFromPoint(p, 2 /*nearest*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(mon, ref mi);
        return mi.rcWork;
    }

    public static void MakeOverlay(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST));
    }

    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Give memory back once nothing is on screen, so the app idles small.</summary>
    public static void TrimMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }

    // Windows 11 rounded corners for normal (non-transparent) windows.
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33 /*DWMWA_WINDOW_CORNER_PREFERENCE*/, ref round, sizeof(int));
        int border = 0x0043373B; // COLORREF of the hairline #3B3743 (0x00BBGGRR)
        DwmSetWindowAttribute(hwnd, 34 /*DWMWA_BORDER_COLOR*/, ref border, sizeof(int));
    }
}
