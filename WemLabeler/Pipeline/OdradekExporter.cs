using System.IO;
using OdradekSharp.Ds2;
using OdradekSharp.Export;
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
    /// <summary>需要导出的 6 种资源，以及它们各自的目标目录。</summary>
    public static readonly (string Type, string Folder)[] TargetTypes =
    {
        ("WwiseWemResource", "WemResJson"),
        ("WwiseBankResource", "BankRes"),
        ("GraphSoundResource", "GraphSoundRes"),
        ("GraphProgramResource", "GraphPgmRes"),
        ("NodeConstantsResource", "NodeConstRes"),
        ("WwiseID", "WwiseID"),
    };

    /// <summary>WEM 原始音频对应的类型。</summary>
    public const string WemType = "WwiseWemResource";

    /// <summary>
    /// 是否连**派生类型**一起导出。默认 false，与原 odradek 的导出结果保持一致：
    /// 例如 <c>WwiseWemLocalizedResource : WwiseWemResource</c> 有 268 个对象，
    /// 它们只分布在少数几个超大的本地化组里 —— 读那些组（还要读子组）会瞬间吃掉几个 GB。
    /// 现有 pipeline 也只会读 <c>WwiseWemResource_*.json</c>，所以精确匹配既是原本的行为也更省资源。
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

    // ---------------------------------------------------------------- JSON 导出

    public sealed record ExportSummary(int Written, int Skipped, int Failed, int Groups)
    {
        public override string ToString() => $"新写 {Written:N0} / 跳过 {Skipped:N0} / 失败 {Failed:N0}";
    }
    /// <summary>
    /// 按类型把对象导出成 JSON，落到 &lt;BaseDir&gt;\&lt;类型目录&gt;\&lt;类型&gt;_&lt;组&gt;_&lt;下标&gt;.json。
    /// 已全部存在时整类跳过。
    /// </summary>
    public static ExportSummary ExportJson(string gameRoot, PipelinePaths paths,
        IProgress<PipelineProgress>? progress, Action<string>? log, CancellationToken ct)
    {
        AudioPipeline.SafeLog(log, $"游戏根目录: {gameRoot}");
        AudioPipeline.SafeLog(log, $"输出到    : {paths.BaseDir}");
        AudioPipeline.SafeLog(log, $"RTTI 定义 : {TypesJsonPath}");

        AudioPipeline.Report(progress, "加载流式图...");
        using var game = DecimaGame.Open(gameRoot, TypesJsonPath);
        AudioPipeline.SafeLog(log,
            $"图: {game.Graph.Groups.Count:N0} 个组, {game.Graph.TypeTable.Count:N0} 个类型表项, {game.Graph.Files.Count:N0} 个文件");
        AudioPipeline.SafeLog(log, "");

        int written = 0, skipped = 0, failed = 0, groups = 0;

        foreach (var (typeName, folder) in TargetTypes)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.Combine(paths.BaseDir, folder);
            Directory.CreateDirectory(dir);

            // 纯元数据数一遍（不读任何对象字节），用于「本地都已有 → 整类跳过」
            var expected = 0;
            foreach (var _ in game.FindObjects(typeName, IncludeDerivedTypes)) expected++;
            var have = Directory.GetFiles(dir, "*.json").Length;
            if (expected > 0 && have >= expected)
            {
                skipped += expected;
                AudioPipeline.SafeLog(log, $"{typeName}: 已有 {have:N0} 个，跳过");
                continue;
            }

            AudioPipeline.SafeLog(log, $"{typeName}: 待导出 {expected:N0} 个 → {dir}");
            AudioPipeline.Report(progress, $"{typeName}: 导出中...");

            // 用库里的批量导出：先纯元数据找出「哪些组里有这个类型」，再每组只读一次（并行），
            // 且只保留要写的对象。
            // 关键 ReadSubgroups=false：不递归子组（locator 照样解析，输出仍与 odradek 一致）。
            // 千万不能逐对象调 ReadObject —— 对象没有长度字段，读 1 个对象 = 顺序解析整组，
            // group 499 有 12 万个对象，逐对象调就是「卡在 432 个、内存爆炸」的根源。
            var report = TypeExporter.Export(game, typeName, new ExportOptions
            {
                OutputDirectory = dir,
                IncludeDerived = false,   // 等于 CLI 的 --exact（该选项默认 true，必须显式关掉）
                ReadSubgroups = false,    // 不读子组：省内存，且不改变输出
                Threads = Math.Min(4, Environment.ProcessorCount),
            }, msg => AudioPipeline.SafeLog(log, "  " + msg));

            written += report.Exported;
            failed += Math.Max(0, report.Requested - report.Exported);
            groups += report.GroupsRead;

            foreach (var w in report.Warnings.Take(5)) AudioPipeline.SafeLog(log, "  [!] " + w);
            AudioPipeline.SafeLog(log, $"  → {report}");
            AudioPipeline.Report(progress, $"{typeName}: {report.Exported:N0} 个（{report.Seconds:F1}s）");
        }

        var summary = new ExportSummary(written, skipped, failed, groups);
        AudioPipeline.SafeLog(log, "");
        AudioPipeline.SafeLog(log, $"完成: {summary}");
        return summary;
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

