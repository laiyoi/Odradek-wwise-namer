using System.Text.Json;
using System.Text.Encodings.Web;

namespace WemLabeler.Pipeline;

/// <summary>对应 py 中 mapping 条目的 AudioSources 元素。</summary>
public sealed class AudioSourceInfo
{
    public long? WemID { get; set; }
    public string SourceType { get; set; } = "Unknown";
    public string? BankFile { get; set; }
    public string? WemRes_Coord { get; set; }
    public string? RawLine { get; set; }
}

/// <summary>对应 export_sounds.py 生成的 sound_wem_mapping_export.json 的条目。</summary>
public sealed class MappingEntry
{
    public string ResourceName { get; set; } = "Unknown";
    public string GraphProgram { get; set; } = "";
    public string ExposedDataResource { get; set; } = "";
    public long WwiseID_Value { get; set; }
    public string WwiseID_Coord { get; set; } = "";
    public string? TXTP_Filename { get; set; }
    public List<AudioSourceInfo> AudioSources { get; set; } = new();
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
