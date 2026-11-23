using System.IO;
using System.Text;

namespace PosturePotato;

/// <summary>
/// A short startup/exit log (%APPDATA%\PosturePotato\startup.log), so a launch that
/// silently goes missing leaves a trace: did it start, hand off, crash or exit?
/// </summary>
public static class Log
{
    private const long MaxBytes = 64 * 1024;
    private static readonly string FilePath = Path.Combine(Settings.Folder, "startup.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Settings.Folder);
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

/// <summary>A Start menu shortcut, so Windows search finds the app.</summary>
public static class StartMenu
{
    private static readonly string LinkPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Posture Potato.lnk");

    /// <summary>Create the shortcut, or repoint it if the exe has moved.</summary>
    public static void Ensure()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(LinkPath); // loads the existing one, if any
            if (File.Exists(LinkPath) && string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
                return;
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Posture check reminders";
            link.Save();
            Log.Write($"start menu shortcut written: {LinkPath}");
        }
        catch (Exception ex)
        {
            Log.Write($"start menu shortcut failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
