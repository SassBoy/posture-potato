using System.Text;

namespace PosturePotato;

/// <summary>
/// A short startup/exit log, so a launch that silently goes missing leaves a trace:
/// did it start, hand off, crash or exit? Windows: %APPDATA%\PosturePotato\startup.log.
/// Linux: ~/.local/state/posture-potato/startup.log.
/// </summary>
public static class Log
{
    private const long MaxBytes = 64 * 1024;
    private static readonly object Gate = new();

    public static readonly string Folder = OperatingSystem.IsWindows()
        ? Settings.Folder
        : Path.Combine(Xdg("XDG_STATE_HOME", ".local/state"), "posture-potato");

    public static readonly string FilePath = Path.Combine(Folder, "startup.log");

    /// <summary>An XDG base directory, falling back to ~/fallback.</summary>
    public static string Xdg(string variable, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(v)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback)
            : v;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never take the app down */ }
    }

    /// <summary>Keep the newest half once the file passes 64 KB.</summary>
    public static void Trim()
    {
        try
        {
            lock (Gate)
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length <= MaxBytes) return;
                var text = File.ReadAllText(FilePath);
                var keep = text[(text.Length / 2)..];
                int nl = keep.IndexOf('\n');
                File.WriteAllText(FilePath, nl >= 0 ? keep[(nl + 1)..] : keep, Encoding.UTF8);
            }
        }
        catch { }
    }
}
