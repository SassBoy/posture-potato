using System.Text;
using Microsoft.Win32;

namespace PosturePotato;

/// <summary>
/// Start at login. Windows: the per-user Run key (Task Manager's "Disable" is honoured).
/// Linux: ~/.config/autostart/posture-potato.desktop (a "Hidden=true" or
/// "X-GNOME-Autostart-enabled=false" set by the desktop's startup settings is honoured).
/// </summary>
public static class Autostart
{
    // ----- Windows -----
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "PosturePotato";
    private static string WinCommand => $"\"{Environment.ProcessPath}\"";

    // ----- Linux -----
    private static string DesktopFile =>
        Path.Combine(Log.Xdg("XDG_CONFIG_HOME", ".config"), "autostart", "posture-potato.desktop");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var run = Registry.CurrentUser.OpenSubKey(RunKey);
                    if (run?.GetValue(Name) is not string) return false;
                    using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
                    // First byte 0x03 means the user disabled it in Task Manager / Settings > Startup.
                    return approved?.GetValue(Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
                }
                return File.Exists(DesktopFile) && !DisabledByDesktop(File.ReadAllText(DesktopFile));
            }
            catch { return false; }
        }
    }

    private static bool DisabledByDesktop(string entry) =>
        entry.Split('\n').Select(l => l.Trim()).Any(l =>
            l.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) ||
            l.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase));

    /// <summary>Explicit choice from Settings. "On" also clears a disable set elsewhere.</summary>
    public static void Set(bool enabled)
    {
        if (OperatingSystem.IsWindows())
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            if (enabled) run.SetValue(Name, WinCommand);
            else run.DeleteValue(Name, throwOnMissingValue: false);
            approved.DeleteValue(Name, throwOnMissingValue: false); // clears a Task Manager "disabled"
            return;
        }
        if (enabled) WriteFile(DesktopFile, Launcher.DesktopEntry(autostart: true));
        else if (File.Exists(DesktopFile)) File.Delete(DesktopFile);
    }

    /// <summary>
    /// Run on every launch: make the startup entry match the saved choice. A missing or stale
    /// entry is (re)written, so startup heals itself; a disable set by the OS is left alone.
    /// </summary>
    public static void Sync(bool wanted)
    {
        try
        {
            if (OperatingSystem.IsWindows()) SyncWindows(wanted);
            else SyncLinux(wanted);
            if (wanted && !IsEnabled) Log.Write("startup entry is disabled in the system's startup settings; leaving it");
        }
        catch (Exception ex)
        {
            Log.Write($"startup entry sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SyncWindows(bool wanted)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        var current = run.GetValue(Name) as string;
        if (wanted && current != WinCommand)
        {
            run.SetValue(Name, WinCommand);
            Log.Write(current == null ? $"startup entry added: {WinCommand}" : $"startup entry repointed: {WinCommand}");
        }
        else if (!wanted && current != null)
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
            Log.Write("startup entry removed (start at login is off)");
        }
    }

    private static void SyncLinux(bool wanted)
    {
        var path = DesktopFile;
        bool exists = File.Exists(path);
        if (!wanted)
        {
            if (exists) { File.Delete(path); Log.Write($"startup entry removed: {path}"); }
            return;
        }
        var desired = Launcher.DesktopEntry(autostart: true);
        if (!exists)
        {
            WriteFile(path, desired);
            Log.Write($"startup entry added: {path}");
            return;
        }
        var current = File.ReadAllText(path);
        if (DisabledByDesktop(current)) return; // the user switched it off in their desktop's startup settings
        if (current != desired)
        {
            WriteFile(path, desired);
            Log.Write($"startup entry repointed: {path}");
        }
    }

    internal static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// Makes the app findable in the OS search: a Start menu shortcut on Windows, a
/// ~/.local/share/applications entry (plus icon) on Linux. Repointed if the exe moves.
/// </summary>
public static class Launcher
{
    private static string WinLink => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Posture Potato.lnk");

    private static string DataHome => Log.Xdg("XDG_DATA_HOME", ".local/share");
    private static string LinuxEntry => Path.Combine(DataHome, "applications", "posture-potato.desktop");
    private static string LinuxIcon => Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "posture-potato.png");

    public static void Ensure()
    {
        try
        {
            if (OperatingSystem.IsWindows()) EnsureWindows();
            else EnsureLinux();
        }
        catch (Exception ex)
        {
            Log.Write($"launcher entry failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void EnsureWindows()
    {
        var exe = Environment.ProcessPath;
        var shellType = exe == null ? null : Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(WinLink); // loads the existing one, if any
        if (File.Exists(WinLink) && string.Equals((string)link.TargetPath, exe, StringComparison.OrdinalIgnoreCase))
            return;
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe);
        link.IconLocation = exe + ",0";
        link.Description = "Posture check reminders";
        link.Save();
        Log.Write($"start menu shortcut written: {WinLink}");
    }

    private static void EnsureLinux()
    {
        var icon = IconFactory.RenderPng(256);
        if (!File.Exists(LinuxIcon) || !File.ReadAllBytes(LinuxIcon).AsSpan().SequenceEqual(icon))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxIcon)!);
            File.WriteAllBytes(LinuxIcon, icon);
        }
        var entry = DesktopEntry(autostart: false);
        if (!File.Exists(LinuxEntry) || File.ReadAllText(LinuxEntry) != entry)
        {
            Autostart.WriteFile(LinuxEntry, entry);
            Log.Write($"app menu entry written: {LinuxEntry}");
        }
    }

    /// <summary>A freedesktop .desktop entry that starts this exe.</summary>
    internal static string DesktopEntry(bool autostart)
    {
        var sb = new StringBuilder()
            .Append("[Desktop Entry]\n")
            .Append("Type=Application\n")
            .Append("Name=Posture Potato\n")
            .Append("Comment=Posture check reminders\n")
            .Append("Exec=").Append(QuoteExec(Environment.ProcessPath ?? "posture-potato")).Append('\n')
            .Append("Icon=").Append(LinuxIcon).Append('\n')
            .Append("Terminal=false\n")
            .Append("StartupNotify=false\n");
        if (autostart) sb.Append("X-GNOME-Autostart-enabled=true\n");
        else sb.Append("Categories=Utility;\n").Append("Keywords=posture;reminder;break;\n");
        return sb.ToString();
    }

    /// <summary>Desktop Entry spec quoting for the Exec key.</summary>
    private static string QuoteExec(string path)
    {
        var escaped = new StringBuilder();
        foreach (char c in path)
        {
            if (c is '"' or '`' or '$' or '\\') escaped.Append('\\');
            if (c == '%') escaped.Append('%');
            escaped.Append(c);
        }
        var s = escaped.ToString();
        // The key value itself also unescapes backslashes once, so double them again.
        s = s.Replace("\\", "\\\\");
        return path.IndexOfAny(new[] { ' ', '\t', '"', '\'', '\\', '>', '<', '~', '|', '&', ';', '$', '*', '?', '#', '(', ')', '`' }) >= 0
            ? $"\"{s}\""
            : s;
    }
}
