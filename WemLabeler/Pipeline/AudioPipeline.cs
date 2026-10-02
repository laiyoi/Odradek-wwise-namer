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
    private static readonly Regex WemResJsonPattern = new(@"WwiseWemResource_(\d+)_(\d+)\.json$", RegexOptions.Compiled);
    private static readonly Regex GraphSoundPattern = new(@"GraphSoundResource_(\d+)_(\d+)\.json$", RegexOptions.Compiled);
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
    /// 对应 build_wem_res_wem_index：WemResJson 中的 WemID → WemResWem 里的 .wem 路径。
    /// </summary>
    internal static Dictionary<uint, string> BuildWemResWemIndex(PipelinePaths paths)
    {
        var index = new Dictionary<uint, string>();
        if (!Directory.Exists(paths.WemResWemDir)) return index;

        foreach (var file in Directory.EnumerateFiles(paths.WemResWemDir, "*.wem"))
        {
            var m = WemFilePattern.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            var jsonPath = Path.Combine(paths.WemResJsonDir,
                $"WwiseWemResource_{m.Groups[1].Value}_{m.Groups[2].Value}.json");
            if (!File.Exists(jsonPath)) continue;
            using var doc = LoadJsonDocument(jsonPath);
            if (doc == null) continue;
            var rawId = JsonLong(doc.RootElement, "WemID");
            if (rawId.HasValue) index[ToU32(rawId.Value)] = file;
        }
        return index;
    }

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

        var map = new Dictionary<uint, string>();
        if (!Directory.Exists(paths.WemResJsonDir)) return map;

        var files = Directory.EnumerateFiles(paths.WemResJsonDir, "*.json").ToList();
        int handled = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            handled++;
            var m = WemResJsonPattern.Match(Path.GetFileName(file));
            if (!m.Success) continue;
            using var doc = LoadJsonDocument(file);
            if (doc == null) continue;

            var root = doc.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };

            foreach (var entry in entries)
            {
                var rawId = JsonLong(entry, "WemID");
                if (!rawId.HasValue) continue;
                var wemFile = Path.Combine(paths.WemResWemDir,
                    $"WwiseWemResource_{m.Groups[1].Value}_{m.Groups[2].Value}.wem");
                if (File.Exists(wemFile)) map[ToU32(rawId.Value)] = wemFile;
            }

            if (handled % 500 == 0)
                Report(null, $"[wem_map] {handled}/{files.Count}");
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

    /// <summary>WemID → 坐标 / JSON 文件名。</summary>
    internal sealed record WemResInfo(string Coord, string JsonFile);

    internal static Dictionary<uint, WemResInfo> BuildWemResJsonIndex(PipelinePaths paths, Action<string>? log)
    {
        var index = new Dictionary<uint, WemResInfo>();
        if (!Directory.Exists(paths.WemResJsonDir))
        {
            SafeLog(log, $"[!] 找不到 WemResJson 目录: {paths.WemResJsonDir}");
            return index;
        }

        foreach (var file in Directory.EnumerateFiles(paths.WemResJsonDir, "WwiseWemResource_*.json"))
        {
            try
            {
                var parts = Path.GetFileNameWithoutExtension(file).Split('_');
                if (parts.Length < 3) continue;
                using var doc = LoadJsonDocument(file);
                if (doc == null) continue;
                var rawId = JsonLong(doc.RootElement, "WemID");
                if (!rawId.HasValue) continue;
                index[ToU32(rawId.Value)] = new WemResInfo(
                    $"{parts[1]}:{parts[2]}",
                    Path.GetFileName(file));
            }
            catch { }
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

    /// <summary>对应 build_bank_res_wem_ids：BankRes 的 WemIDs 字段并集。</summary>
    internal static HashSet<uint> BuildBankResWemIds(PipelinePaths paths, Action<string>? log)
    {
        var ids = new HashSet<uint>();
        if (!Directory.Exists(paths.BankResDir))
        {
            SafeLog(log, $"[!] 找不到 BankRes 目录: {paths.BankResDir}");
            return ids;
        }

        foreach (var file in Directory.EnumerateFiles(paths.BankResDir, "WwiseBankResource_*.json")
                     .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            try
            {
                using var doc = LoadJsonDocument(file);
                if (doc == null) continue;
                if (!doc.RootElement.TryGetProperty("WemIDs", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in arr.EnumerateArray())
                    if (el.TryGetInt64(out var raw)) ids.Add(ToU32(raw));
            }
            catch (Exception ex)
            {
                SafeLog(log, $"[!] 解析 {Path.GetFileName(file)} 失败: {ex.Message}");
            }
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

    #region BNK 提取（extract_bnk_from_json.py）

    /// <summary>
    /// 从 BankRes 的 JSON 中解码 Base64 的 BankData，修复 Wwise Bank 的对齐问题后
    /// 写入 Extracted_Banks。返回成功提取的数量。
    /// </summary>
    public static int ExtractBanks(PipelinePaths paths, IProgress<PipelineProgress>? progress, Action<string>? log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ExtractedBanksDir);

        if (!Directory.Exists(paths.BankResDir))
        {
            SafeLog(log, $"[!] 找不到 BankRes 目录: {paths.BankResDir}");
            return 0;
        }

        var files = Directory.EnumerateFiles(paths.BankResDir, "*.json")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        Report(progress, Locale.S("pipe_bnk_start", files.Count), 0, files.Count);

        int count = 0, handled = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            handled++;
            var name = Path.GetFileName(file);
            try
            {
                using var doc = LoadJsonDocument(file);
                var root = doc?.RootElement;
                string? raw = null;
                if (root.HasValue)
                {
                    raw = JsonString(root.Value, "BankData");
                    if (string.IsNullOrEmpty(raw) &&
                        root.Value.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Object)
                    {
                        raw = JsonString(data, "BankData");
                    }
                }

                if (!string.IsNullOrEmpty(raw))
                {
                    var binary = SmartDecode(raw, log);
                    if (binary != null && binary.Length >= 4 &&
                        binary[0] == (byte)'B' && binary[1] == (byte)'K' &&
                        binary[2] == (byte)'H' && binary[3] == (byte)'D')
                    {
                        var outPath = Path.Combine(paths.ExtractedBanksDir, Path.ChangeExtension(name, ".bnk"));
                        File.WriteAllBytes(outPath, binary);
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                SafeLog(log, $"[错误] 处理 {name}: {ex.Message}");
            }

            if (handled % 5 == 0 || handled == files.Count)
                Report(progress, $"[{handled}/{files.Count}] {name}", handled, files.Count);
        }

        SafeLog(log, Locale.S("pipe_bnk_done", count));
        return count;
    }

    private static byte[]? SmartDecode(string b64Str, Action<string>? log)
    {
        var text = b64Str;
        var missingPadding = text.Length % 4;
        if (missingPadding != 0) text += new string('=', 4 - missingPadding);

        try
        {
            return FixWwiseData(Convert.FromBase64String(text));
        }
        catch (Exception ex)
        {
            // 容错：剔除空白/换行等非 Base64 字符后重试一次
            try
            {
                var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
                missingPadding = cleaned.Length % 4;
                if (missingPadding != 0) cleaned += new string('=', 4 - missingPadding);
                return FixWwiseData(Convert.FromBase64String(cleaned));
            }
            catch
            {
                SafeLog(log, string.Format(Locale.S("pipe_bnk_b64_fail"), ex.Message));
                return null;
            }
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
