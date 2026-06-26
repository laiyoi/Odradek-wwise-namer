using System.IO;
using System.Text.Json;

namespace WemLabeler;

public class AppConfig
{
    public string? LastCsvPath { get; set; }
    public string? VgmstreamPath { get; set; }
    public bool AutoPlay { get; set; }
    public string Language { get; set; } = "zh-CN";
}

public static class ConfigManager
{
    private static readonly string ConfigPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOpts) ?? new AppConfig();
            }
        }
        catch { }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOpts);
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }

    public static void SaveLocale(string lang)
    {
        var config = Load();
        config.Language = lang;
        Save(config);
    }
}
