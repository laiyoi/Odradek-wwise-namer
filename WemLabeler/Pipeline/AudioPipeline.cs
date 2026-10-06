using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace WemLabeler.Pipeline;

/// <summary>
/// 由 Python 脚本迁移而来的音频处理流水线。
///
/// 对应关系：
///   ExtractBanks       ← extract_bnk_from_json.py
///   BuildMapping       ← export_sounds.py 阶段一（含 missing_wem_files.csv）
///   ExportFromMapping  ← export_sounds.py 阶段二
///   ExportByIds        ← export_by_id.py
///   BuildUnusedWemCsv  ← link_unused_wem.py
///
/// 输出文件与原来 py 脚本完全一致，两个实现可以混用。
/// </summary>
public static partial class AudioPipeline
{
    private static readonly Regex RefPattern = new(@"<ref to\s+(\d+):(\d+)>", RegexOptions.Compiled);
    private static readonly Regex WemFilePattern = new(@"WwiseWemResource_(\d+)_(\d+)\.wem$", RegexOptions.Compiled);
    private static readonly Regex TxtpWemPattern = new(@"(?:##|wem/)(\d+)\.wem", RegexOptions.Compiled);
    private static readonly Regex TxtpWemIdPattern = new(@"##(\d+)\.wem", RegexOptions.Compiled);
    private static readonly Regex TxtpWemIdAltPattern = new(@"wem/(\d+)\.wem", RegexOptions.Compiled);
    private static readonly Regex TxtpEventPattern = new(@"CAkEvent\[(\d+)\]\s+(\d+)", RegexOptions.Compiled);
    private static readonly Regex InvalidFileNameChars = new("[\\\\/*?:\"<>|]", RegexOptions.Compiled);

    private static readonly byte[][] WwiseChunkTags =
    {
        "BKHD"u8.ToArray(), "HIRC"u8.ToArray(), "DIDX"u8.ToArray(), "DATA"u8.ToArray(),
        "STID"u8.ToArray(), "ENVS"u8.ToArray(), "PLAT"u8.ToArray(), "INIT"u8.ToArray()
    };

    #region 通用工具

    /// <summary>把有符号数按 32 位截断成无符号（等价 Python 的 `x &amp; 0xFFFFFFFF`）。</summary>
    public static uint ToU32(long value) => unchecked((uint)value);

