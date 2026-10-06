using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using OdradekSharp.Ds2;
using OdradekSharp.Rtti;

namespace WemLabeler.Pipeline;

/// <summary>一条边：某个 GraphSoundResource 通过它的 GraphProgram/NodeConstants 命中的一个 WwiseID。</summary>
public sealed class SoundChainHit
{
    public string ResourceName { get; init; } = "";
    /// <summary>GraphSoundResource 的对象坐标 <c>组:下标</c>（进 mapping）。</summary>
    public string GraphSound { get; init; } = "";
    public string GraphProgram { get; init; } = "";
    public string ExposedDataResource { get; init; } = "";
    public uint WwiseID { get; init; }
    /// <summary>WwiseID 对象的坐标 <c>组:下标</c>（诊断用，不进 mapping）。</summary>
    public string WwiseID_Coord { get; init; } = "";
}

/// <summary>一个 GraphSoundResource 及其跳到的全部 WwiseID。</summary>
public sealed class SoundChainSound
{
    public ObjectId Id { get; init; }
    public string ResourceName { get; init; } = "";
    public string GraphProgram { get; set; } = "";
    public string ExposedDataResource { get; set; } = "";
    public List<SoundChainHit> Hits { get; } = new();
}

/// <summary>跳链过程中「本该有、但拿不到」的对象明细。</summary>
public sealed class SoundChainProblem
{
    public string Stage { get; init; } = "";
    public string Coord { get; init; } = "";
    public string Reason { get; init; } = "";
    public override string ToString() => $"[{Stage}] {Coord}: {Reason}";
}

public sealed class SoundChainReport
{
    /// <summary>图上 GraphSoundResource 对象总数（纯元数据统计）。</summary>
    public int GraphSoundTotal { get; set; }
    /// <summary>成功读出 ResourceName 的 GraphSoundResource 数量。</summary>
    public int GraphSoundRead { get; set; }
    /// <summary>产出的 mapping 条目数（一行 = 一个 WwiseID）。</summary>
    public int Entries { get; set; }
    /// <summary>命中的不同 WwiseID 值个数。</summary>
    public int DistinctWwiseIds { get; set; }
    public int ProgramsFound { get; set; }
    public int ProgramsMissing { get; set; }
    public int NodeConstantsFound { get; set; }
    public int NodeConstantsMissing { get; set; }
    /// <summary>soft-linked 引用总数。</summary>
    public int SoftLinks { get; set; }
    /// <summary>soft-linked 里不是 WwiseID、因而按设计不读取的对象数。</summary>
    public int SoftLinksNotWwiseId { get; set; }
    /// <summary>是 WwiseID 但读不出来的数量（截断 / 解析失败）。</summary>
    public int WwiseIdsMissing { get; set; }
    /// <summary>链路涉及的组（含这四种类型中任意一种的组）。</summary>
    public int ChainGroups { get; set; }
    public int GroupsRead { get; set; }
    public int GroupsFailed { get; set; }
    /// <summary>缓存淘汰掉的组次数（淘汰会导致重读）。</summary>
    public int CacheEvictions { get; set; }
    /// <summary>因未移植回调而位于截断点之后、字节不可信的对象数（按组累计）。</summary>
    public int ObjectsPastTruncation { get; set; }
    public double Seconds { get; set; }
    public long PeakWorkingSetBytes { get; set; }
    public List<SoundChainProblem> Problems { get; } = new();

