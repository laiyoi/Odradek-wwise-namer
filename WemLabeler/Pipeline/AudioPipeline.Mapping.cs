using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OdradekSharp.Ds2;

namespace WemLabeler.Pipeline;

public static partial class AudioPipeline
{
    /// <summary>阶段一（build_mapping）的统计结果。</summary>
    public sealed class MappingBuildResult
    {
        public List<MappingEntry> MappingData { get; } = new();
        public int Skipped { get; set; }
        public int Errors { get; set; }
        public int UsedWemCount { get; set; }
        public int TotalFiles { get; set; }
        public double ElapsedSeconds { get; set; }
    }

    /// <summary>
    /// ③ 构建音频映射表。
    ///
    /// 这一步**不读游戏、也不读任何资源 JSON**：⓪ 已经把链路
    /// （ResourceName / GraphSound 坐标 / WwiseID）写进了 <c>sound_wem_mapping_export.json</c>；
    /// 这里只做 txtp 富化 —— 查 txtp 事件索引、bank 媒体索引和 <c>wem_index.json</c>，
    /// 补上 TXTP_Filename / AudioSources（含每个音频源对应的 WwiseWemResource 坐标），覆写同一个文件。
    /// </summary>
    public static MappingBuildResult BuildMapping(PipelinePaths paths, IProgress<PipelineProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        var result = new MappingBuildResult();
        var startTime = DateTime.UtcNow;

        var mappingLog = paths.MappingLog;
        try { if (File.Exists(mappingLog)) File.Delete(mappingLog); } catch { }
        void LogToFile(string message)
        {
            try { File.AppendAllText(mappingLog, message + Environment.NewLine, new UTF8Encoding(false)); } catch { }
        }

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_mapping_start"));
        SafeLog(log, new string('=', 50));

        // ---------------------------------------------------------------- 读 ⓪ 写下的「链部分」
        var chainEntries = LoadChainEntries(paths, log);
        if (chainEntries == null)
        {
            result.Errors++;
            return result;
        }
        result.TotalFiles = chainEntries.Count;
        SafeLog(log, Locale.S("pipe_mapping_chain_loaded", chainEntries.Count, paths.MappingJson));

        Report(progress, Locale.S("pipe_mapping_indexing"));

        var txtpIndex = BuildTxtpEventIndex(paths);
        var wemIndex = LoadWemIndex(paths);                       // WemID → WwiseWemResource 坐标
        var wemPaths = BuildWemPaths(paths, wemIndex);            // WemID → .wem 实际路径（存在才算）
        var bankMediaIndex = BuildBankMediaIndex(paths, log);

        var usedWemIds = new HashSet<uint>();

        SafeLog(log, Locale.S("pipe_mapping_index_summary",
            txtpIndex.Count, wemPaths.Count, bankMediaIndex.Count));

        int i = 0;
        foreach (var chainEntry in chainEntries)
        {
            ct.ThrowIfCancellationRequested();
            i++;

            var resourceName = chainEntry.ResourceName;
            var wwiseId = ToU32(chainEntry.WwiseID);

            var entry = new MappingEntry
            {
                ResourceName = resourceName,
                GraphSound = chainEntry.GraphSound,
                WwiseID = chainEntry.WwiseID,
            };

            var fileResults = new List<string>();
            var logEntries = new List<string>();

            if (!txtpIndex.TryGetValue(wwiseId, out var txtpPath))
            {
                result.Skipped++;
                entry.TXTP_Filename = "NOT_FOUND";

                var bankInfo = "";
                if (bankMediaIndex.TryGetValue(wwiseId, out var banks))
                {
                    bankInfo = $" (Banks: {string.Join(", ", banks)})";
                    foreach (var bankFilename in banks)
                    {
                        entry.AudioSources.Add(new AudioSourceInfo
                        {
                            WemID = wwiseId,
                            SourceType = "TXTP_NotFound_BankKnown",
                            BankFile = bankFilename,
                            WemRes_Coord = wemIndex.GetValueOrDefault(wwiseId)
                        });
                    }
                    fileResults.Add($"无TXTP[{string.Join(", ", banks.Select(b => Path.GetFileName(b)))}]");
                }
                else
                {
                    entry.AudioSources.Add(new AudioSourceInfo
                    {
                        WemID = wwiseId,
                        SourceType = "TXTP_NotFound_BankUnknown",
                        WemRes_Coord = wemIndex.GetValueOrDefault(wwiseId)
                    });
                    fileResults.Add("无TXTP");
                }

                result.MappingData.Add(entry);
                logEntries.Add($"TXTP未找到: {resourceName} (WwiseID: {wwiseId}){bankInfo}");

                SafeLog(log, $"[{i}/{chainEntries.Count}] {resourceName} | {string.Join(" | ", fileResults)}");
                if ((i & 0x3F) == 0)
                    Report(progress, $"[{i}/{chainEntries.Count}] {resourceName}", i, chainEntries.Count);
                foreach (var logEntry in logEntries) LogToFile(logEntry);
                continue;
            }

            entry.TXTP_Filename = Path.GetFileName(txtpPath);

            string[] lines;
            try
            {
                lines = File.ReadAllLines(txtpPath);
            }
            catch
            {
                result.Errors++;
                continue;
            }

            var layers = new List<string>();
            foreach (var raw in lines)
            {
                var stripped = raw.Trim();
                if (string.IsNullOrEmpty(stripped)) continue;
                if (stripped.StartsWith("../") || stripped.StartsWith("wem/") || TxtpWemIdPattern.IsMatch(stripped))
                {
                    if (!stripped.StartsWith("group =") && !stripped.StartsWith(".plugin-"))
                        layers.Add(stripped);
                }
            }

            if (layers.Count == 0)
            {
                result.Skipped++;
                entry.AudioSources.Add(new AudioSourceInfo { SourceType = "NoAudioLayers" });
                result.MappingData.Add(entry);
                logEntries.Add($"无音频层: {resourceName} (WwiseID: {wwiseId})");

                SafeLog(log, $"[{i}/{chainEntries.Count}] {resourceName} | 无音频层");
                if ((i & 0x3F) == 0)
                    Report(progress, $"[{i}/{chainEntries.Count}] {resourceName}", i, chainEntries.Count);
                foreach (var logEntry in logEntries) LogToFile(logEntry);
                continue;
            }

            var layerResults = new List<string>();
            var hasError = false;

            for (int idx = 0; idx < layers.Count; idx++)
            {
                var layerLine = layers[idx];
                var idMatch = TxtpWemIdPattern.Match(layerLine);
                if (!idMatch.Success) idMatch = TxtpWemIdAltPattern.Match(layerLine);
                var wemName = idMatch.Success ? idMatch.Groups[1].Value : $"L{idx}";
                uint? u32Id = long.TryParse(wemName, out var parsedId) ? ToU32(parsedId) : null;

                var strippedLine = layerLine.TrimStart('?', ' ').Trim();
                var cleanLine = strippedLine.Split("##fade")[0].Trim();

                var source = new AudioSourceInfo
                {
                    WemID = u32Id.HasValue ? u32Id.Value : null,
                    SourceType = "Unknown",
                    RawLine = cleanLine,
                    WemRes_Coord = u32Id.HasValue ? wemIndex.GetValueOrDefault(u32Id.Value) : null
                };

                if (cleanLine.StartsWith("../"))
                {
                    var rawPath = cleanLine.Split(" #")[0].Trim();
                    source.SourceType = "Embedded";
                    source.BankFile = Path.GetFileName(rawPath);
                    layerResults.Add($"E{idx}");
                    if (u32Id.HasValue) usedWemIds.Add(u32Id.Value);
                }
                else if (cleanLine.StartsWith("wem/"))
                {
                    if (u32Id.HasValue && wemPaths.ContainsKey(u32Id.Value))
                    {
                        source.SourceType = "Streaming";
                        layerResults.Add($"S{idx}");
                        usedWemIds.Add(u32Id.Value);
                    }
                    else
                    {
                        source.SourceType = "Streaming_NotFound";
                        hasError = true;
                        layerResults.Add($"S{idx}(缺失)");
                        logEntries.Add($"Streaming文件缺失: {resourceName} (WemID: {u32Id})");
                    }
                }
                else if (u32Id.HasValue)
                {
                    source.SourceType = "UnknownFormat";
                    hasError = true;
                    layerResults.Add($"L{idx}(未知)");
                    logEntries.Add($"未知格式: {resourceName} (层 {idx})");
                    usedWemIds.Add(u32Id.Value);
                }
                else
                {
                    source.SourceType = "Unrecognized";
                    source.WemID = null;
                    hasError = true;
                    layerResults.Add($"L{idx}(错误)");
                    logEntries.Add($"无法识别: {resourceName} (层 {idx})");
                }

                entry.AudioSources.Add(source);
            }

            result.MappingData.Add(entry);
            fileResults.Add(hasError
                ? $"{layers.Count}层[{string.Join(" ", layerResults)}]"
                : $"{layers.Count}层");

            SafeLog(log, $"[{i}/{chainEntries.Count}] {resourceName} | {string.Join(" | ", fileResults)}");
            if ((i & 0x3F) == 0)
                Report(progress, $"[{i}/{chainEntries.Count}] {resourceName}", i, chainEntries.Count);
            foreach (var logEntry in logEntries) LogToFile(logEntry);
        }

        result.UsedWemCount = usedWemIds.Count;

        // 补集：未被使用的 WEM 写入 missing_wem_files.csv
        WriteUnusedWemCsv(paths, wemPaths, usedWemIds, log);

        SaveMappingJson(paths, result.MappingData, log);

        result.ElapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_mapping_done"));
        SafeLog(log, Locale.S("pipe_mapping_total", result.MappingData.Count));
        SafeLog(log, Locale.S("pipe_mapping_skip_err", result.Skipped, result.Errors));
        SafeLog(log, Locale.S("pipe_mapping_used", result.UsedWemCount));
        SafeLog(log, Locale.S("pipe_mapping_elapsed", result.ElapsedSeconds));
        SafeLog(log, Locale.S("pipe_mapping_saved", paths.MappingJson));
        SafeLog(log, new string('=', 50));

        return result;
    }

