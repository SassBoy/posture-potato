using System.Net.Sockets;

namespace PosturePotato;

/// <summary>
/// One running copy per user. The first copy holds a claim (Windows: a session-local named
/// mutex; Linux: an exclusive lock on a file in the user's runtime dir, which, unlike .NET's
/// "Local\" mutexes, doesn't depend on which session a launcher started us in) and listens
/// on a local socket. A later launch pokes that socket (meaning "open Settings") and exits.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\PosturePotato.Instance";

    private readonly IDisposable _claim;
    private readonly Socket _listener;
    private readonly string _path;

    /// <summary>Raised on a background thread when another launch asks for Settings.</summary>
    public static event Action? ShowRequested;

    private SingleInstance(IDisposable claim, Socket listener, string path)
    {
        _claim = claim;
        _listener = listener;
        _path = path;
        new Thread(Accept) { IsBackground = true, Name = "single-instance" }.Start();
    }

    /// <summary>Take the per-user claim, or return null if another copy holds it.</summary>
    private static IDisposable? Claim(string socketPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(true, MutexName, out bool first);
            if (first) return new MutexClaim(mutex);
            mutex.Dispose();
            return null;
        }
        try
        {
            // .NET maps FileShare.None to an exclusive flock(); the kernel drops it if we die.
            return new FileStream(Path.ChangeExtension(socketPath, ".lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class MutexClaim(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }

    private static string SocketPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Path.GetTempPath(), "PosturePotato.sock");
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return string.IsNullOrEmpty(runtime)
                ? Path.Combine(Path.GetTempPath(), $"posture-potato-{Environment.UserName}.sock")
                : Path.Combine(runtime, "posture-potato.sock");
        }
    }

    /// <summary>Returns the claim, or null (after signalling the running copy) if one exists.</summary>
    public static SingleInstance? TryClaim()
    {
        var path = SocketPath;
        var claim = Claim(path);
        if (claim == null)
        {
            Signal(path);
            return null;
        }

        try
        {
            if (File.Exists(path)) File.Delete(path); // left over from a crash
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            socket.Listen(4);
            return new SingleInstance(claim, socket, path);
        }
        catch (Exception ex)
        {
            // Still the only copy; we just can't be poked. Keep running.
            Log.Write($"single-instance socket unavailable: {ex.GetType().Name}: {ex.Message}");
            return new SingleInstance(claim, new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified), path);
        }
    }

    private static void Signal(string path)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            s.Connect(new UnixDomainSocketEndPoint(path));
        }
        catch (Exception ex)
        {
            Log.Write($"couldn't reach the running copy: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Accept()
    {
        while (true)
        {
            try
            {
                using var client = _listener.Accept();
                ShowRequested?.Invoke();
            }
            catch
            {
                return; // listener closed, or never bound
            }
        }
    }

    public void Dispose()
    {
        try { _listener.Dispose(); } catch { }
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        _claim.Dispose();
    }
}
