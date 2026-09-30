using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// 对应 export_sounds.py 的 build_mapping：
    /// 解析 GraphSoundRes → GraphPgmRes → NodeConstRes → WwiseID 链路，
    /// 结合 txtp 索引生成 sound_wem_mapping_export.json，
    /// 并把未被使用的 WEM 写入 missing_wem_files.csv。
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

        Report(progress, Locale.S("pipe_mapping_indexing"));

        var txtpIndex = BuildTxtpEventIndex(paths);
        var wemResWemIndex = BuildWemResWemIndex(paths);
        var bankMediaIndex = BuildBankMediaIndex(paths, log);

        var usedWemIds = new HashSet<uint>();

        SafeLog(log, Locale.S("pipe_mapping_index_summary",
            txtpIndex.Count, wemResWemIndex.Count, bankMediaIndex.Count));

        var allFiles = Directory.Exists(paths.GraphSoundResDir)
            ? Directory.EnumerateFiles(paths.GraphSoundResDir, "*.json")
                .Where(f => GraphSoundPattern.IsMatch(Path.GetFileName(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
                .ToList()
            : new List<string>();

        result.TotalFiles = allFiles.Count;
        SafeLog(log, Locale.S("pipe_mapping_scanned", allFiles.Count));

        int i = 0;
        foreach (var filepath in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            i++;

            using var soundDoc = LoadJsonDocument(filepath);
            if (soundDoc == null) { result.Errors++; continue; }
            var soundData = soundDoc.RootElement;

            var resourceName = JsonString(soundData, "ResourceName") ?? "Unknown";
            var pgmRef = ParseRef(JsonString(soundData, "GraphProgram"));
            if (pgmRef == null) { result.Errors++; continue; }

            using var pgmDoc = LoadJsonDocument(Path.Combine(paths.GraphPgmResDir,
                $"GraphProgramResource_{pgmRef.Value.Group}_{pgmRef.Value.Index}.json"));
            if (pgmDoc == null) { result.Errors++; continue; }

            var exposedRef = ParseRef(JsonString(pgmDoc.RootElement, "ExposedDataResource"));
            if (exposedRef == null) { result.Errors++; continue; }

            using var ncDoc = LoadJsonDocument(Path.Combine(paths.NodeConstResDir,
                $"NodeConstantsResource_{exposedRef.Value.Group}_{exposedRef.Value.Index}.json"));
            if (ncDoc == null || ncDoc.RootElement.ValueKind != JsonValueKind.Object) { result.Errors++; continue; }

            var softLinked = ReadSoftLinkedObjects(ncDoc.RootElement);

            var fileResults = new List<string>();
            var logEntries = new List<string>();

            foreach (var softRefStr in softLinked)
            {
                ct.ThrowIfCancellationRequested();
                var softRef = ParseRef(softRefStr);
                if (softRef == null) continue;

                using var wwiseDoc = LoadJsonDocument(Path.Combine(paths.WwiseIdDir,
                    $"WwiseID_{softRef.Value.Group}_{softRef.Value.Index}.json"));
                if (wwiseDoc == null) { result.Errors++; continue; }
                var wwiseIdRaw = JsonLong(wwiseDoc.RootElement, "Id");
                if (!wwiseIdRaw.HasValue) { result.Errors++; continue; }
                var wwiseId = ToU32(wwiseIdRaw.Value);

                var entry = new MappingEntry
                {
                    ResourceName = resourceName,
                    GraphProgram = $"{pgmRef.Value.Group}:{pgmRef.Value.Index}",
                    ExposedDataResource = $"{exposedRef.Value.Group}:{exposedRef.Value.Index}",
                    WwiseID_Value = wwiseId,
                    WwiseID_Coord = $"{softRef.Value.Group}:{softRef.Value.Index}"
                };

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
                                BankFile = bankFilename
                            });
                        }
                        fileResults.Add($"无TXTP[{string.Join(", ", banks.Select(b => Path.GetFileName(b)))}]");
                    }
                    else
                    {
                        entry.AudioSources.Add(new AudioSourceInfo
                        {
                            WemID = wwiseId,
                            SourceType = "TXTP_NotFound_BankUnknown"
                        });
                        fileResults.Add("无TXTP");
                    }

                    result.MappingData.Add(entry);
                    logEntries.Add($"TXTP未找到: {resourceName} (WwiseID: {wwiseId}){bankInfo}");
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
                        RawLine = cleanLine
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
                        if (u32Id.HasValue && wemResWemIndex.ContainsKey(u32Id.Value))
                        {
                            source.SourceType = "Streaming";
                            source.WemRes_Coord = $"WemID:{u32Id}";
                            layerResults.Add($"S{idx}");
                            usedWemIds.Add(u32Id.Value);
                        }
                        else
                        {
                            source.SourceType = "Streaming_NotFound";
                            source.WemRes_Coord = $"WemID:{u32Id}";
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
            }

            if (fileResults.Count > 0)
            {
                SafeLog(log, $"[{i}/{allFiles.Count}] {resourceName} | {string.Join(" | ", fileResults)}");
                Report(progress, $"[{i}/{allFiles.Count}] {resourceName}", i, allFiles.Count);
            }

            foreach (var logEntry in logEntries) LogToFile(logEntry);
        }

        result.UsedWemCount = usedWemIds.Count;

        // 补集：未被使用的 WEM 写入 missing_wem_files.csv
        WriteUnusedWemCsv(paths, wemResWemIndex, usedWemIds, log);

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

    private static List<string> ReadSoftLinkedObjects(JsonElement nodeConstantsRoot)
    {
        var result = new List<string>();
        if (!nodeConstantsRoot.TryGetProperty("Parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object) return result;
        if (!parameters.TryGetProperty("DefaultSoftLinkedObjects", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s);
            }
        }
        return result;
    }

    /// <summary>对应 py 的 write_unused_wem_csv：所有 WEM 中未被使用的补集。</summary>
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

        try
        {
            using var writer = new StreamWriter(paths.MissingWemCsv, false, new UTF8Encoding(false));
            writer.WriteLine("WemID,Filename,Path");
            foreach (var kv in unused)
                writer.WriteLine($"{kv.Key},{Path.GetFileName(kv.Value)},{kv.Value}");
            SafeLog(log, Locale.S("pipe_unused_recorded", paths.MissingWemCsv, unused.Count));
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
