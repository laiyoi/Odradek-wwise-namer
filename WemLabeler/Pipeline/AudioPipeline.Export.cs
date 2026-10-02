using System.IO;
using System.Text;
using System.Text.Json;

namespace WemLabeler.Pipeline;

public static partial class AudioPipeline
{
    /// <summary>
    /// 对应 export_sounds.py 的 export_from_mapping（阶段二）：
    /// 基于 sound_wem_mapping_export.json 逐个音频源导出 WAV，
    /// 支持断点续传（export_progress.json）并把缺失的 Streaming WEM
    /// 追加到 streaming_wem_map.csv。
    /// </summary>
    public static ExportReport ExportFromMapping(PipelinePaths paths, string vgmstreamPath,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var report = new ExportReport();

        if (!File.Exists(paths.MappingJson))
        {
            SafeLog(log, Locale.S("pipe_export_no_mapping"));
            return report;
        }

        List<MappingEntry>? mappingData;
        try
        {
            mappingData = JsonSerializer.Deserialize<List<MappingEntry>>(
                File.ReadAllText(paths.MappingJson), PipelineJson.Options);
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_export_mapping_broken", ex.Message));
            return report;
        }

        if (mappingData == null || mappingData.Count == 0)
        {
            SafeLog(log, Locale.S("pipe_export_mapping_broken", "empty"));
            return report;
        }

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_export_start"));
        SafeLog(log, new string('=', 50));

        var startTime = DateTime.UtcNow;
        var wemResWemIndex = BuildWemResWemIndex(paths);

        Directory.CreateDirectory(paths.OutputDir);

        var csvBuffer = new List<string>();
        const int csvBufferSize = 10;

        var txtpDirFull = Path.GetFullPath(paths.TxtpDir);
        var absBase = Path.GetFullPath(Path.Combine(txtpDirFull, ".."));

        var processed = LoadProgress(paths.ProgressFile);
        var existingFiles = BuildExistingFilesSet(paths.OutputDir);

        if (processed.Count == 0 && existingFiles.Count == 0)
        {
            try { if (File.Exists(paths.StreamingCsv)) File.Delete(paths.StreamingCsv); } catch { }
            SafeLog(log, Locale.S("pipe_export_csv_reset"));
        }

        report.Resumed = processed.Count + existingFiles.Count;

        int total = mappingData.Count;
        int processedCount = 0;

        SafeLog(log, Locale.S("pipe_export_loaded", total));
        SafeLog(log, Locale.S("pipe_export_index", wemResWemIndex.Count, existingFiles.Count, processed.Count));

        var nameSeq = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < mappingData.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = mappingData[i];
            var resourceName = item.ResourceName;
            var wwiseId = item.WwiseID_Value;
            var key = (resourceName, wwiseId);

            if (processed.Contains(key)) continue;

            var cleanName = SanitizeFileName(resourceName);

            var seq = nameSeq.TryGetValue(cleanName, out var s) ? s : 0;
            nameSeq[cleanName] = seq + 1;

            string namePrefix;
            if (seq == 0)
            {
                namePrefix = cleanName;
                if (existingFiles.Contains(cleanName))
                {
                    report.Skipped.Add($"{resourceName} (输出文件已存在)");
                    processed.Add(key);
                    processedCount++;
                    SafeLog(log, $"[{i + 1}/{total}] {resourceName} | 跳过(文件已存在)");
                    continue;
                }
            }
            else
            {
                namePrefix = $"{cleanName}_{seq}";
            }

            var audioSources = item.AudioSources;
            if (audioSources.Count == 0 ||
                audioSources[0].SourceType is "TXTP_NotFound" or "NoAudioLayers")
            {
                report.Skipped.Add($"{resourceName} (无有效音频源)");
                processed.Add(key);
                continue;
            }

            var exportResults = new List<string>();

            for (int idx = 0; idx < audioSources.Count; idx++)
            {
                var source = audioSources[idx];
                var sourceType = source.SourceType;
                var u32Id = source.WemID;
                var rawLine = source.RawLine ?? "";

                if (sourceType == "Embedded")
                {
                    var rawPath = rawLine.Split(" #")[0].Trim();
                    var parameters = rawLine.Length > rawPath.Length ? rawLine[rawPath.Length..].Trim() : "";
                    var relative = ReplaceFirst(rawPath, "../", "");
                    var finalPath = Path.GetFullPath(Path.Combine(absBase, relative));
                    var finalLine = $"{finalPath} {parameters}".Trim();

                    var outputName = audioSources.Count > 1 ? $"{namePrefix}_{idx:D2}.wav" : $"{namePrefix}.wav";
                    var outputPath = Path.Combine(paths.OutputDir, outputName);

                    if (File.Exists(outputPath))
                    {
                        report.Skipped.Add($"{outputName} (文件已存在)");
                        exportResults.Add($"E{idx}:已存在");
                        continue;
                    }

                    if (ExportAudio(vgmstreamPath, paths.OutputDir, finalLine, outputPath))
                    {
                        report.Success.Add(outputName);
                        exportResults.Add($"E{idx}:OK");
                    }
                    else
                    {
                        report.Failed.Add($"{outputName} (vgmstream失败)");
                        exportResults.Add($"E{idx}:失败");
                    }
                }
                else if (sourceType == "Streaming")
                {
                    if (u32Id.HasValue && wemResWemIndex.TryGetValue(ToU32(u32Id.Value), out var wemPath))
                    {
                        var outputName = audioSources.Count > 1 ? $"{namePrefix}_{idx:D2}.wav" : $"{namePrefix}.wav";
                        var outputPath = Path.Combine(paths.OutputDir, outputName);

                        if (File.Exists(outputPath))
                        {
                            report.Skipped.Add($"{outputName} (文件已存在)");
                            exportResults.Add($"S{idx}:已存在");
                            continue;
                        }

                        if (ExportWemFile(vgmstreamPath, wemPath, outputPath))
                        {
                            report.Success.Add(outputName);
                            exportResults.Add($"S{idx}:OK");
                        }
                        else
                        {
                            report.Failed.Add($"{outputName} (vgmstream导出失败)");
                            exportResults.Add($"S{idx}:失败");
                        }
                    }
                    else
                    {
                        var csvLine = $"{u32Id},WwiseWemResource,{resourceName}";
                        csvBuffer.Add(csvLine);
                        report.Streaming.Add(csvLine);
                        if (csvBuffer.Count >= csvBufferSize)
                        {
                            AppendStreamingCsv(paths, csvBuffer, log);
                            csvBuffer.Clear();
                        }
                        report.Failed.Add($"{resourceName} -> Wem {u32Id} (Streaming文件未找到)");
                        exportResults.Add($"S{idx}:无文件");
                    }
                }
                else if (sourceType is "Streaming_NotFound" or "UnknownFormat" or "Unrecognized")
                {
                    if (sourceType == "Streaming_NotFound" && u32Id.HasValue)
                    {
                        var csvLine = $"{u32Id},WwiseWemResource,{resourceName}";
                        csvBuffer.Add(csvLine);
                        report.Streaming.Add(csvLine);
                        if (csvBuffer.Count >= csvBufferSize)
                        {
                            AppendStreamingCsv(paths, csvBuffer, log);
                            csvBuffer.Clear();
                        }
                    }
                    exportResults.Add($"{idx}:跳过");
                }
                else
                {
                    // TXTP_NotFound_BankKnown / TXTP_NotFound_BankUnknown 等：无实际音频源
                    exportResults.Add($"{idx}:跳过");
                }
            }

            processed.Add(key);
            processedCount++;

            if (processedCount % 10 == 0) SaveProgress(paths.ProgressFile, processed);

            if (exportResults.Count > 0)
            {
                SafeLog(log, $"[{i + 1}/{total}] {resourceName} | {string.Join(", ", exportResults)}");
                Report(progress, $"[{i + 1}/{total}] {resourceName}", i + 1, total);
            }
        }

