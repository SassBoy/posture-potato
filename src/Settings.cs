using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace PosturePotato;

public sealed class Settings
{
    public int IntervalMinutes { get; set; } = 30;
    public int AutoHideSeconds { get; set; } = 60;
    public bool QuietDuringFullscreen { get; set; } = true;
    public bool PauseWhenAway { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;

    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PosturePotato");
    private static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(out bool firstRun)
    {
        firstRun = !File.Exists(FilePath);
        Settings s;
        try { s = firstRun ? new() : JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { s = new(); }
        s.Clamp();
        return s;
    }

    /// <summary>Write settings.json atomically. Returns false (and logs) instead of throwing.</summary>
    public bool Save()
    {
        Clamp();
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private void Clamp()
    {
        IntervalMinutes = Math.Clamp(IntervalMinutes, 5, 180);
        AutoHideSeconds = Math.Clamp(AutoHideSeconds, 15, 600);
    }

    public static string FormatMinutes(int m) =>
        m < 60 ? $"{m} min" : m % 60 == 0 ? $"{m / 60} h" : $"{m / 60} h {m % 60} min";
}

/// <summary>Start with Windows via the per-user Run key. Task Manager's "Disable" is honoured.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string Name = "PosturePotato";
    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(Name) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            // First byte 0x03 means the user disabled it in Task Manager / Settings > Startup.
            return approved?.GetValue(Name) is not byte[] b || b.Length == 0 || (b[0] & 1) == 0;
        }
    }

    public static void Set(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        if (enabled)
        {
            run.SetValue(Name, Command);
            approved.DeleteValue(Name, throwOnMissingValue: false); // clears a Task Manager "disabled"
        }
        else
        {
            run.DeleteValue(Name, throwOnMissingValue: false);
            approved.DeleteValue(Name, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Run on every launch: make the Run entry match the saved choice. A missing or stale
    /// entry is (re)written, so startup heals itself; a Task Manager "Disable" is left alone.
    /// </summary>
    public static void Sync(bool wanted)
    {
        try
        {
            using var run = Registry.CurrentUser.CreateSubKey(RunKey);
            var current = run.GetValue(Name) as string;
            if (wanted && current != Command)
            {
                run.SetValue(Name, Command);
                Log.Write(current == null ? $"startup entry added: {Command}" : $"startup entry repointed: {Command}");
            }
            else if (!wanted && current != null)
            {
                run.DeleteValue(Name, throwOnMissingValue: false);
                Log.Write("startup entry removed (Start with Windows is off)");
            }
            if (wanted && !IsEnabled) Log.Write("startup entry is disabled in Task Manager; leaving it");
        }
        catch (Exception ex)
        {
            Log.Write($"startup entry sync failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
