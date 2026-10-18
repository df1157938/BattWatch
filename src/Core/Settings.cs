using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace BattWatch.Core;

public sealed class Settings
{
    public int LowThreshold { get; set; } = 20;
    public int PollSeconds { get; set; } = 60;
    public bool ShowLaptopBattery { get; set; } = true;
    public bool ShowWidget { get; set; }
    public double WidgetLeft { get; set; } = double.NaN;
    public double WidgetTop { get; set; } = double.NaN;
    public List<string> HiddenIds { get; set; } = [];

    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BattWatch");

    static string FilePath => Path.Combine(DataDir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch
        {
            // corrupt file → defaults
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }

    // Autostart lives in the registry rather than the JSON so it reflects reality.
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "BattWatch";

    public static bool AutoStart
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunName) is string;
        }
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) k.SetValue(RunName, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(RunName, false);
        }
    }
}