    /// <summary>
    /// 读 ⓪ 写下的 <c>sound_wem_mapping_export.json</c>（只含链路字段）。缺失时给出明确指引。
    /// </summary>
    private static List<MappingEntry>? LoadChainEntries(PipelinePaths paths, Action<string>? log)
    {
        if (!File.Exists(paths.MappingJson))
        {
            SafeLog(log, Locale.S("pipe_mapping_no_chain", paths.MappingJson));
            return null;
        }
        try
        {
            var text = File.ReadAllText(paths.MappingJson);
            // 旧格式（还有 GraphProgram / WwiseID_Value / WwiseID_Coord 字段）反序列化后 WwiseID 会是 0，
            // 那会让后面把每条都当成 WwiseID=0 —— 明确报错，别静默产出垃圾。
            if (text.Contains("\"WwiseID_Value\"", StringComparison.Ordinal))
            {
                SafeLog(log, Locale.S("pipe_mapping_old_format", paths.MappingJson));
                return null;
            }
            var entries = JsonSerializer.Deserialize<List<MappingEntry>>(text, PipelineJson.Options);
            if (entries == null || entries.Count == 0)
            {
                SafeLog(log, Locale.S("pipe_mapping_no_chain", paths.MappingJson));
                return null;
            }
            return entries;
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_mapping_chain_bad", ex.Message));
            return null;
        }
    }