    public string Render()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== GraphSound → WwiseID 链路（直接读游戏数据，无 JSON 中间产物） ===");
        sb.AppendLine($"GraphSoundResource 总数 : {GraphSoundTotal}（成功读出 {GraphSoundRead}）");
        sb.AppendLine($"产出条目数             : {Entries}");
        sb.AppendLine($"不同 WwiseID 值        : {DistinctWwiseIds}");
        sb.AppendLine($"GraphProgramResource   : 命中 {ProgramsFound}，缺失/读不出 {ProgramsMissing}");
        sb.AppendLine($"NodeConstantsResource  : 命中 {NodeConstantsFound}，缺失/读不出 {NodeConstantsMissing}");
        sb.AppendLine($"soft-link 引用总数     : {SoftLinks}（其中 {SoftLinksNotWwiseId} 个不是 WwiseID，" +
                      $"{WwiseIdsMissing} 个是 WwiseID 但读不出）");
        sb.AppendLine($"链路涉及的组           : {ChainGroups}（实际读取 {GroupsRead}，失败 {GroupsFailed}，" +
                      $"淘汰重读 {CacheEvictions}）");
        sb.AppendLine($"截断点之后的对象       : {ObjectsPastTruncation}");
        sb.AppendLine($"耗时                   : {Seconds:F2} s");
        sb.AppendLine($"峰值内存               : {PeakWorkingSetBytes / 1048576.0:F0} MiB");
        sb.AppendLine($"问题明细               : {Problems.Count} 条");
        foreach (var p in Problems.Take(50)) sb.AppendLine("  " + p);
        if (Problems.Count > 50) sb.AppendLine($"  ... 另有 {Problems.Count - 50} 条");
        return sb.ToString();
    }
}

/// <summary>
/// 直接从游戏数据解析 GraphSoundResource → GraphProgramResource → NodeConstantsResource → WwiseID，
/// 不经过任何 JSON 中间产物。
///
/// 字段（已在 types.json 上逐一核实，不是猜的）：
/// <code>
/// GraphSoundResource.ResourceName             : String                       （继承自 SoundResource）
/// GraphSoundResource.GraphProgram             : Ref_GraphProgramResource
/// GraphProgramResource.ExposedDataResource    : Ref_NodeConstantsResource
/// NodeConstantsResource.Parameters            : ProgramParameterList
///   └ DefaultSoftLinkedObjects                : Array_Ref_RTTIRefObject
/// WwiseID.Id                                  : uint32
/// </code>
///
/// 性能取向（每一条都对应一次实测）：
/// <list type="number">
/// <item>一个进程、图只加载一次：绝不逐对象起进程（每个约 4 s × 5700 = 6 小时）。</item>
/// <item>按需读组：<c>ReadGroupFiltered</c>，只要链路上那四种类型的下标，其余对象只留类型桩
/// （<c>CollectPayload=false</c> 的内部机制），所以不会把组里无关的大数组拖进内存。</item>
/// <item>有界 LRU 组缓存（按 groupId）。**不**照抄 TypeExporter「每组处理完 ReleaseCaches()」——
/// 那是为一次性批量导出设计的；走链会反复回到同一批组，大组重解析代价极高。</item>
/// <item>先查类型再决定读不读：soft-linked 目标用 <c>group.Types[i]</c> O(1) 判类型，不是 WwiseID 就不读。</item>
/// <item>**每个组只读一次**。这一点是性能的关键：<c>ReadGroupFiltered</c> 无论要几个对象都必须
/// 顺序解出整组，所以「同一组按不同下标读两趟」= 两组份工作量。做法是先用纯元数据把每组里
/// 属于这四种类型的下标全部收集起来，第一次读该组时就把它们一次读齐并缓存；后续任何下标请求
/// 都是缓存命中（实测把原先 4 趟 × ~520 组 = 2122 次组读取降到 520 次，137 s → 约 30 s）。</item>
/// </list>
/// </summary>
public static class SoundChainResolver
{
    /// <summary>链路上用到的四种类型，顺序无关。</summary>
    private static readonly string[] ChainTypes =
    {
        "GraphSoundResource", "GraphProgramResource", "NodeConstantsResource", "WwiseID"
    };

