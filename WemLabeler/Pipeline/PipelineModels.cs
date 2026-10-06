using System.Text.Json;
using System.Text.Encodings.Web;

namespace WemLabeler.Pipeline;

/// <summary>mapping 条目的 AudioSources 元素。</summary>
public sealed class AudioSourceInfo
{
    public long? WemID { get; set; }
    public string SourceType { get; set; } = "Unknown";
    public string? BankFile { get; set; }
    /// <summary>
    /// 对应的 <c>WwiseWemResource</c> 对象坐标 <c>组:下标</c>（**不是** WemID）。
    /// 由 wem_index.json 查得；查不到时为 null。
    /// </summary>
    public string? WemRes_Coord { get; set; }
    public string? RawLine { get; set; }
}

/// <summary>
/// sound_wem_mapping_export.json 的条目。
///
/// 只保留「名字 + GraphSoundResource 坐标 + WwiseID + txtp 信息」：中间的
/// GraphProgramResource / NodeConstantsResource 坐标已不需要 —— 跳链直接从游戏数据完成
/// （见 SoundChainResolver），那些 JSON 也不再落盘。
/// </summary>
public sealed class MappingEntry
{
    public string ResourceName { get; set; } = "Unknown";
    /// <summary>GraphSoundResource 的对象坐标 <c>组:下标</c>。</summary>
    public string GraphSound { get; set; } = "";
    public long WwiseID { get; set; }
    public string? TXTP_Filename { get; set; }
    public List<AudioSourceInfo> AudioSources { get; set; } = new();
}

/// <summary>wem_index.json 里一个 WEM 的条目。</summary>
public sealed class WemIndexItem
{
    /// <summary><c>WwiseWemResource</c> 的对象坐标 <c>组:下标</c>。</summary>
    public string Coord { get; set; } = "";
    /// <summary>对应 <c>WwiseWemResource.mLengthInSeconds</c>（约 1,513 个 WEM 有非零值）。</summary>
    public double LengthSeconds { get; set; }
}

/// <summary>
/// wem_index.json：<c>WemID → WwiseWemResource 的对象坐标/流式标志</c>。
/// 读一次游戏就能写出来，用来顶替原来 7,838 个 WemResJson 文件
/// （WemID↔.wem 文件名的对应、来源关联、未使用 WEM 分析都靠它）。
/// </summary>
public sealed class WemIndexFile
{
    public string? WemDir { get; set; }
    public Dictionary<string, WemIndexItem> Wems { get; set; } = new();
    /// <summary>
    /// 所有 <c>WwiseBankResource.WemIDs</c> 的并集（bank 引用到的 WemID）。
    /// 顶替旧版读 BankRes JSON 的 <c>WemIDs</c> 字段 —— 「分析未使用的 WEM」的
    /// <c>FoundInBankRes</c> 列用它，语义与 banks.xml 的媒体表不同。
    /// </summary>
    public List<string> BankWemIDs { get; set; } = new();
}

/// <summary>流水线进度回调载荷。</summary>
public sealed class PipelineProgress
{
    public string Message { get; init; } = "";
    public int Current { get; init; }
    public int Total { get; init; }
}

/// <summary>export_sounds.py 阶段二的导出统计。</summary>
public sealed class ExportReport
{
    public List<string> Success { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<string> Streaming { get; } = new();
    public int Resumed { get; set; }
}

/// <summary>export_by_id.py 的导出统计。</summary>
public sealed class ByIdReport
{
    public List<string> Success { get; } = new();
    public List<string> Failed { get; } = new();
    public List<string> NotFoundTxtp { get; } = new();
}

/// <summary>link_unused_wem.py 的一行结果。</summary>
public sealed class UnusedWemRow
{
    public long WemID { get; set; }
    public string Coord { get; set; } = "";
    public string JsonFile { get; set; } = "";
    public string WemFile { get; set; } = "";
    public string WemPath { get; set; } = "";
    public string FoundInBankRes { get; set; } = "否";
    public string TxtpFiles { get; set; } = "";
}

public static class PipelineJson
{
    /// <summary>
    /// 与 Python 的 json.dump(..., ensure_ascii=False) 对齐：
    /// 不转义非 ASCII 字符，属性名保持原样。
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };
}
