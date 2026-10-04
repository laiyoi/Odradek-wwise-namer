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

    /// <summary>Streaming WEM 文件所在目录（对应 export_sounds.py 的 WEM_RES_WEM_DIR）。
    /// 同时也是「导出 WEM 音频」的默认输出目录 —— 两者是同一种 .wem，没必要分成两个配置项。</summary>
    public string? WemResWemDir { get; set; }

    /// <summary>按 ID 导出（export_by_id.py）的输出目录。</summary>
    public string? ExportByIdDir { get; set; }

    // --- 直接读游戏文件导出 ---

    /// <summary>DS2 游戏根目录（含 DS2.exe 的那一层），为空时自动探测。</summary>
    public string? GameRoot { get; set; }

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