    internal static (int Group, int Index)? ParseRef(string? refStr)
    {
        if (string.IsNullOrEmpty(refStr)) return null;
        var m = RefPattern.Match(refStr);
        if (!m.Success) return null;
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    internal static string SanitizeFileName(string name) => InvalidFileNameChars.Replace(name, "_");

    internal static void Report(IProgress<PipelineProgress>? progress, string message, int current = 0, int total = 0) =>
        progress?.Report(new PipelineProgress { Message = message, Current = current, Total = total });

    internal static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); } catch { }
    }

    #endregion

    #region vgmstream / 解码

    /// <summary>用 vgmstream 把输入（wem / txtp）解码为指定的 WAV 文件。</summary>
    private static bool RunVgmstream(string vgmstreamPath, string outputPath, string inputPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = vgmstreamPath,
                Arguments = $"-o \"{outputPath}\" \"{inputPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            var stdOut = proc.StandardOutput.ReadToEndAsync();
            var stdErr = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            _ = stdOut.GetAwaiter().GetResult();
            _ = stdErr.GetAwaiter().GetResult();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>对应 py 的 export_audio：把 txtp 内容写成临时 txtp 再解码。</summary>
    private static bool ExportAudio(string vgmstreamPath, string outputDir, string txtpContent, string outputWav)
    {
        var tempTxtp = Path.Combine(outputDir, $"_temp_{Guid.NewGuid():N}.txtp");
        try
        {
            File.WriteAllText(tempTxtp, txtpContent, new UTF8Encoding(false));
            return RunVgmstream(vgmstreamPath, outputWav, tempTxtp);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tempTxtp)) File.Delete(tempTxtp); } catch { }
        }
    }

    /// <summary>对应 py 的 export_wem_file。</summary>
    private static bool ExportWemFile(string vgmstreamPath, string wemPath, string outputWav) =>
        RunVgmstream(vgmstreamPath, outputWav, wemPath);

    #endregion

    #region 索引构建

    /// <summary>对应 build_txtp_index：CAkEvent[..] N 中的 N → txtp 路径。</summary>
    internal static Dictionary<uint, string> BuildTxtpEventIndex(PipelinePaths paths)
    {
        var index = new Dictionary<uint, string>();
        if (!Directory.Exists(paths.TxtpDir)) return index;
        foreach (var file in Directory.EnumerateFiles(paths.TxtpDir, "*.txtp"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var m = TxtpEventPattern.Match(content);
                if (m.Success)
                    index[ToU32(long.Parse(m.Groups[2].Value))] = file;
            }
            catch { }
        }
        return index;
    }

    /// <summary>
    /// WemID → WemResWem 里的 .wem 路径，由 ⓪ 写出的 <c>wem_index.json</c> 推导。
    /// （旧实现是去读 7,838 个 WemResJson 拿 WemID，现在那些 JSON 已经不存在了。）
    /// </summary>
    internal static Dictionary<uint, string> BuildWemResWemIndex(PipelinePaths paths) =>
        BuildWemPaths(paths, LoadWemIndex(paths));

    /// <summary>对应 export_by_id.py 的 build_wem_map：WemID → WemResWem 路径（带缓存文件）。</summary>
    internal static Dictionary<uint, string> BuildWemMapWithCache(PipelinePaths paths, bool forceRefresh,
        Action<string>? log, CancellationToken ct)
    {
        var cachePath = paths.WemMapCache;
        if (!forceRefresh && File.Exists(cachePath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                var cached = new Dictionary<uint, string>();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (uint.TryParse(prop.Name, out var key) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(value)) cached[key] = value;
                    }
                }
                if (cached.Count > 0) return cached;
            }
            catch { }
        }

        var map = BuildWemPaths(paths, LoadWemIndex(paths));
        if (map.Count == 0)
        {
            SafeLog(log, Locale.S("pipe_wemindex_missing", paths.WemIndexJson));
            return map;
        }

        try
        {
            var dict = map.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, PipelineJson.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeLog(log, $"写入 wem_map_cache.json 失败: {ex.Message}");
        }

        return map;
    }

    /// <summary>WemID → 坐标 / 对象名（对象名沿用旧的 <c>WwiseWemResource_g_i.json</c> 形式，仅作标识）。</summary>
    internal sealed record WemResInfo(string Coord, string JsonFile);

    internal static Dictionary<uint, WemResInfo> BuildWemResJsonIndex(PipelinePaths paths, Action<string>? log)
    {
        var index = new Dictionary<uint, WemResInfo>();
        var raw = LoadWemIndex(paths);
        if (raw.Count == 0)
            SafeLog(log, Locale.S("pipe_wemindex_missing", paths.WemIndexJson));

        foreach (var (wemId, coord) in raw)
        {
            var parts = coord.Split(':');
            if (parts.Length != 2) continue;
            index[wemId] = new WemResInfo(coord, $"WwiseWemResource_{parts[0]}_{parts[1]}.json");
        }
        return index;
    }

    /// <summary>坐标 → wem 文件名（link_unused_wem.py 的 build_wem_res_wem_index）。</summary>
    internal static Dictionary<string, string> BuildWemFileByCoordIndex(PipelinePaths paths)
    {
        var index = new Dictionary<string, string>();
        if (!Directory.Exists(paths.WemResWemDir)) return index;
        foreach (var file in Directory.EnumerateFiles(paths.WemResWemDir, "*.wem"))
        {
            var m = WemFilePattern.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            index[$"{m.Groups[1].Value}:{m.Groups[2].Value}"] = Path.GetFileName(file);
        }
        return index;
    }

    /// <summary>
    /// 「这个 WEM 是否被某个 bank 引用」的集合。
    ///
    /// 旧实现读 BankRes JSON 的 <c>WemIDs</c> 字段；BankRes 已取消，改读 ⓪ 写进
    /// <c>wem_index.json</c> 的 <c>BankWemIDs</c>（同样是 <c>WwiseBankResource.WemIDs</c> 的并集，
    /// 语义完全一致）。**不能**用 banks.xml 的媒体表代替 —— 那是「内嵌在 bank 里的媒体」，
    /// 是另一个集合。
    /// </summary>
    internal static HashSet<uint> BuildBankResWemIds(PipelinePaths paths, Action<string>? log)
    {
        var ids = new HashSet<uint>();
        if (!File.Exists(paths.WemIndexJson))
        {
            SafeLog(log, Locale.S("pipe_wemindex_missing", paths.WemIndexJson));
            return ids;
        }
        try
        {
            var file = JsonSerializer.Deserialize<WemIndexFile>(File.ReadAllText(paths.WemIndexJson),
                PipelineJson.Options);
            if (file?.BankWemIDs != null)
                foreach (var raw in file.BankWemIDs)
                    if (uint.TryParse(raw, out var id)) ids.Add(id);
        }
        catch (Exception ex)
        {
            SafeLog(log, $"[!] 解析 {Path.GetFileName(paths.WemIndexJson)} 失败: {ex.Message}");
        }
        return ids;
    }

    /// <summary>对应 build_txtp_wem_index：WemID → 引用它的 txtp 文件名列表。</summary>
    internal static Dictionary<uint, List<string>> BuildTxtpWemIndex(PipelinePaths paths, Action<string>? log)
    {
        var index = new Dictionary<uint, List<string>>();
        if (!Directory.Exists(paths.TxtpDir))
        {
            SafeLog(log, $"[!] 找不到 txtp 目录: {paths.TxtpDir}");
            return index;
        }

        foreach (var file in Directory.EnumerateFiles(paths.TxtpDir, "*.txtp"))
        {
            try
            {
                var content = File.ReadAllText(file);
                var name = Path.GetFileName(file);
                foreach (Match m in TxtpWemPattern.Matches(content))
                {
                    var id = ToU32(long.Parse(m.Groups[1].Value));
                    if (!index.TryGetValue(id, out var list))
                    {
                        list = new List<string>();
                        index[id] = list;
                    }
                    list.Add(name);
                }
            }
            catch { }
        }
        return index;
    }

    /// <summary>对应 build_used_wem_ids：从 sound_wem_mapping_export.json 收集已使用的 WemID。</summary>
    internal static HashSet<uint> BuildUsedWemIds(PipelinePaths paths, Action<string>? log)
    {
        var used = new HashSet<uint>();
        if (!File.Exists(paths.MappingJson))
        {
            SafeLog(log, $"[!] 找不到 {Path.GetFileName(paths.MappingJson)}");
            return used;
        }
        try
        {
            var entries = JsonSerializer.Deserialize<List<MappingEntry>>(
                File.ReadAllText(paths.MappingJson), PipelineJson.Options);
            if (entries == null) return used;
            foreach (var entry in entries)
                foreach (var src in entry.AudioSources)
                    if (src.WemID.HasValue) used.Add(ToU32(src.WemID.Value));
        }
        catch (Exception ex)
        {
            SafeLog(log, $"[!] 解析 {Path.GetFileName(paths.MappingJson)} 失败: {ex.Message}");
        }
        return used;
    }

    /// <summary>
    /// 对应 build_bank_media_index：扫描 banks.xml（约 515MB）得到
    /// WemID → 所在的 bank 文件名列表。必须流式解析，不能整棵加载。
    /// </summary>
    internal static Dictionary<uint, List<string>> BuildBankMediaIndex(PipelinePaths paths, Action<string>? log)
    {
        var mediaToBankIndices = new Dictionary<uint, List<int>>();
        var bankNames = new List<string>();
        var bankNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(paths.BanksXml)) return new Dictionary<uint, List<string>>();

        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true,
                IgnoreProcessingInstructions = true
            };

            string? bankName = null;
            int currentBankIndex = -1;
            int depth = 0, rootDepth = -1, mediaDepth = -1;
            bool insideRoot = false, insideMediaHeader = false;

            using var stream = new FileStream(paths.BanksXml, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var reader = XmlReader.Create(stream, settings);

            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    depth++;
                    var name = reader.LocalName;

                    if (name == "root")
                    {
                        bankName = reader.GetAttribute("filename");
                        insideRoot = true;
                        rootDepth = depth;
                        if (!string.IsNullOrEmpty(bankName))
                        {
                            if (!bankNameToIndex.TryGetValue(bankName, out currentBankIndex))
                            {
                                currentBankIndex = bankNames.Count;
                                bankNames.Add(bankName);
                                bankNameToIndex[bankName] = currentBankIndex;
                            }
                        }
                        else
                        {
                            currentBankIndex = -1;
                        }
                    }
                    else if (insideRoot && name == "obj" && reader.GetAttribute("na") == "MediaHeader")
                    {
                        insideMediaHeader = true;
                        mediaDepth = depth;
                    }
                    else if (insideMediaHeader && name == "fld" && reader.GetAttribute("na") == "id")
                    {
                        var value = reader.GetAttribute("value") ?? reader.GetAttribute("va");
                        if (value != null && currentBankIndex >= 0 && long.TryParse(value.Trim(), out var mediaId))
                        {
                            var key = ToU32(mediaId);
                            if (!mediaToBankIndices.TryGetValue(key, out var list))
                            {
                                list = new List<int>();
                                mediaToBankIndices[key] = list;
                            }
                            list.Add(currentBankIndex);
                        }
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (insideMediaHeader && depth == mediaDepth) insideMediaHeader = false;
                    else if (insideRoot && depth == rootDepth)
                    {
                        insideRoot = false;
                        bankName = null;
                        currentBankIndex = -1;
                    }
                    depth--;
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog(log, $"加载 banks.xml 失败: {ex.Message}");
        }

        var result = new Dictionary<uint, List<string>>(mediaToBankIndices.Count);
        foreach (var kv in mediaToBankIndices)
        {
            var names = new List<string>(kv.Value.Count);
            foreach (var i in kv.Value) names.Add(bankNames[i]);
            result[kv.Key] = names;
        }
        return result;
    }

    #endregion

    #region BNK（由 ⓪ 直读游戏数据写出）

    // 「从 BankRes 的 JSON 解码 Base64 BankData」这一步已取消：⓪ 在读取游戏文件时直接把
    // WwiseBankResource.BankData 落成 .bnk（见 OdradekExporter.ExportBanks），
    // 修复逻辑仍是下面的 FixWwiseData，产物与旧步骤逐字节一致。

    /// <summary>
    /// 直接把游戏里读到的 <c>WwiseBankResource.BankData</c> 变成可写盘的 bank 二进制。
    /// 走的是与「从 Base64 JSON 提取」**同一段** <see cref="FixWwiseData"/>，
    /// 所以 ⓪ 直读游戏数据产出的 .bnk 与旧步骤逐字节一致。
    /// </summary>
    internal static byte[]? FixWwiseBankData(object? raw)
    {
        switch (raw)
        {
            case byte[] bytes:
                return FixWwiseData(bytes);
            case IReadOnlyList<object?> list when list.Count > 0:
            {
                var buffer = new byte[list.Count];
                for (var i = 0; i < list.Count; i++)
                    buffer[i] = Convert.ToByte(list[i], System.Globalization.CultureInfo.InvariantCulture);
                return FixWwiseData(buffer);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// 对应 py 的 fix_wwise_data：定位最后一个已知 Wwise Chunk，
    /// 按其声明长度补齐被截断的数据或裁掉多余脏数据。
    /// </summary>
    private static byte[] FixWwiseData(byte[] raw)
    {
        long lastTagPos = -1;
        foreach (var tag in WwiseChunkTags)
        {
            var pos = LastIndexOf(raw, tag);
            if (pos > lastTagPos) lastTagPos = pos;
        }

        if (lastTagPos < 0) return raw;

        try
        {
            var p = (int)lastTagPos;
            if (p + 8 > raw.Length) return raw;
            var declaredLen = BitConverter.ToUInt32(raw, p + 4);
            var theoreticalEnd = lastTagPos + 8 + declaredLen;
            var actualLen = raw.LongLength;

            if (actualLen < theoreticalEnd)
            {
                var padded = new byte[theoreticalEnd];
                Buffer.BlockCopy(raw, 0, padded, 0, raw.Length);
                return padded;
            }

            if (actualLen > theoreticalEnd)
            {
                var truncated = new byte[theoreticalEnd];
                Buffer.BlockCopy(raw, 0, truncated, 0, (int)theoreticalEnd);
                return truncated;
            }
        }
        catch
        {
            // 与 py 一致：修复失败时返回原数据
        }

        return raw;
    }

    private static long LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (long i = haystack.LongLength - needle.LongLength; i >= 0; i--)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    #endregion

    #region JSON 读取小工具

    internal static string JsonScalarToString(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.True => "True",
        JsonValueKind.False => "False",
        JsonValueKind.Null => "",
        _ => el.ToString()
    };

    internal static string? JsonString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        return JsonScalarToString(el);
    }

    internal static long? JsonLong(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v)) return v;
        if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var sv)) return sv;
        return null;
    }

    internal static JsonDocument? LoadJsonDocument(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch
        {
            return null;
        }
    }

    #endregion
}