    /// <summary>读 <c>wem_index.json</c>：WemID → WwiseWemResource 坐标 <c>组:下标</c>。</summary>
    internal static Dictionary<uint, string> LoadWemIndex(PipelinePaths paths)
    {
        var index = new Dictionary<uint, string>();
        foreach (var (id, item) in LoadWemIndexItems(paths))
            if (!string.IsNullOrEmpty(item.Coord)) index[id] = item.Coord;
        return index;
    }

    /// <summary>读 <c>wem_index.json</c> 的完整条目（坐标 + 时长）。</summary>
    internal static Dictionary<uint, WemIndexItem> LoadWemIndexItems(PipelinePaths paths)
    {
        var index = new Dictionary<uint, WemIndexItem>();
        if (!File.Exists(paths.WemIndexJson)) return index;
        try
        {
            var file = JsonSerializer.Deserialize<WemIndexFile>(File.ReadAllText(paths.WemIndexJson),
                PipelineJson.Options);
            if (file?.Wems != null)
                foreach (var (key, item) in file.Wems)
                    if (uint.TryParse(key, out var id) && item != null) index[id] = item;
            if (index.Count > 0) return index;

            // 兼容更早一版的索引（只有 WemID → 坐标，没有时长）
            using var doc = JsonDocument.Parse(File.ReadAllText(paths.WemIndexJson));
            if (doc.RootElement.TryGetProperty("WemIDToCoord", out var legacy) &&
                legacy.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in legacy.EnumerateObject())
                    if (uint.TryParse(prop.Name, out var id) && prop.Value.GetString() is { } coord)
                        index[id] = new WemIndexItem { Coord = coord };
            }
        }
        catch { }
        return index;
    }

    /// <summary>WemID → WemResWem 里实际存在的 <c>.wem</c> 路径（文件不在就不进索引）。</summary>
    internal static Dictionary<uint, string> BuildWemPaths(PipelinePaths paths, Dictionary<uint, string> wemIndex)
    {
        var result = new Dictionary<uint, string>();
        foreach (var (wemId, coord) in wemIndex)
        {
            var parts = coord.Split(':');
            if (parts.Length != 2) continue;
            var path = Path.Combine(paths.WemResWemDir, $"WwiseWemResource_{parts[0]}_{parts[1]}.wem");
            if (File.Exists(path)) result[wemId] = path;
        }
        return result;
    }

    /// <summary>
    /// 写 <c>missing_wem_files.csv</c>：所有 WEM 中未被使用的补集。
    ///
    /// 与 <c>unused_wem_with_banks.csv</c> 一样：基础列是 <c>WemID,Filename,Path</c>，
    /// 旧文件里的**其余列按 WemID 原样带过来**（人工加的 Label 等不会丢）；
    /// 唯一的例外是 <c>IsStreaming</c> —— 本作 7,838 个 WEM 全是 true，无信息量，明确不要。
    /// 这个文件会被反复重写，不保留额外列就等于每次重跑都清掉用户填过的东西。
    /// </summary>
    internal static void WriteUnusedWemCsv(PipelinePaths paths, Dictionary<uint, string> wemIndex,
        HashSet<uint> usedWemIds, Action<string>? log)
    {
        var unused = new List<KeyValuePair<uint, string>>();
        foreach (var kv in wemIndex)
            if (!usedWemIds.Contains(kv.Key)) unused.Add(kv);

        if (unused.Count == 0)
        {
            SafeLog(log, Locale.S("pipe_all_wem_used"));
            return;
        }

        string[] baseHeader = { "WemID", "Filename", "Path" };
        var baseSet = new HashSet<string>(baseHeader, StringComparer.OrdinalIgnoreCase);
        var droppedSet = new HashSet<string>(UnusedCsvDroppedHeader, StringComparer.OrdinalIgnoreCase);

        // 旧文件里的额外列（保序）+ 按 WemID 取回的值；IsStreaming 明确丢掉
        var extraColumns = new List<string>();
        var extraIndexes = new List<int>();
        var extraValues = new Dictionary<long, List<string>>();
        if (File.Exists(paths.MissingWemCsv))
        {
            try
            {
                var oldLines = File.ReadAllLines(paths.MissingWemCsv);
                if (oldLines.Length > 0)
                {
                    var oldHeader = ParseCsvLine(oldLines[0]).Select(h => h.Trim()).ToList();
                    for (int i = 0; i < oldHeader.Count; i++)
                    {
                        if (baseSet.Contains(oldHeader[i]) || droppedSet.Contains(oldHeader[i])) continue;
                        extraColumns.Add(oldHeader[i]);
                        extraIndexes.Add(i);
                    }
                    var idIdx = oldHeader.FindIndex(h => h.Equals("WemID", StringComparison.OrdinalIgnoreCase));
                    if (idIdx >= 0 && extraColumns.Count > 0)
                    {
                        for (int i = 1; i < oldLines.Length; i++)
                        {
                            var parts = ParseCsvLine(oldLines[i]);
                            if (idIdx >= parts.Count) continue;
                            if (!long.TryParse(parts[idIdx].Trim(), out var id)) continue;
                            var values = new List<string>(extraIndexes.Count);
                            foreach (var idx in extraIndexes)
                                values.Add(idx < parts.Count ? parts[idx] : "");
                            extraValues[id] = values;
                        }
                    }
                }
            }
            catch { }
        }

        try
        {
            var header = new List<string>(baseHeader);
            header.AddRange(extraColumns);
            using var writer = new StreamWriter(paths.MissingWemCsv, false, new UTF8Encoding(false));
            writer.WriteLine(string.Join(",", header));
            foreach (var kv in unused)
            {
                var cells = new List<string>
                {
                    kv.Key.ToString(),
                    EscapeCsv(Path.GetFileName(kv.Value)),
                    EscapeCsv(kv.Value)
                };
                for (int i = 0; i < extraColumns.Count; i++)
                    cells.Add(EscapeCsv(extraValues.TryGetValue(kv.Key, out var v) && i < v.Count ? v[i] : ""));
                writer.WriteLine(string.Join(",", cells));
            }

            var kept = extraValues.Count > 0 ? Locale.S("pipe_unused_kept_extra", extraColumns.Count) : "";
            SafeLog(log, Locale.S("pipe_unused_recorded", paths.MissingWemCsv, unused.Count) + kept);
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_unused_fail", ex.Message));
        }
    }

    internal static void SaveMappingJson(PipelinePaths paths, List<MappingEntry> mappingData, Action<string>? log)
    {
        try
        {
            File.WriteAllText(paths.MappingJson,
                JsonSerializer.Serialize(mappingData, PipelineJson.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_mapping_save_fail", ex.Message));
        }
    }

    /// <summary>对应 export_sounds.py 的 append_to_csv（streaming_wem_map.csv）。</summary>
    private static void AppendStreamingCsv(PipelinePaths paths, List<string> csvLines, Action<string>? log)
    {
        if (csvLines.Count == 0) return;
        try
        {
            var writeHeader = !File.Exists(paths.StreamingCsv);
            using var writer = new StreamWriter(paths.StreamingCsv, true, new UTF8Encoding(false));
            if (writeHeader) writer.WriteLine("ObjectId,Type,name");
            foreach (var line in csvLines) writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_csv_append_fail", ex.Message));
        }
    }
}