    public sealed class Options
    {
        /// <summary>是否匹配派生类型。默认 false，与仓库里既有的 JSON 导出（--exact）保持一致。</summary>
        public bool IncludeDerived { get; init; }
        public int Threads { get; init; } = Math.Min(4, Environment.ProcessorCount);
        /// <summary>并发组读取占用的解压数据上限。</summary>
        public long MaxMemoryBytes { get; init; } = 1024L * 1024 * 1024;
        /// <summary>
        /// 组缓存上界，按缓存列表里的对象槽位计（含类型桩）。0 = 自动（刚好装下所有涉及的组，不淘汰）。
        /// 给了正数就是硬上界，超界按最近最少使用淘汰，淘汰会导致重读。
        /// </summary>
        public long MaxCachedObjects { get; init; }
        /// <summary>日志回调。</summary>
        public Action<string>? Log { get; init; }
    }

    /// <summary>解析整条链；返回按 (组, 下标) 排序的 GraphSound 列表，每个内部按 soft-link 顺序。</summary>
    public static List<SoundChainSound> Resolve(DecimaGame game, Options options, Action<string>? log,
        CancellationToken ct, out SoundChainReport report)
    {
        var sw = Stopwatch.StartNew();
        var problems = new List<SoundChainProblem>();
        void Problem(string stage, string coord, string reason)
        {
            lock (problems) problems.Add(new SoundChainProblem { Stage = stage, Coord = coord, Reason = reason });
        }

        var soundType = (ClassTypeInfo)game.Types.Resolve("GraphSoundResource");
        var acceptable = AcceptableTypeNames(game, ChainTypes, options.IncludeDerived);

        // ------------------------------------------------ 阶段 0：纯元数据扫描（不读任何对象字节）
        // 一次遍历同时得到：① 所有 GraphSoundResource；② 每个组里属于链路四类型的下标集合。
        // 后者是「每组只读一次」的前提 —— 读组时把它们一次读齐。
        var soundIds = new List<ObjectId>();
        var chainIndexByGroup = new Dictionary<int, HashSet<int>>();
        long chainSlots = 0;
        foreach (var group in game.Graph.Groups)
        {
            ct.ThrowIfCancellationRequested();
            HashSet<int>? indices = null;
            for (var i = 0; i < group.Types.Count; i++)
            {
                var name = group.Types[i].Name;
                if (!acceptable.Contains(name)) continue;
                (indices ??= []).Add(i);
                if (name == soundType.Name) soundIds.Add(new ObjectId(group.Id, i));
            }
            if (indices is null) continue;
            chainIndexByGroup[group.Id] = indices;
            chainSlots += group.Types.Count; // 缓存项按整组槽位计费
        }
        var cacheBudget = options.MaxCachedObjects > 0 ? options.MaxCachedObjects : Math.Max(chainSlots, 1);
        SafeLog(log, $"  GraphSoundResource: {soundIds.Count:N0} 个；链路涉及的组 {chainIndexByGroup.Count:N0} 个、" +
                     $"共 {chainSlots:N0} 个对象槽位（缓存上界 {cacheBudget:N0}）");

        using var gate = new MemoryGate(Math.Max(options.MaxMemoryBytes, 64L * 1024 * 1024));
        using var cache = new GroupCache(game, chainIndexByGroup, cacheBudget);

        // ------------------------------------------------ 阶段 1：ResourceName + GraphProgram
        var soundName = new ConcurrentDictionary<ObjectId, string>();
        var soundProgram = new ConcurrentDictionary<ObjectId, ObjectId>();
        RunPhase(game, cache, gate, options, soundIds, "GraphSoundResource", ct,
            (id, obj) =>
            {
                soundName[id] = obj.Fields.TryGetValue("ResourceName", out var n) ? n as string ?? "" : "";
                if (RefId(obj, "GraphProgram") is { } program) soundProgram[id] = program;
            },
            (id, reason) => Problem("GraphSoundResource", id.ToString(), reason), problems, log);
        SafeLog(log, $"  阶段1: 读出 {soundName.Count:N0} 个，其中 {soundProgram.Count:N0} 个有 GraphProgram");

        // ------------------------------------------------ 阶段 2：ExposedDataResource
        var programTarget = new ConcurrentDictionary<ObjectId, ObjectId>();
        RunPhase(game, cache, gate, options, soundProgram.Values.Distinct(), "GraphProgramResource", ct,
            (id, obj) =>
            {
                if (RefId(obj, "ExposedDataResource") is { } nc) programTarget[id] = nc;
            },
            (id, reason) => Problem("GraphProgramResource", id.ToString(), reason), problems, log);
        SafeLog(log, $"  阶段2: {programTarget.Count:N0}/{soundProgram.Count:N0} 个 GraphProgramResource " +
                     "指向 NodeConstantsResource");

        // ------------------------------------------------ 阶段 3：DefaultSoftLinkedObjects
        var softLinked = new ConcurrentDictionary<ObjectId, List<ObjectId>>();
        RunPhase(game, cache, gate, options, programTarget.Values.Distinct(), "NodeConstantsResource", ct,
            (id, obj) => softLinked[id] = SoftLinkTargets(obj),
            (id, reason) => Problem("NodeConstantsResource", id.ToString(), reason), problems, log);
        SafeLog(log, $"  阶段3: {softLinked.Count:N0}/{programTarget.Count:N0} 个 NodeConstantsResource 读出");

        // ------------------------------------------------ 阶段 4：先判类型，只读真正的 WwiseID
        var allSoft = new List<ObjectId>();
        foreach (var list in softLinked.Values) allSoft.AddRange(list);
        var wwiseIds = new List<ObjectId>();
        var notWwise = 0;
        foreach (var id in allSoft.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            if (Matches(game.Types.Resolve("WwiseID") as ClassTypeInfo, game.ObjectType(id), options.IncludeDerived))
                wwiseIds.Add(id);
            else notWwise++;
        }
        var wwiseValue = new ConcurrentDictionary<ObjectId, uint>();
        RunPhase(game, cache, gate, options, wwiseIds, "WwiseID", ct,
            (id, obj) =>
            {
                if (obj.Fields.TryGetValue("Id", out var v) && v is not null)
                    wwiseValue[id] = AudioPipeline.ToU32(Convert.ToInt32(v));
            },
            (id, reason) => Problem("WwiseID", id.ToString(), reason), problems, log);
        SafeLog(log, $"  阶段4: soft-link {allSoft.Count:N0} 个（{notWwise:N0} 个不是 WwiseID，按设计未读），" +
                     $"WwiseID {wwiseIds.Count:N0} 个，读出 {wwiseValue.Count:N0} 个");

        // ------------------------------------------------ 阶段 5：按 GraphSound 顺序组装
        var wwiseType = (ClassTypeInfo)game.Types.Resolve("WwiseID");
        var sounds = new List<SoundChainSound>();
        int programsMissing = 0, nodeConstantsMissing = 0, wwiseMissing = 0;
        foreach (var soundId in soundIds.OrderBy(i => i.GroupId).ThenBy(i => i.ObjectIndex))
        {
            var sound = new SoundChainSound { Id = soundId, ResourceName = soundName.GetValueOrDefault(soundId, "") };
            sounds.Add(sound);

            if (!soundProgram.TryGetValue(soundId, out var programId))
            {
                programsMissing++;
                if (soundName.ContainsKey(soundId))
                    Problem("GraphProgram", soundId.ToString(),
                        "GraphSoundResource 的 GraphProgram 为空（引用缺失）");
                continue;
            }
            sound.GraphProgram = programId.ToString();
            if (!programTarget.TryGetValue(programId, out var ncId))
            {
                nodeConstantsMissing++;
                Problem("ExposedDataResource", programId.ToString(),
                    $"GraphProgramResource 的 ExposedDataResource 为空（来自 GraphSound {soundId}）");
                continue;
            }
            sound.ExposedDataResource = ncId.ToString();
            if (!softLinked.TryGetValue(ncId, out var links)) continue;

            foreach (var target in links)
            {
                if (!Matches(wwiseType, game.ObjectType(target), options.IncludeDerived)) continue;
                if (!wwiseValue.TryGetValue(target, out var value))
                {
                    wwiseMissing++;
                    continue; // 已在阶段 4 记为 problem
                }
                sound.Hits.Add(new SoundChainHit
                {
                    ResourceName = sound.ResourceName,
                    GraphSound = soundId.ToString(),
                    GraphProgram = sound.GraphProgram,
                    ExposedDataResource = sound.ExposedDataResource,
                    WwiseID = value,
                    WwiseID_Coord = target.ToString(),
                });
            }
        }

        var proc = Process.GetCurrentProcess();
        report = new SoundChainReport
        {
            GraphSoundTotal = soundIds.Count,
            GraphSoundRead = soundName.Count,
            Entries = sounds.Sum(s => s.Hits.Count),
            DistinctWwiseIds = sounds.SelectMany(s => s.Hits).Select(h => h.WwiseID).Distinct().Count(),
            ProgramsFound = soundProgram.Count,
            ProgramsMissing = programsMissing,
            NodeConstantsFound = programTarget.Count,
            NodeConstantsMissing = nodeConstantsMissing,
            SoftLinks = allSoft.Count,
            SoftLinksNotWwiseId = notWwise,
            WwiseIdsMissing = wwiseMissing,
            ChainGroups = chainIndexByGroup.Count,
            GroupsRead = cache.GroupsRead,
            GroupsFailed = cache.GroupsFailed,
            CacheEvictions = cache.CacheEvictions,
            ObjectsPastTruncation = cache.ObjectsPastTruncation,
            Seconds = sw.Elapsed.TotalSeconds,
            PeakWorkingSetBytes = proc.PeakWorkingSet64,
        };
        lock (problems) report.Problems.AddRange(problems);
        return sounds;
    }

