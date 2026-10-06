using System.IO;
using System.Text;
using System.Text.Json;
using OdradekSharp.Ds2;
using OdradekSharp.Rtti;

namespace WemLabeler.Pipeline;

/// <summary>
/// 直接从游戏文件导出资源：读取 <c>streaming_graph.core</c>，按类型搜索对象，
/// 再逐个反序列化并写出 odradek 兼容的 JSON。
///
/// 与早期方案的区别：**不再需要 odradek.exe，也不需要 links-*.db**。
/// 原来靠「链接库 + 已有导出文件名」反推「哪些对象属于哪个类型」，
/// 而链接库里根本没有类型名；现在 <c>group.types()</c> 直接给出每个对象的类型，
/// 按类型搜索是纯元数据操作，零反序列化。
///
/// 底层由 OdradekSharp 提供（另一个 agent 对 odradek 源码的逐行移植，
/// 已在真实 DS2 上实测：按类型搜索的数量与 odradek 实际导出的一一相等，
/// 导出的 JSON 与 odradek 逐字节相同）。
/// </summary>
public static class OdradekExporter
{
    /// <summary>WEM 原始音频对应的类型。</summary>
    public const string WemType = "WwiseWemResource";

    /// <summary>bank 二进制对应的类型。</summary>
    public const string BankType = "WwiseBankResource";

    /// <summary>
    /// 是否连**派生类型**一起算。默认 false，与原 odradek 的导出结果保持一致：
    /// 例如 <c>WwiseWemLocalizedResource : WwiseWemResource</c> 有 268 个对象，
    /// 它们只分布在少数几个超大的本地化组里 —— 读那些组（还要读子组）会瞬间吃掉几个 GB。
    /// </summary>
    public const bool IncludeDerivedTypes = false;

    private static string TypesJsonPath => Path.Combine(AppContext.BaseDirectory, "Data", "types.json");

    // ---------------------------------------------------------------- 定位游戏