        if (csvBuffer.Count > 0) AppendStreamingCsv(paths, csvBuffer, log);

        SaveProgress(paths.ProgressFile, processed);
        // 正常跑完（未取消）时清理进度文件，与 py 的 clear_progress() 一致
        try { if (File.Exists(paths.ProgressFile)) File.Delete(paths.ProgressFile); } catch { }

        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_export_done"));
        SafeLog(log, Locale.S("pipe_export_summary",
            report.Success.Count, report.Failed.Count, report.Skipped.Count, report.Streaming.Count));
        SafeLog(log, Locale.S("pipe_mapping_elapsed", elapsed));
        SafeLog(log, new string('=', 50));

        return report;
    }

    /// <summary>对应 export_by_id.py：按 Event(WwiseID) 导出 txtp 中的每一层音频。</summary>
    public static ByIdReport ExportByIds(PipelinePaths paths, string vgmstreamPath, IReadOnlyList<long> ids,
        bool forceRefreshCache, IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var report = new ByIdReport();
        var targetIds = new HashSet<string>(ids.Select(x => x.ToString()));
        foreach (var id in targetIds) report.NotFoundTxtp.Add(id);

        var outputDir = paths.ByIdOutputDir;
        Directory.CreateDirectory(outputDir);

        SafeLog(log, Locale.S("pipe_byid_indexing"));
        var wemMap = BuildWemMapWithCache(paths, forceRefreshCache, log, ct);
        SafeLog(log, Locale.S("pipe_byid_index_done", wemMap.Count));

        if (!Directory.Exists(paths.TxtpDir))
        {
            SafeLog(log, $"[!] 找不到 txtp 目录: {paths.TxtpDir}");
            return report;
        }

        var txtpDirFull = Path.GetFullPath(paths.TxtpDir);
        var absBase = Path.GetFullPath(Path.Combine(txtpDirFull, ".."));

        foreach (var txtpFile in Directory.EnumerateFiles(paths.TxtpDir, "*.txtp"))
        {
            ct.ThrowIfCancellationRequested();

            string content;
            string[] lines;
            try
            {
                content = File.ReadAllText(txtpFile);
                lines = File.ReadAllLines(txtpFile);
            }
            catch (Exception ex)
            {
                SafeLog(log, Locale.S("pipe_byid_parse_fail", Path.GetFileName(txtpFile), ex.Message));
                continue;
            }

            var eventMatch = TxtpEventPattern.Match(content);
            if (!eventMatch.Success) continue;
            var currentId = eventMatch.Groups[2].Value;
            if (!targetIds.Contains(currentId)) continue;

            report.NotFoundTxtp.Remove(currentId);
            Report(progress, Locale.S("pipe_byid_event", currentId, Path.GetFileName(txtpFile)));

            var layers = new List<string>();
            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (trimmed.StartsWith("../") || trimmed.Contains("##") || trimmed.StartsWith("wem/"))
                    layers.Add(trimmed);
            }

            for (int idx = 0; idx < layers.Count; idx++)
            {
                ct.ThrowIfCancellationRequested();
                var layerLine = layers[idx];

                var idMatch = TxtpWemIdPattern.Match(layerLine);
                if (!idMatch.Success) idMatch = TxtpWemIdAltPattern.Match(layerLine);
                var wemName = idMatch.Success ? idMatch.Groups[1].Value : $"L{idx}";
                long? u32Id = long.TryParse(wemName, out var parsed) ? ToU32(parsed) : null;

                var strippedLine = layerLine.TrimStart('?', ' ').Trim();
                var cleanLine = strippedLine.Split("##fade")[0].Trim();

                string finalLine;

                if (cleanLine.StartsWith("../"))
                {
                    var rawPath = cleanLine.Split(" #")[0].Trim();
                    var parameters = cleanLine.Length > rawPath.Length ? cleanLine[rawPath.Length..].Trim() : "";
                    var finalPath = Path.GetFullPath(Path.Combine(absBase, ReplaceFirst(rawPath, "../", "")));
                    finalLine = $"{finalPath} {parameters}".Trim();
                }
                else if (u32Id.HasValue && wemMap.TryGetValue(ToU32(u32Id.Value), out var targetPath))
                {
                    if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                    {
                        var lastDot = cleanLine.LastIndexOf(".wem", StringComparison.OrdinalIgnoreCase);
                        var parameters = lastDot >= 0 ? cleanLine[(lastDot + 4)..].Trim() : "";
                        finalLine = $"{targetPath} {parameters}".Trim();
                    }
                    else
                    {
                        report.Failed.Add($"Event {currentId} -> Wem {u32Id} (文件在 WemResWem 中缺失)");
                        continue;
                    }
                }
                else
                {
                    var preview = cleanLine.Length > 30 ? cleanLine[..30] : cleanLine;
                    report.Failed.Add($"Event {currentId} -> 层 {idx} (无法识别: {preview}...)");
                    continue;
                }

                var tempTxtp = Path.Combine(outputDir, $"temp_{currentId}_{idx}.txtp");
                var outputWav = Path.Combine(outputDir, $"{currentId}_{wemName}.wav");
                try
                {
                    File.WriteAllText(tempTxtp, finalLine, new UTF8Encoding(false));
                    SafeLog(log, Locale.S("pipe_byid_exporting", Path.GetFileName(outputWav)));
                    Report(progress, Path.GetFileName(outputWav));
                    if (RunVgmstream(vgmstreamPath, outputWav, tempTxtp))
                        report.Success.Add($"{currentId}_{wemName}");
                    else
                        report.Failed.Add($"{currentId}_{wemName} (vgmstream 报错)");
                }
                catch (Exception ex)
                {
                    report.Failed.Add($"{currentId}_{wemName} ({ex.Message})");
                }
                finally
                {
                    try { if (File.Exists(tempTxtp)) File.Delete(tempTxtp); } catch { }
                }
            }
        }

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_byid_stats", string.Join(", ", targetIds)));
        SafeLog(log, Locale.S("pipe_byid_success", report.Success.Count));
        SafeLog(log, Locale.S("pipe_byid_failed", report.Failed.Count));
        foreach (var f in report.Failed.Take(50)) SafeLog(log, $"   [!] {f}");
        if (report.NotFoundTxtp.Count > 0)
            SafeLog(log, Locale.S("pipe_byid_not_found", string.Join(", ", report.NotFoundTxtp)));
        SafeLog(log, new string('=', 50));

        return report;
    }

    /// <summary>
    /// 对应 link_unused_wem.py：从 WemResJson 构建索引，交叉引用
    /// BankRes(WemIDs)、txtp 和 WemResWem，输出 unused_wem_with_banks.csv。
    /// </summary>
    public static int BuildUnusedWemCsv(PipelinePaths paths, IProgress<PipelineProgress>? progress,
        Action<string>? log, CancellationToken ct)
    {
        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_link_start"));
        SafeLog(log, new string('=', 50));

        Report(progress, Locale.S("pipe_link_indexing"));

        var wemJsonIndex = BuildWemResJsonIndex(paths, log);
        SafeLog(log, Locale.S("pipe_link_wemresjson", wemJsonIndex.Count));

        // WemPath 这一列全靠这个目录：不存在就别默默写一堆空路径出来
        if (!Directory.Exists(paths.WemResWemDir))
            throw new DirectoryNotFoundException(Locale.S("pipe_link_wemdir_missing", paths.WemResWemDir));

        var wemFileIndex = BuildWemFileByCoordIndex(paths);
        SafeLog(log, Locale.S("pipe_link_wemreswem", wemFileIndex.Count));

        ct.ThrowIfCancellationRequested();
        Report(progress, Locale.S("pipe_link_bankres"));
        var bankWemIds = BuildBankResWemIds(paths, log);
        SafeLog(log, Locale.S("pipe_link_bankres_done", bankWemIds.Count));

        ct.ThrowIfCancellationRequested();
        Report(progress, Locale.S("pipe_link_txtp"));
        var txtpWemIndex = BuildTxtpWemIndex(paths, log);
        SafeLog(log, Locale.S("pipe_link_txtp_done", txtpWemIndex.Count));

        ct.ThrowIfCancellationRequested();
        var usedWemIds = BuildUsedWemIds(paths, log);
        SafeLog(log, Locale.S("pipe_link_used", usedWemIds.Count));

        var results = new List<UnusedWemRow>();
        int inWemDir = 0, inBankRes = 0, inTxtp = 0;

        var orderedIds = wemJsonIndex.Keys.ToList();
        orderedIds.Sort();

        for (int i = 0; i < orderedIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var wemId = orderedIds[i];
            if (usedWemIds.Contains(wemId)) continue;

            var info = wemJsonIndex[wemId];
            var wemFilename = wemFileIndex.TryGetValue(info.Coord, out var f) ? f : "";
            var wemPath = string.IsNullOrEmpty(wemFilename)
                ? ""
                : Path.Combine(paths.WemResWemDir, wemFilename);

            var row = new UnusedWemRow
            {
                WemID = wemId,
                Coord = info.Coord,
                JsonFile = info.JsonFile,

                WemFile = wemFilename,
                WemPath = wemPath
            };

            if (!string.IsNullOrEmpty(wemFilename)) inWemDir++;

            if (bankWemIds.Contains(wemId))
            {
                row.FoundInBankRes = "是";
                inBankRes++;
            }

            if (txtpWemIndex.TryGetValue(wemId, out var txtpFiles))
            {
                row.TxtpFiles = string.Join(";", txtpFiles);
                inTxtp++;
            }

            results.Add(row);

            if (i % 2000 == 0) Report(progress, Locale.S("pipe_link_progress", i, orderedIds.Count), i, orderedIds.Count);
        }

        SafeLog(log, new string('=', 50));
        SafeLog(log, Locale.S("pipe_link_header_stats"));
        SafeLog(log, Locale.S("pipe_link_stat_total", wemJsonIndex.Count));
        SafeLog(log, Locale.S("pipe_link_stat_filtered", usedWemIds.Count));
        SafeLog(log, Locale.S("pipe_link_stat_remain", results.Count));
        SafeLog(log, Locale.S("pipe_link_stat_files", inWemDir));
        SafeLog(log, Locale.S("pipe_link_stat_bankres", inBankRes));
        SafeLog(log, Locale.S("pipe_link_stat_txtp", inTxtp));
        SafeLog(log, new string('=', 50));

        WriteUnusedWemWithBanksCsv(paths.UnusedWemCsv, results, log);
        SafeLog(log, Locale.S("pipe_link_saved", paths.UnusedWemCsv));
        return results.Count;
    }

    /// <summary>新格式的基础列（去掉了无信息量的 IsStreaming）。</summary>
    private static readonly string[] UnusedCsvBaseHeader =
    {
        "WemID", "Coord", "JsonFile", "WemFile", "WemPath", "FoundInBankRes", "TxtpFiles"
    };

    /// <summary>
    /// 旧格式里有、但新格式**明确不再产出**的列：这些列属于「本工具负责的字段」，
    /// 不要当成额外列保留回来（其余非基础列一律原样保留）。
    /// </summary>
    private static readonly string[] UnusedCsvDroppedHeader = { "IsStreaming" };

    /// <summary>
    /// 写 unused_wem_with_banks.csv：
    ///   基础列 = WemID,Coord,JsonFile,WemFile,WemPath,FoundInBankRes,TxtpFiles
    ///   + 旧文件里**除基础列与被删列以外的所有列**（原顺序），值按 WemID 原样带过来
    ///
    /// 另外：旧文件里存在、但这次不再是「未使用」的 WEM，**整行原样追加在末尾**
    /// （连同它们的标注等额外列），这样重新生成不会丢掉任何人工填过的信息。
    ///
    /// 被明确删掉的列（IsStreaming）不写、也不带回。
    /// </summary>
    private static void WriteUnusedWemWithBanksCsv(string path, List<UnusedWemRow> rows, Action<string>? log)
    {
        try
        {
            var baseSet = new HashSet<string>(UnusedCsvBaseHeader, StringComparer.OrdinalIgnoreCase);
            var droppedSet = new HashSet<string>(UnusedCsvDroppedHeader, StringComparer.OrdinalIgnoreCase);
            var extraColumns = new List<string>();                  // 旧文件里的额外列（保序）
            var extraIndexes = new List<int>();                     // 它们在旧行里的下标
            var baseIndexes = new List<int>();                      // 新基础列各自在旧行里的下标（缺则 -1）
            var extraValues = new Dictionary<long, List<string>>();  // WemID -> 额外列的值
            var oldCells = new Dictionary<long, List<string>>();     // WemID -> 旧行的全部单元格
            var oldOrder = new List<long>();                        // 旧文件里的行顺序

            if (File.Exists(path))
            {
                try
                {
                    var oldLines = File.ReadAllLines(path);
                    if (oldLines.Length > 0)
                    {
                        var oldHeader = ParseCsvLine(oldLines[0]).Select(h => h.Trim()).ToList();
                        foreach (var name in UnusedCsvBaseHeader)
                            baseIndexes.Add(oldHeader.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase)));

                        for (int i = 0; i < oldHeader.Count; i++)
                        {
                            if (baseSet.Contains(oldHeader[i]) || droppedSet.Contains(oldHeader[i])) continue;
                            extraColumns.Add(oldHeader[i]);
                            extraIndexes.Add(i);
                        }

                        var idIdx = oldHeader.FindIndex(h => h.Equals("WemID", StringComparison.OrdinalIgnoreCase));
                        if (idIdx >= 0)
                        {
                            for (int i = 1; i < oldLines.Length; i++)
                            {
                                var parts = ParseCsvLine(oldLines[i]);
                                if (idIdx >= parts.Count) continue;
                                if (!long.TryParse(parts[idIdx].Trim(), out var id)) continue;

                                oldOrder.Add(id);
                                oldCells[id] = parts;

                                var values = new List<string>(extraIndexes.Count);
                                foreach (var idx in extraIndexes)
                                    values.Add(idx < parts.Count ? parts[idx] : "");
                                extraValues[id] = values;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SafeLog(log, Locale.S("pipe_link_preserve_fail", ex.Message));
                    extraColumns.Clear();
                    extraIndexes.Clear();
                    baseIndexes.Clear();
                    extraValues.Clear();
                    oldCells.Clear();
                    oldOrder.Clear();
                }
            }

            var header = new List<string>(UnusedCsvBaseHeader);
            header.AddRange(extraColumns);

            var written = new HashSet<long>();
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.WriteLine(string.Join(",", header));

                int keptValues = 0, keptRows = 0;
                foreach (var r in rows)
                {
                    written.Add(r.WemID);

                    var cells = new List<string>
                    {
                        EscapeCsv(r.WemID.ToString()),
                        EscapeCsv(r.Coord),
                        EscapeCsv(r.JsonFile),
                        EscapeCsv(r.WemFile),
                        EscapeCsv(r.WemPath),
                        EscapeCsv(r.FoundInBankRes),
                        EscapeCsv(r.TxtpFiles)
                    };

                    if (extraColumns.Count > 0)
                    {
                        var values = extraValues.TryGetValue(r.WemID, out var v) ? v : null;
                        bool rowKept = false;
                        for (int i = 0; i < extraColumns.Count; i++)
                        {
                            var value = values != null && i < values.Count ? values[i] : "";
                            if (!string.IsNullOrWhiteSpace(value)) { keptValues++; rowKept = true; }
                            cells.Add(EscapeCsv(value));
                        }
                        if (rowKept) keptRows++;
                    }

                    writer.WriteLine(string.Join(",", cells));
                }

                if (extraColumns.Count > 0)
                    SafeLog(log, Locale.S("pipe_link_preserved", extraColumns.Count, keptRows, keptValues));

                // 旧文件里还有、这次不再是「未使用」的行：整行追加到末尾，信息一点不丢。
                int carried = 0;
                var seen = new HashSet<long>();
                foreach (var id in oldOrder)
                {
                    if (written.Contains(id) || !seen.Add(id)) continue;
                    if (!oldCells.TryGetValue(id, out var cells)) continue;

                    var rebuilt = new List<string>(baseIndexes.Count + extraIndexes.Count);
                    foreach (var bi in baseIndexes)
                        rebuilt.Add(EscapeCsv(bi >= 0 && bi < cells.Count ? cells[bi] : ""));
                    foreach (var ei in extraIndexes)
                        rebuilt.Add(EscapeCsv(ei < cells.Count ? cells[ei] : ""));

                    writer.WriteLine(string.Join(",", rebuilt));
                    carried++;
                }

                if (carried > 0)
                    SafeLog(log, Locale.S("pipe_link_carried_rows", carried));
            }
        }
        catch (Exception ex)
        {
            SafeLog(log, Locale.S("pipe_link_write_fail", ex.Message));
        }
    }

    /// <summary>最简 CSV 行解析（支持双引号包裹与 "" 转义）。</summary>
    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        result.Add(current.ToString());
        return result;
    }

    internal static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    #region 断点续传（export_progress.json）

    internal static HashSet<(string Name, long WwiseId)> LoadProgress(string progressFile)
    {
        var set = new HashSet<(string, long)>();
        if (!File.Exists(progressFile)) return set;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(progressFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return set;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Array) continue;
                var arr = el.EnumerateArray().ToList();
                if (arr.Count < 2) continue;
                var name = arr[0].ValueKind == JsonValueKind.String ? arr[0].GetString() ?? "" : arr[0].ToString();
                long id = 0;
                if (arr[1].ValueKind == JsonValueKind.Number) arr[1].TryGetInt64(out id);
                else if (arr[1].ValueKind == JsonValueKind.String) long.TryParse(arr[1].GetString(), out id);
                set.Add((name, id));
            }
        }
        catch (Exception ex)
        {
            SafeLog(null, $"加载进度失败: {ex.Message}");
        }
        return set;
    }

    internal static void SaveProgress(string progressFile, HashSet<(string Name, long WwiseId)> processed)
    {
        try
        {
            var payload = processed.Select(k => new object[] { k.Name, k.WwiseId }).ToList();
            File.WriteAllText(progressFile, JsonSerializer.Serialize(payload, PipelineJson.Options), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            SafeLog(null, $"保存进度失败: {ex.Message}");
        }
    }

    /// <summary>对应 py 的 build_existing_files_set：从输出目录反推已导出的资源名。</summary>
    internal static HashSet<string> BuildExistingFilesSet(string outputDir)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(outputDir)) return existing;
        foreach (var file in Directory.EnumerateFiles(outputDir, "*.wav"))
        {
            var baseName = Path.GetFileNameWithoutExtension(file);
            var underscore = baseName.LastIndexOf('_');
            if (underscore >= 0)
            {
                var suffix = baseName[(underscore + 1)..];
                if (suffix.Length > 0 && suffix.All(char.IsAsciiDigit)) baseName = baseName[..underscore];
            }
            existing.Add(baseName);
        }
        return existing;
    }

    #endregion

    /// <summary>Python 的 str.replace(old, new, 1)。</summary>
    internal static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var idx = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (idx < 0) return text;
        return text[..idx] + newValue + text[(idx + oldValue.Length)..];
    }
}
