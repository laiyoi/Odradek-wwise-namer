using System.IO;
using System.Text.Json;

namespace WemLabeler;

public class AppConfig
{
    public string? LastCsvPath { get; set; }
    public string? VgmstreamPath { get; set; }
    public bool AutoPlay { get; set; }
    public string Language { get; set; } = "zh-CN";

    // --- 音频流水线（由 Python 脚本迁移而来）路径配置 ---

    /// <summary>项目根目录，默认自动探测（包含 GraphSoundRes / BankRes 等子目录的目录）。</summary>
    public string? BaseDir { get; set; }

    /// <summary>音频导出目录（对应 export_sounds.py 的 OUTPUT_DIR）。</summary>
    public string? OutputAudioDir { get; set; }

    /// <summary>Streaming WEM 文件所在目录（对应 export_sounds.py 的 WEM_RES_WEM_DIR）。</summary>
    public string? WemResWemDir { get; set; }

    /// <summary>txtp 文件目录，为空时使用 BaseDir\Extracted_Banks\txtp。</summary>
    public string? TxtpDir { get; set; }

    /// <summary>按 ID 导出（export_by_id.py）的输出目录。</summary>
    public string? ExportByIdDir { get; set; }

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