    /// <summary>DS2 的判据与 odradek 一致：根目录下有 DS2.exe。</summary>
    public static bool IsGameRoot(string? dir) =>
        !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "DS2.exe"));

    /// <summary>
    /// 自动找 DS2 安装目录：先看配置，再扫各盘 Steam 库里的 <c>steamapps\common\*</c>，
    /// 最后兜底浅扫盘根。
    /// </summary>
    public static string? AutoDetectGameRoot(string? configured, Action<string>? log, CancellationToken ct)
    {
        if (IsGameRoot(configured)) return configured;

        var candidates = new List<string>();

        try
        {
            var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                        ?? Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (!string.IsNullOrEmpty(steam))
            {
                var libs = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (File.Exists(libs))
                {
                    foreach (var line in File.ReadAllLines(libs))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, "\"path\"\\s+\"([^\"]+)\"");
                        if (m.Success)
                            candidates.Add(Path.Combine(m.Groups[1].Value.Replace("\\\\", "\\"), "steamapps", "common"));
                    }
                }
                candidates.Add(Path.Combine(steam, "steamapps", "common"));
            }
        }
        catch { }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
            var root = drive.RootDirectory.FullName;
            candidates.Add(Path.Combine(root, "SteamLibrary", "steamapps", "common"));
            candidates.Add(Path.Combine(root, "Steam", "steamapps", "common"));
            candidates.Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common"));
            candidates.Add(Path.Combine(root, "Games"));
        }

        foreach (var dir in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!IsGameRoot(sub)) continue;
                    AudioPipeline.SafeLog(log, $"找到游戏: {sub}");
                    return sub;
                }
            }
            catch { }
        }

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed) continue;
            var found = ScanForDs2(drive.RootDirectory.FullName, 0, 3, log, ct);
            if (found != null) return found;
        }

        AudioPipeline.SafeLog(log, "[!] 没找到 DS2 安装目录（需要包含 DS2.exe 的那一层）");
        return null;
    }

    private static string? ScanForDs2(string dir, int depth, int maxDepth, Action<string>? log, CancellationToken ct)
    {
        if (depth > maxDepth) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (IsGameRoot(dir)) return dir;
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('$') || name is "Windows" or "Program Files" or "Program Files (x86)"
                    or "ProgramData" or "System Volume Information" or "$Recycle.Bin" or "AppData") continue;
                var hit = ScanForDs2(sub, depth + 1, maxDepth, log, ct);
                if (hit == null) continue;
                AudioPipeline.SafeLog(log, $"找到游戏: {hit}");
                return hit;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    /// <summary>「WwiseWemResource_1604_1266」→「1604:1266」。</summary>
    public static string? ParseIdFromFileName(string nameWithoutExt)
    {
        var parts = nameWithoutExt.Split('_');
        if (parts.Length < 3) return null;
        if (!int.TryParse(parts[^2], out var g)) return null;
        if (!int.TryParse(parts[^1], out var i)) return null;
        return $"{g}:{i}";
    }

    // ---------------------------------------------------------------- ⓪ 读游戏资源（一步）

    public sealed record ExportSummary(int Sounds, int Entries, int Banks, int Wems, int Groups)
    {
        public override string ToString() =>
            $"GraphSound {Sounds:N0} → {Entries:N0} 条目 / bank {Banks:N0} 个 / wem 索引 {Wems:N0} 个 / 读组 {Groups:N0}";
    }

    /// <summary>
    /// ⓪ 读一次游戏，把后面几步需要的东西一次写完 —— **不再落任何按对象的资源 JSON**：
    /// <list type="bullet">
    /// <item><c>sound_wem_mapping_export.json</c>：只写「链路部分」
    /// （ResourceName / GraphSound 坐标 / WwiseID），txtp 相关的字段留给 ③ 富化；</item>
    /// <item><c>wem_index.json</c>：<c>WemID → WwiseWemResource 坐标</c>，顶替原来 7,838 个 WemResJson；</item>
    /// <item><c>Extracted_Banks\*.bnk</c>：直接把 <c>WwiseBankResource.BankData</c> 落盘，
    /// 于是「从 BankRes 提取 BNK」那一步不需要了；</item>
    /// <item><c>soundmap_report.txt</c>：链路报告（总数 / 产出 / 缺失明细）。</item>
    /// </list>
    /// </summary>
    public static ExportSummary ExportResources(string gameRoot, PipelinePaths paths,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ExtractedBanksDir);
        AudioPipeline.SafeLog(log, $"游戏根目录: {gameRoot}");
        AudioPipeline.SafeLog(log, $"项目根    : {paths.BaseDir}");
        AudioPipeline.SafeLog(log, $"RTTI 定义 : {TypesJsonPath}");

        AudioPipeline.Report(progress, Locale.S("pipe_res_loading_graph"));
        using var game = DecimaGame.Open(gameRoot, TypesJsonPath);
        AudioPipeline.SafeLog(log,
            $"图: {game.Graph.Groups.Count:N0} 个组, {game.Graph.TypeTable.Count:N0} 个类型表项, {game.Graph.Files.Count:N0} 个文件");
        AudioPipeline.SafeLog(log, "");

        // ---- 1) 链路 → mapping 的链部分 ----
        AudioPipeline.Report(progress, Locale.S("pipe_mapping_chain"));
        var chain = SoundChainResolver.Resolve(game, new SoundChainResolver.Options
        {
            Threads = Math.Min(4, Environment.ProcessorCount),
            IncludeDerived = IncludeDerivedTypes,
        }, log, ct, out var chainReport);

        var entries = new List<MappingEntry>(chainReport.Entries);
        foreach (var sound in chain)
            foreach (var hit in sound.Hits)
                entries.Add(new MappingEntry
                {
                    ResourceName = hit.ResourceName,
                    GraphSound = hit.GraphSound,
                    WwiseID = hit.WwiseID,
                });

        AudioPipeline.SaveMappingJson(paths, entries, log);
        AudioPipeline.SafeLog(log, "");
        AudioPipeline.SafeLog(log, chainReport.Render());
        try
        {
            File.WriteAllText(paths.ChainReport, chainReport.Render(), new UTF8Encoding(false));
            AudioPipeline.SafeLog(log, Locale.S("pipe_mapping_chain_report", paths.ChainReport));
        }
        catch (Exception ex)
        {
            AudioPipeline.SafeLog(log, Locale.S("pipe_mapping_save_fail", ex.Message));
        }

        // ---- 2) bank 二进制（顺便收集 WemIDs，见下）----
        AudioPipeline.Report(progress, Locale.S("pipe_res_banks"));
        var (banks, bankWemIds) = ExportBanks(game, paths, progress, log, ct);

        // ---- 3) WemID → 坐标 / 时长 索引（含 bank 引用的 WemID）----
        AudioPipeline.Report(progress, Locale.S("pipe_res_wem_index"));
        var wems = WriteWemIndex(game, paths, log, ct, bankWemIds);

        var summary = new ExportSummary(chainReport.GraphSoundTotal, entries.Count, banks, wems,
            chainReport.GroupsRead);
        AudioPipeline.SafeLog(log, "");
        AudioPipeline.SafeLog(log, Locale.S("pipe_res_done", summary.ToString()));
        return summary;
    }

    /// <summary>
    /// 把每个 <c>WwiseWemResource.WemID</c> 连同它的对象坐标/时长写成 wem_index.json。
    /// 只读 WemID / IsStreaming 之类的小字段，所以走 <c>CollectPayload=false</c>
    /// （不物化内嵌的 WemData 大数组）。
    /// 顺带记下 <paramref name="bankWemIds"/>（bank 引用到的 WemID 并集），
    /// 顶替原来读 BankRes JSON 的 <c>WemIDs</c> 字段。
    /// </summary>
    private static int WriteWemIndex(DecimaGame game, PipelinePaths paths, Action<string>? log,
        CancellationToken ct, IReadOnlyCollection<uint> bankWemIds)
    {
        var byGroup = new Dictionary<int, List<int>>();
        foreach (var (id, _) in game.FindObjects(WemType, IncludeDerivedTypes))
        {
            ct.ThrowIfCancellationRequested();
            if (!byGroup.TryGetValue(id.GroupId, out var list)) byGroup[id.GroupId] = list = new List<int>();
            list.Add(id.ObjectIndex);
        }

        var index = new Dictionary<string, WemIndexItem>();
        foreach (var (groupId, indices) in byGroup)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<TypedObject> objects;
            try { objects = game.ReadGroupFiltered(groupId, indices.ToHashSet(), false, collectPayload: false); }
            catch (Exception ex)
            {
                AudioPipeline.SafeLog(log, $"[!] 组 {groupId} 读取失败，{indices.Count} 个 WEM 未进索引: {ex.Message}");
                continue;
            }
            var valid = game.ValidObjectCount(groupId);
            foreach (var i in indices)
            {
                if (i >= valid || i >= objects.Count || objects[i] is null) continue;
                var obj = objects[i];
                if (!obj.Fields.TryGetValue("WemID", out var v) || v is null) continue;
                index[AudioPipeline.ToU32(Convert.ToInt32(v)).ToString()] = new WemIndexItem
                {
                    Coord = $"{groupId}:{i}",
                    LengthSeconds = obj.Fields.TryGetValue("mLengthInSeconds", out var len) && len is not null
                        ? Convert.ToDouble(len, System.Globalization.CultureInfo.InvariantCulture)
                        : 0,
                };
            }
        }

        try
        {
            File.WriteAllText(paths.WemIndexJson,
                JsonSerializer.Serialize(new WemIndexFile
                {
                    WemDir = paths.WemResWemDir,
                    Wems = index,
                    BankWemIDs = bankWemIds.Select(id => id.ToString()).ToList(),
                }, PipelineJson.Options), new UTF8Encoding(false));
            AudioPipeline.SafeLog(log, Locale.S("pipe_res_wem_index_done", index.Count, paths.WemIndexJson));
        }
        catch (Exception ex)
        {
            AudioPipeline.SafeLog(log, $"[!] 写 wem_index.json 失败: {ex.Message}");
        }
        return index.Count;
    }

    /// <summary>
    /// 把 <c>WwiseBankResource.BankData</c> 直接落成 <c>Extracted_Banks\WwiseBankResource_&lt;组&gt;_&lt;下标&gt;.bnk</c>。
    /// 仍然过一遍 <see cref="AudioPipeline.FixWwiseBankData"/>（与旧的「从 Base64 JSON 提取」完全同一段修复），
    /// 所以产物和以前逐字节一致。
    ///
    /// 同时收集所有 bank 的 <c>WemIDs</c> 并集 —— 这就是旧版「读 BankRes JSON 的 WemIDs」那个字段，
    /// 「分析未使用的 WEM」的 <c>FoundInBankRes</c> 列靠它（**不是** banks.xml 的媒体表，两者语义不同）。
    /// </summary>
    private static (int Written, List<uint> BankWemIds) ExportBanks(DecimaGame game, PipelinePaths paths,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        var byGroup = new Dictionary<int, List<int>>();
        var total = 0;
        foreach (var (id, _) in game.FindObjects(BankType, IncludeDerivedTypes))
        {
            total++;
            if (!byGroup.TryGetValue(id.GroupId, out var list)) byGroup[id.GroupId] = list = new List<int>();
            list.Add(id.ObjectIndex);
        }

        var bankWemIds = new HashSet<uint>();
        var written = 0;
        var done = 0;
        foreach (var (groupId, indices) in byGroup)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<TypedObject> objects;
            try { objects = game.ReadGroupFiltered(groupId, indices.ToHashSet(), false); }
            catch (Exception ex)
            {
                AudioPipeline.SafeLog(log, $"[!] 组 {groupId} 读取失败，{indices.Count} 个 bank 未导出: {ex.Message}");
                done += indices.Count;
                continue;
            }
            var valid = game.ValidObjectCount(groupId);
            foreach (var i in indices)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if (i >= valid || i >= objects.Count || objects[i] is null) continue;
                var obj = objects[i];

                foreach (var id in U32s(obj, "WemIDs")) bankWemIds.Add(id);

                if (!obj.Fields.TryGetValue("BankData", out var raw) || raw is null) continue;
                try
                {
                    var bytes = AudioPipeline.FixWwiseBankData(raw);
                    if (bytes is null || bytes.Length < 4) continue;
                    File.WriteAllBytes(
                        Path.Combine(paths.ExtractedBanksDir, $"WwiseBankResource_{groupId}_{i}.bnk"), bytes);
                    written++;
                }
                catch (Exception ex)
                {
                    AudioPipeline.SafeLog(log, $"  [!] bank {groupId}:{i} 写出失败: {ex.Message}");
                }
                AudioPipeline.Report(progress, $"[{done}/{total}] bank {groupId}:{i}", done, total);
            }
        }

        AudioPipeline.SafeLog(log, Locale.S("pipe_res_banks_done", written, total, paths.ExtractedBanksDir));
        AudioPipeline.SafeLog(log, Locale.S("pipe_res_bank_wemids", bankWemIds.Count));
        return (written, bankWemIds.ToList());
    }

    /// <summary>
    /// 读一个 <c>Array_uint32</c> 字段。**注意**：原始类型容器走的是块拷贝快路径，
    /// 拿到的是 <c>int[]</c> 而不是 <c>List&lt;object?&gt;</c>，两种都要认。
    /// </summary>
    private static IEnumerable<uint> U32s(TypedObject obj, string field)
    {
        if (!obj.Fields.TryGetValue(field, out var value)) yield break;
        switch (value)
        {
            case int[] ints:
                foreach (var v in ints) yield return AudioPipeline.ToU32(v);
                break;
            case uint[] uints:
                foreach (var v in uints) yield return v;
                break;
            case long[] longs:
                foreach (var v in longs) yield return AudioPipeline.ToU32(v);
                break;
            case IReadOnlyList<object?> list:
                foreach (var v in list)
                    if (v is not null) yield return AudioPipeline.ToU32(Convert.ToInt64(v));
                break;
        }
    }

    // ---------------------------------------------------------------- WEM 音频

    /// <summary>
    /// 抽取 WEM 原始音频：streaming 的走 <c>StreamingDataSource</c>（按 Locator 从
    /// package 文件里读），内嵌的走 <c>WemData</c>。
    ///
    /// 实测：<c>WemSize == StreamingDataSource.Length == .wem 文件大小</c>，
    /// 且读出的字节与已有导出逐字节相同（头部是 <c>RIFF....WAVEfmt </c>）。
    /// </summary>
    public static int ExportWemAudio(string gameRoot, string targetDir,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);
        AudioPipeline.SafeLog(log, $"游戏根目录: {gameRoot}");
        AudioPipeline.SafeLog(log, $"WEM 输出  : {targetDir}");

        AudioPipeline.Report(progress, "加载流式图...");
        using var game = DecimaGame.Open(gameRoot, TypesJsonPath);

        var byGroup = new Dictionary<int, List<(int Index, string TypeName)>>();
        foreach (var (id, type) in game.FindObjects(WemType, IncludeDerivedTypes))
        {
            if (!byGroup.TryGetValue(id.GroupId, out var list))
                byGroup[id.GroupId] = list = new List<(int, string)>();
            list.Add((id.ObjectIndex, type.Name));
        }

        var total = byGroup.Sum(kv => kv.Value.Count);
        AudioPipeline.SafeLog(log, $"待处理 WEM: {total:N0} 个，分布在 {byGroup.Count:N0} 个组");

        int written = 0, skipped = 0, failed = 0, done = 0;
        foreach (var (groupId, items) in byGroup)
        {
            ct.ThrowIfCancellationRequested();

            var pending = items
                .Where(it => !File.Exists(Path.Combine(targetDir, $"{it.TypeName}_{groupId}_{it.Index}.wem")))
                .ToList();
            skipped += items.Count - pending.Count;
            if (pending.Count == 0) { done += items.Count; continue; }

            // 同上：只读需要的对象，别整组读（WEM 那些组同样可能极大）
            var wanted = new HashSet<int>(pending.Count);
            foreach (var (index, _) in pending) wanted.Add(index);

            IReadOnlyList<TypedObject> objects;
            try
            {
                objects = game.ReadGroupFiltered(groupId, wanted, false);   // 不读子组：group 499 有 2365 个子组
            }
            catch (Exception ex)
            {
                failed += pending.Count;
                done += items.Count;
                AudioPipeline.SafeLog(log, $"  [!] 组 {groupId} 读取失败（跳过 {pending.Count} 个）: {ex.Message}");
                continue;
            }
            var valid = game.ValidObjectCount(groupId);
            foreach (var (index, name) in pending)
            {
                done++;
                if (index < 0 || index >= valid || index >= objects.Count) { failed++; continue; }
                var obj = objects[index];
                if (obj is null) { failed++; continue; }
                try
                {
                    var bytes = ExtractWemBytes(game, obj);
                    if (bytes == null || bytes.Length == 0) { failed++; continue; }
                    File.WriteAllBytes(Path.Combine(targetDir, $"{name}_{groupId}_{index}.wem"), bytes);
                    written++;
                }
                catch (Exception ex)
                {
                    failed++;
                    AudioPipeline.SafeLog(log, $"  [!] {groupId}:{index} 抽取失败: {ex.Message}");
                }

                if ((done & 0x3F) == 0)
                    AudioPipeline.Report(progress, $"WEM: {done:N0} / {total:N0}（新写 {written:N0}）", done, total);
            }
            game.ReleaseCaches();   // 每组读完全部放掉，别累积
        }

        AudioPipeline.SafeLog(log, "");
        AudioPipeline.SafeLog(log, $"WEM 完成: 新写 {written:N0} / 跳过 {skipped:N0} / 失败 {failed:N0}");
        return written;
    }

    /// <summary>从一个 WwiseWemResource 取原始字节：优先 streaming 数据源，其次内嵌 WemData。</summary>
    private static byte[]? ExtractWemBytes(DecimaGame game, TypedObject obj)
    {
        if (obj["StreamingDataSource"] is TypedObject source)
        {
            var len = Convert.ToInt64(source["Length"] ?? 0L);
            if (len > 0) return game.ReadDataSource(source);
        }

        switch (obj["WemData"])
        {
            case byte[] raw:
                return raw;
            case IReadOnlyList<object?> list when list.Count > 0:
            {
                var buffer = new byte[list.Count];
                for (var i = 0; i < list.Count; i++)
                    buffer[i] = Convert.ToByte(list[i], System.Globalization.CultureInfo.InvariantCulture);
                return buffer;
            }
        }
        return null;
    }
}

