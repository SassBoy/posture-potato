using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosturePotato;

public sealed class Settings
{
    public int IntervalMinutes { get; set; } = 30;
    public int AutoHideSeconds { get; set; } = 60;
    public bool QuietDuringFullscreen { get; set; } = true;
    public bool PauseWhenAway { get; set; } = true;
    public bool StartAtLogin { get; set; } = true;

    /// <summary>Read-only alias for settings files written by 1.x ("Start with Windows").</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StartWithWindows
    {
        get => null;
        set { if (value is bool b) StartAtLogin = b; }
    }

    /// <summary>Windows: %APPDATA%\PosturePotato. Linux: ~/.config/posture-potato.</summary>
    public static readonly string Folder = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PosturePotato")
        : Path.Combine(Log.Xdg("XDG_CONFIG_HOME", ".config"), "posture-potato");

    public static readonly string FilePath = Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(out bool firstRun)
    {
        firstRun = !File.Exists(FilePath);
        Settings s;
        try { s = firstRun ? new() : JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new(); }
        catch (Exception ex)
        {
            Log.Write($"settings unreadable, using defaults: {ex.GetType().Name}: {ex.Message}");
            s = new();
        }
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
