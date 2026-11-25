using System.Diagnostics;
using System.Runtime.InteropServices;
using Tmds.DBus.Protocol;

namespace PosturePotato;

/// <summary>
/// Linux desktop probes. Idle time: GNOME's Mutter idle monitor over D-Bus, else the X11
/// screen-saver extension on X11 sessions. Lock: logind's LockedHint and the screen-saver
/// D-Bus services. Full screen: the active window's _NET_WM_STATE on X11 sessions.
/// D-Bus answers are polled in the background and cached, so the UI thread never waits.
/// Wayland compositors other than GNOME don't expose idle time or full-screen state to
/// apps, so those features report "unknown" there and stay out of the way.
/// </summary>
internal static class LinuxDesktop
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(10);

    private static TimeSpan? _dbusIdle;
    private static bool? _locked;
    private static bool _started;

    public static string SessionType =>
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") is { Length: > 0 } t ? t
        : Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is { Length: > 0 } ? "wayland"
        : Environment.GetEnvironmentVariable("DISPLAY") is { Length: > 0 } ? "x11" : "unknown";

    private static bool IsX11Session => SessionType == "x11";

    public static string SessionDescription =>
        $"{Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "unknown desktop"}, {SessionType}";

    public static void Start()
    {
        if (_started) return;
        _started = true;
        _ = PollLoop();
    }

    // ---------------- idle ----------------

    public static TimeSpan? IdleTime => _dbusIdle ?? (IsX11Session ? X11.IdleTime : null);

    // ---------------- lock ----------------

    public static bool? IsLocked => _locked;

    // ---------------- full screen ----------------

    public static bool IsFullscreenActive => IsX11Session && X11.ActiveWindowIsFullscreen();

    // ---------------- reduced motion ----------------

    public static bool AnimationsDisabled
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("gsettings", "get org.gnome.desktop.interface enable-animations")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (p == null || !p.WaitForExit(2000)) return false;
                return p.StandardOutput.ReadToEnd().Trim() == "false";
            }
            catch { return false; }
        }
    }

    // ---------------- D-Bus polling ----------------

    private static async Task PollLoop()
    {
        Connection? session = await Connect(Address.Session, "session");
        Connection? system = await Connect(Address.System, "system");
        bool loggedSources = false;

        if (session != null)
        {
            bool tray = await Try(() => session.CallMethodAsync(
                Call(session, "org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner",
                     "s", w => w.WriteString("org.kde.StatusNotifierWatcher")),
                (m, _) => m.GetBodyReader().ReadBool(), null)) ?? false;
            Log.Write(tray ? "tray: a system tray (StatusNotifierWatcher) is available"
                           : "tray: no system tray found; open Posture Potato from your app menu to reach Settings");
        }

        while (true)
        {
            TimeSpan? idle = null;
            bool? locked = null;

            if (session != null)
            {
                var ms = await Try(() => session.CallMethodAsync(
                    Call(session, "org.gnome.Mutter.IdleMonitor", "/org/gnome/Mutter/IdleMonitor/Core",
                         "org.gnome.Mutter.IdleMonitor", "GetIdletime"),
                    (m, _) => m.GetBodyReader().ReadUInt64(), null));
                if (ms is ulong v) idle = TimeSpan.FromMilliseconds(v);

                foreach (var (dest, path, iface) in new[]
                {
                    ("org.gnome.ScreenSaver", "/org/gnome/ScreenSaver", "org.gnome.ScreenSaver"),
                    ("org.freedesktop.ScreenSaver", "/org/freedesktop/ScreenSaver", "org.freedesktop.ScreenSaver"),
                })
                {
                    var active = await Try(() => session.CallMethodAsync(
                        Call(session, dest, path, iface, "GetActive"),
                        (m, _) => m.GetBodyReader().ReadBool(), null));
                    if (active is bool a) locked = (locked ?? false) || a;
                }
            }

            if (system != null)
            {
                var hint = await Try(() => system.CallMethodAsync(
                    Call(system, "org.freedesktop.login1", "/org/freedesktop/login1/session/auto",
                         "org.freedesktop.DBus.Properties", "Get", "ss", w =>
                         {
                             w.WriteString("org.freedesktop.login1.Session");
                             w.WriteString("LockedHint");
                         }),
                    (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), null));
                if (hint is bool h) locked = (locked ?? false) || h;
            }

            _dbusIdle = idle;
            _locked = locked;

            if (!loggedSources)
            {
                loggedSources = true;
                var idleSource = idle != null ? "GNOME idle monitor" : IsX11Session && X11.IdleTime != null ? "X11 screen saver" : "unavailable";
                Log.Write($"linux probes: idle time {idleSource}, lock state {(locked != null ? "available" : "unavailable")}, " +
                          $"full-screen check {(IsX11Session ? "X11" : "unavailable on this session type")}");
            }

            await Task.Delay(PollEvery);
        }
    }

    private static MessageBuffer Call(Connection c, string dest, string path, string iface, string member,
                                      string? signature = null, Action<MessageWriterBox>? body = null)
    {
        using var writer = c.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: dest, path: path, @interface: iface, member: member, signature: signature);
        if (body != null)
        {
            var box = new MessageWriterBox();
            body(box);
            foreach (var s in box.Strings) writer.WriteString(s);
        }
        return writer.CreateMessage();
    }

    /// <summary>The only argument type we send is strings; collect them, then write.</summary>
    private sealed class MessageWriterBox
    {
        public readonly List<string> Strings = new();
        public void WriteString(string s) => Strings.Add(s);
    }

    private static async Task<Connection?> Connect(string? address, string name)
    {
        if (string.IsNullOrEmpty(address)) return null;
        try
        {
            var c = new Connection(address);
            await c.ConnectAsync();
            return c;
        }
        catch (Exception ex)
        {
            Log.Write($"d-bus {name} bus unavailable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<T?> Try<T>(Func<Task<T>> call) where T : struct
    {
        try { return await call(); }
        catch { return null; } // service not running on this desktop
    }
}

/// <summary>Minimal Xlib for X11 sessions: idle time and the active window's full-screen state.</summary>
internal static class X11
{
    private const string LibX11 = "libX11.so.6", LibXss = "libXss.so.1";

    [DllImport(LibX11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(LibX11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(LibX11)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
    [DllImport(LibX11)] private static extern int XFree(IntPtr data);
    [DllImport(LibX11)]
    private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset,
        IntPtr length, bool delete, IntPtr reqType, out IntPtr actualType, out int actualFormat,
        out IntPtr nItems, out IntPtr bytesAfter, out IntPtr data);
    [DllImport(LibXss)] private static extern IntPtr XScreenSaverAllocInfo();
    [DllImport(LibXss)] private static extern int XScreenSaverQueryInfo(IntPtr display, IntPtr drawable, IntPtr info);

    private static readonly object Gate = new();
    private static IntPtr _display, _info;
    private static bool _broken, _xssBroken;

    private static IntPtr Display
    {
        get
        {
            if (_display == IntPtr.Zero && !_broken)
            {
                try { _display = XOpenDisplay(IntPtr.Zero); }
                catch { _broken = true; }
                if (_display == IntPtr.Zero) _broken = true;
            }
            return _display;
        }
    }

    public static TimeSpan? IdleTime
    {
        get
        {
            lock (Gate)
            {
                if (_xssBroken || Display == IntPtr.Zero) return null;
                try
                {
                    if (_info == IntPtr.Zero) _info = XScreenSaverAllocInfo();
                    if (_info == IntPtr.Zero || XScreenSaverQueryInfo(_display, XDefaultRootWindow(_display), _info) == 0)
                        return null;
                    // XScreenSaverInfo { Window window; int state; int kind; ulong til_or_since; ulong idle; ... }
                    return TimeSpan.FromMilliseconds(Marshal.ReadInt64(_info, IntPtr.Size + 8 + 8));
                }
                catch
                {
                    _xssBroken = true; // libXss missing
                    return null;
                }
            }
        }
    }

    public static bool ActiveWindowIsFullscreen()
    {
        lock (Gate)
        {
            if (Display == IntPtr.Zero) return false;
            try
            {
                var root = XDefaultRootWindow(_display);
                var active = ReadLongs(root, "_NET_ACTIVE_WINDOW", 33 /*XA_WINDOW*/);
                if (active.Length == 0 || active[0] == IntPtr.Zero) return false;
                var fullscreen = XInternAtom(_display, "_NET_WM_STATE_FULLSCREEN", true);
                if (fullscreen == IntPtr.Zero) return false;
                return ReadLongs(active[0], "_NET_WM_STATE", 4 /*XA_ATOM*/).Contains(fullscreen);
            }
            catch { return false; }
        }
    }

    /// <summary>Format-32 properties come back as C longs.</summary>
    private static IntPtr[] ReadLongs(IntPtr window, string property, int type)
    {
        var atom = XInternAtom(_display, property, true);
        if (atom == IntPtr.Zero) return Array.Empty<IntPtr>();
        if (XGetWindowProperty(_display, window, atom, IntPtr.Zero, new IntPtr(1024), false, new IntPtr(type),
                out _, out int format, out var count, out _, out var data) != 0 || data == IntPtr.Zero)
            return Array.Empty<IntPtr>();
        try
        {
            if (format != 32) return Array.Empty<IntPtr>();
            var result = new IntPtr[(int)count];
            for (int i = 0; i < result.Length; i++) result[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size);
            return result;
        }
        finally { XFree(data); }
    }
}
