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

        var wemResOverride = Config.WemResWemDir;
        WemResWemDirOverride = string.IsNullOrWhiteSpace(wemResOverride) ? null : wemResOverride;
        var outOverride = Config.OutputAudioDir;
        OutputDirOverride = string.IsNullOrWhiteSpace(outOverride) ? null : outOverride;
        var byIdOverride = Config.ExportByIdDir;
        ExportByIdDirOverride = string.IsNullOrWhiteSpace(byIdOverride) ? null : byIdOverride;
    }

    public string BaseDir { get; }
    public AppConfig Config { get; }

    public string? WemResWemDirOverride { get; }
    public string? OutputDirOverride { get; }
    public string? ExportByIdDirOverride { get; }

    // --- 输入目录 ---
    //
    // 这里**没有资源 JSON 目录了**。原先的 BankRes / GraphSoundRes / GraphPgmRes / NodeConstRes /
    // WwiseID / WemResJson 六个目录全部取消：
    //   * 链路（GraphSound → GraphProgram → NodeConstants → WwiseID）由 SoundChainResolver
    //     直接从游戏数据解出；
    //   * bank 由 ⓪ 直接从游戏数据写成 .bnk（省掉「从 BankRes 提 BNK」那一步）；
    //   * WemID ↔ .wem 的对应由 ⓪ 写的 wem_index.json 提供。
    // 于是中间产物只剩两类：提取出来的 bank（含 txtp）和提取出来的 wem。

    /// <summary>提取出来的 <c>*.bnk</c>（⓪ 直接写），以及它下面的 <c>txtp/</c>。</summary>
    public string ExtractedBanksDir => Combine("Extracted_Banks");
    public string BanksXml => Path.Combine(ExtractedBanksDir, "banks.xml");

    /// <summary>wwiser 生成的 txtp 目录。固定挂在提取出来的 bank 下面（不是配置项）——
    /// 与仓库根布局一致：<c>&lt;项目根&gt;\Extracted_Banks\txtp</c>。</summary>
    public string TxtpDir => Path.Combine(ExtractedBanksDir, "txtp");

    /// <summary>WEM 文件目录：既是 pipeline 读取 .wem 的输入，也是「导出 WEM 音频」的默认输出。
    /// 默认 <c>&lt;项目根&gt;\WemResWem</c>，可用 config 的 WemResWemDir 指到别处（本项目在 G: 盘）。</summary>
    public string WemResWemDir =>
        WemResWemDirOverride ?? Path.Combine(BaseDir, "WemResWem");

    // --- 输出 ---

    /// <summary>导出音频目录（export_sounds.py 阶段二）。</summary>
    public string OutputDir => OutputDirOverride ?? Path.Combine(BaseDir, "Exported_Audio");

    /// <summary>export_by_id.py 的输出目录。</summary>
    public string ByIdOutputDir => ExportByIdDirOverride ?? Path.Combine(BaseDir, "Decoded_Audio_Split");

    public string MappingJson => MappingJsonOverride ?? Combine("sound_wem_mapping_export.json");
    /// <summary>⓪ 从游戏数据写出的 <c>WemID → 组:下标</c> 索引（顶替原来 7,838 个 WemResJson）。</summary>
    public string WemIndexJson => Combine("wem_index.json");
    public string ProgressFile => Combine("export_progress.json");
    public string StreamingCsv => Combine("streaming_wem_map.csv");
    public string MissingWemCsv => Combine("missing_wem_files.csv");
    public string MappingLog => Combine("mapping_build.log");
    /// <summary>「直接从游戏数据跳链」这一步的报告（总数 / 产出条目 / 缺失明细）。</summary>
    public string ChainReport => ChainReportOverride ?? Combine("soundmap_report.txt");

    /// <summary>
    /// 覆盖 mapping JSON 的输出路径。只给命令行/对比测试用 —— 默认值就是仓库里的基准文件
    /// （sound_wem_mapping_export.json），直接跑会把基准覆盖掉。
    /// </summary>
    public string? MappingJsonOverride { get; set; }

    /// <summary>覆盖链路报告的输出路径，用途同上。</summary>
    public string? ChainReportOverride { get; set; }
    public string UnusedWemCsv => Combine("unused_wem_with_banks.csv");
    public string WemMapCache => Combine("wem_map_cache.json");

    private string Combine(string relative) => Path.Combine(BaseDir, relative);

    /// <summary>
    /// 把流水线要用的各个目录建出来。
    /// 中间产物只剩两类（提取出来的 bank、提取出来的 wem），其余都是本程序自己的输出目录 ——
    /// 它们**都是本程序的产物**，所以不该拿它们是否存在来判断「用户选的目录对不对」。
    /// </summary>
    public void EnsureDirectories()
    {
        foreach (var dir in new[] { BaseDir, ExtractedBanksDir, WemResWemDir })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }
    /// <summary>是否设置了项目根目录（目录确实存在）。</summary>
    public bool HasBaseDir => !string.IsNullOrWhiteSpace(BaseDir) && Directory.Exists(BaseDir);

    /// <summary>
    /// 配置里的路径是否指向一个看起来像项目根目录的位置。
    /// 用作「有没有选对文件夹」的判定：真实用户从别处运行 exe 时探测不到，
    /// 必须靠用户自己指到那一层。
    /// 判定用的是**本程序产物里的稳定名字**（不再是那几个已取消的资源 JSON 目录），
    /// 同时保留对旧目录的识别，这样老用户的配置不会因为升级而失效。
    /// </summary>
    public static bool LooksLikeBaseDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        return Directory.Exists(Path.Combine(dir, "Extracted_Banks"))
            || Directory.Exists(Path.Combine(dir, "WemResWem"))
            || File.Exists(Path.Combine(dir, "sound_wem_mapping_export.json"))
            || File.Exists(Path.Combine(dir, "wem_index.json"))
            || File.Exists(Path.Combine(dir, "unused_wem_with_banks.csv"))
            // 旧布局：升级前留下的目录，仍然认
            || Directory.Exists(Path.Combine(dir, "GraphSoundRes"))
            || Directory.Exists(Path.Combine(dir, "BankRes"))
            || Directory.Exists(Path.Combine(dir, "WemResJson"));
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
