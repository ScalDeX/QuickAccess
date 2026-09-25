using System.Text.Json;

namespace QuickAccess;

public class ShortcutEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public double X { get; set; } = 0;
    public double Y { get; set; } = 0;
    public string IconGlyph { get; set; } = "";
}

public class GroupEntry
{
    public string Name { get; set; } = "";
    public List<ShortcutEntry> Shortcuts { get; set; } = new();
    public double X { get; set; } = 0;
    public double Y { get; set; } = 0;
    public string IconGlyph { get; set; } = "";
}

public class AppSettings
{
    public double LineThickness { get; set; } = 3.0;
    public bool SnapToGrid { get; set; } = false;
    public int GridSize { get; set; } = 25;
    public double Tension { get; set; } = 0.5;
    public string LineColorHex { get; set; } = "#FFFFFF";
    public string AccentColorHex { get; set; } = "#FF8C1A";
    public string BackgroundColorHex { get; set; } = "#DC121215";
    public string HotKeyMain { get; set; } = "Alt+E";
    public string HotKeySecondary { get; set; } = "Alt+Space";
    public string Language { get; set; } = "auto";
    public Dictionary<string, bool> ModuleStates { get; set; } = new();
}

public static class ConfigService
{
    private static readonly string Dir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickAccess");
    private static readonly string FilePath = System.IO.Path.Combine(Dir, "config.json");
    private static readonly string SettingsPath = System.IO.Path.Combine(Dir, "settings.json");

    public static List<GroupEntry> Load()
    {
        try
        {
            if (System.IO.File.Exists(FilePath))
            {
                var json = System.IO.File.ReadAllText(FilePath);
                var data = JsonSerializer.Deserialize<List<GroupEntry>>(json);
                if (data is not null) return data;
            }
        }
        catch { }
        var defaults = new List<GroupEntry>
        {
            new() { Name = "Игры", Shortcuts = new() { new ShortcutEntry { Name = "Пример", Path = "notepad.exe" } } },
            new() { Name = "Работа", Shortcuts = new() },
            new() { Name = "Интернет", Shortcuts = new() },
        };
        Save(defaults);
        return defaults;
    }

    public static void Save(List<GroupEntry> groups)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(groups, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(FilePath, json);
        }
        catch { }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (System.IO.File.Exists(SettingsPath))
            {
                var json = System.IO.File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<AppSettings>(json);
                if (data is not null) return Normalize(data);
            }
        }
        catch { }
        var defaults = new AppSettings();
        SaveSettings(defaults);
        return defaults;
    }

    private static AppSettings Normalize(AppSettings s)
    {
        bool fixed_ = false;
        if (string.IsNullOrWhiteSpace(s.LineColorHex)) { s.LineColorHex = "#FFFFFF"; fixed_ = true; }
        if (string.IsNullOrWhiteSpace(s.AccentColorHex)) { s.AccentColorHex = "#FF8C1A"; fixed_ = true; }
        if (string.IsNullOrWhiteSpace(s.BackgroundColorHex)) { s.BackgroundColorHex = "#DC121215"; fixed_ = true; }
        if (s.Tension <= 0) { s.Tension = 0.5; fixed_ = true; }
        if (s.LineThickness <= 0) { s.LineThickness = 3.0; fixed_ = true; }
        if (string.IsNullOrWhiteSpace(s.HotKeyMain)) { s.HotKeyMain = "Alt+E"; fixed_ = true; }
        if (string.IsNullOrWhiteSpace(s.HotKeySecondary)) { s.HotKeySecondary = "Alt+Space"; fixed_ = true; }
        if (string.IsNullOrWhiteSpace(s.Language)) { s.Language = "auto"; fixed_ = true; }
        s.ModuleStates ??= new();
        if (fixed_) SaveSettings(s);
        return s;
    }

    public static void SaveSettings(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }
}
