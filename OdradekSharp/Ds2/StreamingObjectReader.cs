using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;
using OdradekSharp.Rtti;

namespace OdradekSharp.Ds2;

/// <summary>
/// Streaming-side reader: reads whole groups of objects, resolving pointers through the streaming
/// link table and StreamingDataSource locators.
/// Port of odradek's StreamingObjectReader (odradek-game-ds2/.../storage/StreamingObjectReader.java).
/// </summary>
public sealed class StreamingObjectReader(RttiReader reader, StreamingGraph graph, string gameRoot)
{
    public readonly record struct GroupResult(StreamingGraph.Group Group, List<TypedObject> Objects);

    private readonly RttiReader _reader = reader;
    private readonly StreamingGraph _graph = graph;
    private readonly string _gameRoot = gameRoot;
    private readonly Dictionary<int, GroupResult> _cache = [];

    private List<GroupResult> _currentSubGroups = [];
    private GroupResult _currentGroup;
    private LinkCursor? _links;
    private int _locatorIndex;
    private bool _resolveLinks;

    public IReadOnlyList<TypedObject> Objects => _currentGroup.Objects;

    /// <summary>Optional per-object trace: object index, type and byte range inside its span.</summary>
    public bool TraceEnabled { get; set; }
    public List<(int Index, string Type, int Start, int End, string File, int SpanLength)> Trace { get; } = [];

    public GroupResult ReadGroup(int id, bool readSubgroups = true) => ReadGroup(id, [], readSubgroups);

    private GroupResult ReadGroup(int id, Dictionary<int, GroupResult> cache, bool readSubgroups)
    {
        var group = _graph.GetGroup(id); // throws KeyNotFoundException like odradek's "Group not found"
        if (_cache.TryGetValue(id, out var cached)) return cached;
        var result = ReadGroupInternal(group, readSubgroups);
        _cache[group.Id] = result;
        return result;
    }

    private GroupResult ReadGroupInternal(StreamingGraph.Group group, bool readSubgroups)
    {
        var subGroups = new List<GroupResult>(group.SubGroups.Count);
        if (readSubgroups)
        {
            foreach (var sub in group.SubGroups)
            {
                try
                {
                    subGroups.Add(ReadGroup(sub.Id, [], true));
                }
                catch (Exception e)
                {
                    // Deviates from odradek, which would propagate the exception: a child group whose
                    // objects need an unimplemented callback (Jolt physics / RigLogic) is skipped, so the
                    // requested group can still be read. Links pointing into the skipped group resolve to
                    // null; the readable objects around it are unaffected.
                    Warnings.Add($"subgroup {sub.Id} skipped: {e.Message}");
                }
            }
        }

        _currentSubGroups = subGroups;
        _resolveLinks = readSubgroups;

        if (_cache.TryGetValue(group.Id, out var cached)) return cached;
        var result = ReadSingleGroup(group);
        _cache[group.Id] = result;
        return result;
    }

    public List<string> Warnings { get; } = [];

    /// <summary>Port of StreamingObjectReader.readSingleGroup (:100-135).</summary>
    private GroupResult ReadSingleGroup(StreamingGraph.Group group)
    {
        var objects = new List<TypedObject>(group.Types.Count);
        foreach (var type in group.Types) objects.Add(new TypedObject { Type = type });

        var result = new GroupResult(group, objects);
        _currentGroup = result;
        _links = _graph.Links(group.LinkStart);
        _locatorIndex = 0;

        var index = 0;
        foreach (var span in group.Spans)
        {
            var file = _graph.Files[span.FileIndex];
            var path = StreamingGraph.ResolveGamePath(_gameRoot, file);
            using var data = DataFile.Open(path);
            var bytes = data.Read(span.Offset, span.Length);
            var spanReader = new BinaryReader(bytes);
            _reader.Context = new Context(this, span.Offset, file);
            while (spanReader.Remaining > 0)
            {
                if (index >= objects.Count)
                    throw new InvalidDataException(
                        $"Span has {spanReader.Remaining} leftover bytes but group {group.Id} declares {objects.Count} objects");
                var obj = objects[index];
                obj.Id = new ObjectId(group.Id, index);
                var start = spanReader.Position;
                try
                {
                    _reader.FillCompound(obj.Type, spanReader, obj);
                }
                catch (Exception e)
                {
                    if (TraceEnabled) Trace.Add((index, obj.Type.Name, start, spanReader.Position, file, span.Length));
                    throw new InvalidDataException(
                        $"failed reading object [{index}] {obj.Type.Name} ({group.Id}:{index}) at span offset {start}: {e.Message}", e);
                }
                if (TraceEnabled)
                    Trace.Add((index, obj.Type.Name, start, spanReader.Position, file, span.Length));
                index++;
            }
        }
        _reader.Context = null;
        return result;
    }

    /// <summary>Port of StreamingObjectReader.resolveLink (:178-239) and resolveStreamingDataSource (:157-176).</summary>
    private sealed class Context(StreamingObjectReader owner, int spanOffset, string file) : IStreamingContext
    {
        public bool ResolveLinks => owner._resolveLinks;

        public GraphLink NextLink() => (owner._links ?? throw new InvalidOperationException("no link cursor")).Next();

        public void OnStreamingDataSource(TypedObject dataSource)
        {
            if (!owner._resolveLinks) return;
            var channel = dataSource.Fields.TryGetValue("Channel", out var c) ? Convert.ToInt64(c!) : -1;
            var length = dataSource.Fields.TryGetValue("Length", out var l) ? Convert.ToInt64(l!) : 0;
            if (channel == -1 || length <= 0) return; // isValid()
            var locator = owner._currentGroup.Group.Locators[owner._locatorIndex++];
            dataSource.Fields["Locator"] = (long)((locator.Offset << 24) | (locator.FileIndex & 0xffffff));
        }

        public object? ResolveLink(PointerTypeInfo info, GraphLink link)
        {
            if (!owner._resolveLinks) return null;

            if (info.PointerKind == "StreamingRef")
            {
                // StreamingRef stores the *global group id* (StreamingObjectReader.java:188-196)
                return link.Group is { } gid ? new ObjectRef("StreamingRef", new ObjectId(gid, link.Index), null) : null;
            }

            StreamingObjectReader.GroupResult group;
            if (link.Group is { } subIndex)
            {
                // For Ref/cptr/WeakPtr the group field is an index into the parent's subGroups list
                if (subIndex < 0 || subIndex >= owner._currentSubGroups.Count) return null;
                group = owner._currentSubGroups[subIndex];
            }
            else
            {
                group = owner._currentGroup;
            }

            if (link.Index < 0 || link.Index >= group.Objects.Count) return null;
            var obj = group.Objects[link.Index];
            if (!IsAssignableFrom(info.TargetType, obj.Type)) return null; // odradek logs and returns null
            return new ObjectRef(info.PointerKind, new ObjectId(group.Group.Id, link.Index), obj);
        }
    }

    /// <summary>Port of ClassTypeInfo.isAssignableFrom (:77-87): name-based, recursive over bases.</summary>
    public static bool IsAssignableFrom(TypeInfo expected, ClassTypeInfo actual)
    {
        if (expected is not ClassTypeInfo cls) return false;
        if (cls.Name == actual.Name) return true;
        foreach (var b in actual.Bases)
            if (IsAssignableFrom(cls, b.Type)) return true;
        return false;
    }
}
