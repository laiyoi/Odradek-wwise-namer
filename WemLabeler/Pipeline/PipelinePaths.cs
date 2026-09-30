using System.IO;

namespace WemLabeler.Pipeline;

/// <summary>
/// 复刻 Python 脚本使用的目录结构。
/// 所有路径都由项目根目录（BaseDir）推导，个别路径可由 config.json 覆盖，
/// 这样迁移过来的 C# 实现与原来的 py 脚本读写同一批文件。
/// </summary>
public sealed class PipelinePaths
{
    public PipelinePaths(string baseDir, AppConfig? config = null)
    {
        BaseDir = baseDir;
        Config = config ?? new AppConfig();

        var txtpOverride = Config.TxtpDir;
        TxtpDirOverride = string.IsNullOrWhiteSpace(txtpOverride) ? null : txtpOverride;
        var wemResOverride = Config.WemResWemDir;
        WemResWemDirOverride = string.IsNullOrWhiteSpace(wemResOverride) ? null : wemResOverride;
        var outOverride = Config.OutputAudioDir;
        OutputDirOverride = string.IsNullOrWhiteSpace(outOverride) ? null : outOverride;
        var byIdOverride = Config.ExportByIdDir;
        ExportByIdDirOverride = string.IsNullOrWhiteSpace(byIdOverride) ? null : byIdOverride;
    }

    public string BaseDir { get; }
    public AppConfig Config { get; }

    public string? TxtpDirOverride { get; }
    public string? WemResWemDirOverride { get; }
    public string? OutputDirOverride { get; }
    public string? ExportByIdDirOverride { get; }

    // --- 输入目录（与 README 步骤 1 的导出路径一致）---

    public string BankResDir => Combine("BankRes");
    public string GraphSoundResDir => Combine("GraphSoundRes");
    public string GraphPgmResDir => Combine("GraphPgmRes");
    public string NodeConstResDir => Combine("NodeConstRes");
    public string WwiseIdDir => Combine("WwiseID");
    public string WemResJsonDir => Combine("WemResJson");

    public string ExtractedBanksDir => Combine("Extracted_Banks");
    public string BanksXml => Path.Combine(ExtractedBanksDir, "banks.xml");

    /// <summary>wwiser 生成的 txtp 目录，可由 config 的 TxtpDir 覆盖。</summary>
    public string TxtpDir => TxtpDirOverride ?? Path.Combine(ExtractedBanksDir, "txtp");

    /// <summary>Streaming WEM 文件目录（原 py 中硬编码为 G:\ds2_unpack\wems\WemResWem）。</summary>
    public string WemResWemDir =>
        WemResWemDirOverride ?? Path.Combine(BaseDir, "WemResWem");

    // --- 输出 ---

    /// <summary>导出音频目录（export_sounds.py 阶段二）。</summary>
    public string OutputDir => OutputDirOverride ?? Path.Combine(BaseDir, "Exported_Audio");

    /// <summary>export_by_id.py 的输出目录。</summary>
    public string ByIdOutputDir => ExportByIdDirOverride ?? Path.Combine(BaseDir, "Decoded_Audio_Split");

    public string MappingJson => Combine("sound_wem_mapping_export.json");
    public string ProgressFile => Combine("export_progress.json");
    public string StreamingCsv => Combine("streaming_wem_map.csv");
    public string MissingWemCsv => Combine("missing_wem_files.csv");
    public string MappingLog => Combine("mapping_build.log");
    public string UnusedWemCsv => Combine("unused_wem_with_banks.csv");
    public string WemMapCache => Combine("wem_map_cache.json");

    private string Combine(string relative) => Path.Combine(BaseDir, relative);

    /// <summary>是否设置了项目根目录（目录确实存在）。</summary>
    public bool HasBaseDir => !string.IsNullOrWhiteSpace(BaseDir) && Directory.Exists(BaseDir);

    /// <summary>
    /// 配置里的路径是否指向一个看起来像项目根目录的位置。
    /// 用作「有没有选对文件夹」的判定：真实用户从别处运行 exe 时探测不到，
    /// 必须靠用户自己指到导出资源的那一层。
    /// </summary>
    public static bool LooksLikeBaseDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return Directory.Exists(Path.Combine(dir, "GraphSoundRes"))
            || Directory.Exists(Path.Combine(dir, "BankRes"))
            || Directory.Exists(Path.Combine(dir, "WemResJson"))
            || Directory.Exists(Path.Combine(dir, "Extracted_Banks"));
    }

    /// <summary>
    /// 依次尝试：已保存的 BaseDir → 已加载 CSV 所在目录及其祖先 →
    /// exe 所在目录及其祖先。返回第一个能被识别为项目根目录的路径，否则返回 null。
    /// </summary>
    public static string? DetectBaseDir(string? configured, string? csvPath)
    {
        if (LooksLikeBaseDir(configured)) return configured;

        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            var fromCsv = WalkUp(Path.GetDirectoryName(csvPath));
            if (fromCsv != null) return fromCsv;
        }

        return WalkUp(AppDomain.CurrentDomain.BaseDirectory);
    }

    private static string? WalkUp(string? start)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;
        var current = new DirectoryInfo(start);
        for (int depth = 0; depth < 8 && current != null; depth++)
        {
            if (LooksLikeBaseDir(current.FullName)) return current.FullName;
            current = current.Parent;
        }
        return null;
    }
}