    private static bool Matches(ClassTypeInfo? expected, ClassTypeInfo actual, bool includeDerived) =>
        expected is not null && (includeDerived
            ? StreamingObjectReader.IsAssignableFrom(expected, actual)
            : actual.Name == expected.Name);

    /// <summary>四种链类型 +（可选）它们的派生类型，全部名字放进一个 HashSet，元数据扫描时 O(1) 查。</summary>
    private static HashSet<string> AcceptableTypeNames(DecimaGame game, string[] roots, bool includeDerived)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var rootTypes = new List<ClassTypeInfo>();
        foreach (var name in roots)
        {
            if (game.Types.Resolve(name) is not ClassTypeInfo cls) continue;
            rootTypes.Add(cls);
            result.Add(cls.Name);
        }
        if (!includeDerived) return result;

        foreach (var name in game.Types.KnownNames)
        {
            if (game.Types.Resolve(name) is not ClassTypeInfo cls || result.Contains(cls.Name)) continue;
            foreach (var root in rootTypes)
            {
                if (!StreamingObjectReader.IsAssignableFrom(root, cls)) continue;
                result.Add(cls.Name);
                break;
            }
        }
        return result;
    }

    private static ObjectId? RefId(TypedObject obj, string field) =>
        obj.Fields.TryGetValue(field, out var v) && v is ObjectRef r ? r.Id : null;

    /// <summary>NodeConstantsResource.Parameters.DefaultSoftLinkedObjects → 目标 ObjectId 列表。</summary>
    private static List<ObjectId> SoftLinkTargets(TypedObject nodeConstants)
    {
        var result = new List<ObjectId>();
        if (nodeConstants.Fields.TryGetValue("Parameters", out var p) && p is TypedObject parameters &&
            parameters.Fields.TryGetValue("DefaultSoftLinkedObjects", out var list) &&
            list is IReadOnlyList<object?> refs)
        {
            foreach (var item in refs)
                if (item is ObjectRef r) result.Add(r.Id);
        }
        return result;
    }

    /// <summary>
    /// 一趟：把 ids 按所属组分桶，每个组只读一次（缓存保证），并行跑。
    /// 对象下标 ≥ ValidObjectCount 说明该组在它之前就已失去同步（未移植回调），字节不可信，记为问题。
    /// </summary>
    private static void RunPhase(DecimaGame game, GroupCache cache, MemoryGate gate, Options options,
        IEnumerable<ObjectId> ids, string stage, CancellationToken ct,
        Action<ObjectId, TypedObject> visit, Action<ObjectId, string> onProblem, List<SoundChainProblem> problems,
        Action<string>? log)
    {
        var jobs = ids.Distinct()
            .GroupBy(i => i.GroupId)
            .Select(g => (GroupId: g.Key, Wanted: g.Select(i => i.ObjectIndex).ToHashSet()))
            .ToList();
        if (jobs.Count == 0) return;

        var phaseSw = Stopwatch.StartNew();
        var spanBytesTotal = jobs.Sum(j => game.Graph.GetGroup(j.GroupId).Spans.Sum(s => (long)s.Length));
        var hitsBefore = cache.Hits;
        var readsBefore = cache.GroupsRead;

        var truncatedGroups = new ConcurrentDictionary<int, string>();
        Parallel.ForEach(jobs, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, options.Threads),
            CancellationToken = ct,
        }, job =>
        {
            ct.ThrowIfCancellationRequested();
            var group = game.Graph.GetGroup(job.GroupId);
            var spanBytes = Math.Min(group.Spans.Sum(s => (long)s.Length), gate.Budget);
            gate.Enter(spanBytes);
            try
            {
                if (!cache.TryGet(job.GroupId, job.Wanted, out var objects, out var error))
                {
                    foreach (var index in job.Wanted)
                        onProblem(new ObjectId(job.GroupId, index), $"组读取失败: {error}");
                    return;
                }
                var valid = game.ValidObjectCount(job.GroupId);
                if (valid < group.Types.Count)
                    truncatedGroups[job.GroupId] =
                        $"组 {job.GroupId} 在对象 [{valid}] 处截断" +
                        $"（{group.Types[Math.Min(valid, group.Types.Count - 1)].Name} 需要未移植的回调），" +
                        $"其后 {group.Types.Count - valid} 个对象不可用";
                foreach (var index in job.Wanted)
                {
                    if (index >= valid)
                    {
                        onProblem(new ObjectId(job.GroupId, index), "位于组的截断点之后，字节不可信（未移植的回调）");
                        continue;
                    }
                    if (index < 0 || index >= objects.Count) continue;
                    if (objects[index] is { } o) visit(new ObjectId(job.GroupId, index), o);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                foreach (var index in job.Wanted) onProblem(new ObjectId(job.GroupId, index), $"异常: {e.Message}");
            }
            finally
            {
                gate.Exit(spanBytes);
            }
        });

        phaseSw.Stop();
        SafeLog(log, $"    [{stage}] {jobs.Count:N0} 组 / {spanBytesTotal / 1048576.0:F0} MiB span，" +
                     $"实读 {cache.GroupsRead - readsBefore:N0} 组，缓存命中 {cache.Hits - hitsBefore:N0}，" +
                     $"{phaseSw.Elapsed.TotalSeconds:F1}s");

        foreach (var (groupId, message) in truncatedGroups)
        {
            lock (problems)
                problems.Add(new SoundChainProblem { Stage = stage, Coord = $"group {groupId}", Reason = message });
            SafeLog(log, "  [!] " + message);
        }
    }

    internal static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); } catch { }
    }

    /// <summary>
    /// 有界 LRU 组缓存，按 groupId 键。
    ///
    /// 关键点：<c>ReadGroupFiltered</c> 必须顺序解出整组才能定位到目标对象，所以要几个对象都一样贵。
    /// 因此这里第一次读某个组时，就把「该组里所有属于链路四类型的下标」（<c>chainIndexByGroup</c>，
    /// 纯元数据算出来）一次读齐，之后再要这个组的任何下标都是命中 —— 一个组只付一次解析代价。
    /// 一个缓存项按整组槽位计费（没被要的对象是类型桩），超界按最近最少使用淘汰。
    /// </summary>
    private sealed class GroupCache(DecimaGame game, Dictionary<int, HashSet<int>> chainIndexByGroup,
        long maxObjects) : IDisposable
    {
        private sealed class Entry
        {
            public required IReadOnlyList<TypedObject> Objects { get; init; }
            public required HashSet<int> Materialized { get; init; }
            public required LinkedListNode<int> Node { get; init; }
        }

        private readonly Dictionary<int, Entry> _map = [];
        private readonly LinkedList<int> _order = new(); // 最少使用的在表头
        private readonly ConcurrentDictionary<int, object> _groupLocks = new();
        private readonly Lock _sync = new();

        private long _slots;
        private int _groupsRead;

        public int GroupsRead => _groupsRead;
        public int GroupsFailed { get; private set; }
        public int ObjectsPastTruncation { get; private set; }
        public int CacheEvictions { get; private set; }
        /// <summary>缓存命中次数（评估「反复回到同一批组」的收益）。</summary>
        public int Hits;

        public bool TryGet(int groupId, IReadOnlyCollection<int> wanted,
            out IReadOnlyList<TypedObject> objects, out string? error)
        {
            // 每个组一把锁：同一组的读取串行化（缓存项必须整体一致），不同组仍然并行。
            var groupLock = _groupLocks.GetOrAdd(groupId, _ => new object());
            lock (groupLock)
            {
                if (_map.TryGetValue(groupId, out var hit) && wanted.All(hit.Materialized.Contains))
                {
                    Touch(hit);
                    Interlocked.Increment(ref Hits);
                    objects = hit.Objects;
                    error = null;
                    return true;
                }

                // 一次读齐：该组的全部链路类型下标 + 上次读过的 + 这次要的。
                var union = new HashSet<int>(wanted);
                if (chainIndexByGroup.TryGetValue(groupId, out var chain)) union.UnionWith(chain);
                if (hit is not null) union.UnionWith(hit.Materialized);

                IReadOnlyList<TypedObject> list;
                try
                {
                    list = game.ReadGroupFiltered(groupId, union, readSubgroups: false);
                }
                catch (Exception e)
                {
                    GroupsFailed++;
                    objects = [];
                    error = e.Message;
                    return false;
                }
                Interlocked.Increment(ref _groupsRead);

                var valid = game.ValidObjectCount(groupId);
                var types = game.Graph.GetGroup(groupId).Types;
                if (valid < types.Count) ObjectsPastTruncation += types.Count - valid;

                var entry = new Entry
                {
                    Objects = list,
                    Materialized = union,
                    Node = _order.AddLast(groupId),
                };
                Put(groupId, entry);
                objects = list;
                error = null;
                return true;
            }
        }

        private void Touch(Entry entry)
        {
            lock (_sync)
            {
                if (entry.Node.List is null) return;
                _order.Remove(entry.Node);
                _order.AddLast(entry.Node);
            }
        }

        private void Put(int groupId, Entry entry)
        {
            lock (_sync)
            {
                if (_map.TryGetValue(groupId, out var old))
                {
                    _slots -= old.Objects.Count;
                    if (old.Node.List is not null) _order.Remove(old.Node);
                }
                _map[groupId] = entry;
                _slots += entry.Objects.Count;
                while (_slots > maxObjects && _order.First is { } first)
                {
                    if (!_map.Remove(first.Value, out var evicted)) { _order.RemoveFirst(); continue; }
                    _slots -= evicted.Objects.Count;
                    _order.RemoveFirst();
                    CacheEvictions++;
                }
            }
        }

        public void Dispose() => _map.Clear();
    }
}
