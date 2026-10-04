using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;
using OdradekSharp.Rtti;

namespace OdradekSharp.Ds2;

/// <summary>
/// Streaming graph built from the StreamingGraphResource stored in
/// {game}\LocalCacheWinGame\package\streaming_graph.core.
/// Port of odradek's StreamingGraphImpl (odradek-game-ds2/.../storage/StreamingGraphImpl.java).
/// </summary>
public sealed class StreamingGraph
{
    public readonly record struct Span(int FileIndex, int Offset, int Length);
    public readonly record struct Locator(int FileIndex, long Offset);

    public sealed class Group
    {
        public required int Id { get; init; }
        public required int TypeStart { get; init; }
        public required List<ClassTypeInfo> Types { get; init; }
        public required List<Span> Spans { get; init; }
        public required List<Locator> Locators { get; init; }
        public required List<Group> SubGroups { get; init; }
        public required int LinkStart { get; init; }
        public required List<int> Roots { get; init; }
        public override string ToString() => $"Group {Id} ({Types.Count} objects)";
    }

    public List<Group> Groups { get; } = [];
    public List<ClassTypeInfo> TypeTable { get; } = [];
    public List<string> Files { get; } = [];
    public byte[] LinkTable { get; private set; } = [];
    public TypedObject Resource { get; private set; } = null!;

    private readonly Dictionary<int, Group> _groupsById = [];
    private readonly List<Span> _spans = [];
    private readonly List<Locator> _locators = [];
    private readonly List<int> _subGroupIds = [];
    private readonly List<int> _rootIndices = [];

    public Group GetGroup(int id) => _groupsById.TryGetValue(id, out var g)
        ? g
        : throw new KeyNotFoundException($"Group not found: {id}");

    public bool TryGroup(int id, out Group group) => _groupsById.TryGetValue(id, out group!);

    /// <summary>Streaming link iterator over a byte range of the link table (StreamingGraphImpl.links).</summary>
    public LinkCursor Links(int position) => new(LinkTable, position);

    public static StreamingGraph Build(string path, TypeFactory factory, RttiReader reader, string gameRoot)
    {
        var graph = new StreamingGraph { GameRoot = gameRoot };
        var data = File.ReadAllBytes(path);
        var obj = reader.ReadFileObject(new BinaryReader(data));
        graph.Resource = obj;

        graph.Files.AddRange(((IReadOnlyList<object?>)obj.Fields["Files"]!).Select(f => (string)f!));
        graph._subGroupIds.AddRange(((IReadOnlyList<object?>)obj.Fields["SubGroups"]!).Select(v => (int)v!));
        graph._rootIndices.AddRange(((IReadOnlyList<object?>)obj.Fields["RootIndices"]!).Select(v => (int)v!));

        // spans / locators (StreamingGraphImpl.computeSpans / computeLocators)
        foreach (var s in (IReadOnlyList<object?>)obj.Fields["SpanTable"]!)
        {
            var span = (TypedObject)s!;
            var fileIndexAndIsPatch = (int)span.Fields["FileIndexAndIsPatch"]!;
            graph._spans.Add(new Span(fileIndexAndIsPatch & 0x7fffffff,
                (int)span.Fields["Offset"]!, (int)span.Fields["Length"]!));
        }
        foreach (var l in (IReadOnlyList<object?>)obj.Fields["LocatorTable"]!)
        {
            var locator = (TypedObject)l!;
            var value = (long)locator.Fields["Data"]!;
            graph._locators.Add(new Locator((int)(value & 0xffffff), (long)((ulong)value >> 24)));
        }

        graph.ReadTypeTable(obj, factory);
        graph.ReadLinkTable(obj);
        graph.BuildGroups(obj);
        return graph;
    }

    /// <summary>Port of StreamingGraphImpl.readTypeTable (StreamingGraphImpl.java:149-186).</summary>
    private void ReadTypeTable(TypedObject resource, TypeFactory factory)
    {
        var typeTableData = (IReadOnlyList<object?>)resource.Fields["TypeTableData"]!;
        var bytes = new byte[typeTableData.Count];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(sbyte)typeTableData[i]!;
        var r = new BinaryReader(bytes);

        var compression = r.ReadInt();
        var stride = r.ReadInt();
        var count = r.ReadInt();
        var count2 = r.ReadInt();
        var unk10 = r.ReadInt();
        if (compression != 0) throw new InvalidDataException($"Unsupported compression: {compression}");
        if (stride != 2) throw new InvalidDataException($"Unsupported stride: {stride}");
        if (count != count2) throw new InvalidDataException("Count mismatch");
        if (unk10 != 1) throw new InvalidDataException($"Unexpected unknown value: {unk10}");

        var hashes = (IReadOnlyList<object?>)resource.Fields["TypeHashes"]!;
        var index = new Dictionary<long, long>();
        for (var i = 0; i < count; i++)
        {
            var typeIndex = r.ReadUShort();
            var hash = (long)hashes[typeIndex]!;
            TypeTable.Add((ClassTypeInfo)factory.Resolve(TypeNameByHash(factory, hash)));
        }
    }

    /// <summary>Resolves a 64-bit type hash to a type name (DS2TypeFactory.computeTypeId).</summary>
    private static string TypeNameByHash(TypeFactory factory, long hash)
    {
        factory.EnsureHashIndex();
        return factory.NameByHash.TryGetValue(hash, out var name)
            ? name
            : throw new KeyNotFoundException($"Unknown type id in streaming graph: 0x{hash:X16}");
    }

    /// <summary>Port of StreamingGraphImpl.readLinkTable (StreamingGraphImpl.java:188-194).</summary>
    private void ReadLinkTable(TypedObject resource)
    {
        var fileIndex = (int)(long)resource.Fields["LinkTableID"]!;
        var size = (int)resource.Fields["LinkTableSize"]!;
        if (size <= 0)
        {
            LinkTable = [];
            return;
        }
        var file = Files[fileIndex];
        using var dataFile = DataFile.Open(ResolveGamePath(GameRoot!, file));
        LinkTable = dataFile.Read(0, size);
    }

    public string? GameRoot { get; set; }

    public static string ResolveGamePath(string gameRoot, string devicePath)
    {
        var parts = devicePath.Split(':', 2);
        var relative = parts.Length > 1 ? parts[1] : parts[0];
        return parts[0] switch
        {
            "source" => Path.Combine(gameRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
            "cache" => Path.Combine(gameRoot, "LocalCacheWinGame", relative.Replace('/', Path.DirectorySeparatorChar)),
            "tools" => Path.Combine(gameRoot, "tools", relative.Replace('/', Path.DirectorySeparatorChar)),
            _ => throw new InvalidDataException($"Unknown device path: {devicePath}"),
        };
    }

    /// <summary>Port of StreamingGraphImpl.computeGroups + GroupImpl.</summary>
    private void BuildGroups(TypedObject resource)
    {
        foreach (var g in (IReadOnlyList<object?>)resource.Fields["Groups"]!)
        {
            var data = (TypedObject)g!;
            var id = (int)data.Fields["GroupID"]!;
            var typeStart = (int)data.Fields["TypeStart"]!;
            var typeCount = (int)data.Fields["TypeCount"]!;
            var spanStart = (int)data.Fields["SpanStart"]!;
            var spanCount = (int)data.Fields["SpanCount"]!;
            var locatorStart = (int)data.Fields["LocatorStart"]!;
            var locatorCount = (int)data.Fields["LocatorCount"]!;
            var subGroupStart = (int)data.Fields["SubGroupStart"]!;
            var subGroupCount = (int)data.Fields["SubGroupCount"]!;
            var rootStart = (int)data.Fields["RootStart"]!;
            var rootCount = (int)data.Fields["RootCount"]!;

            var group = new Group
            {
                Id = id,
                TypeStart = typeStart,
                Types = TypeTable.GetRange(typeStart, typeCount),
                Spans = _spans.GetRange(spanStart, spanCount),
                Locators = _locators.GetRange(locatorStart, locatorCount),
                SubGroups = [], // filled after all groups exist
                LinkStart = (int)data.Fields["LinkStart"]!,
                Roots = _rootIndices.GetRange(rootStart, rootCount),
            };
            Groups.Add(group);
            _groupsById[id] = group;
            _subGroupRanges[group] = _subGroupIds.GetRange(subGroupStart, subGroupCount);
        }

        foreach (var group in Groups)
        {
            if (_subGroupRanges.TryGetValue(group, out var ids))
                group.SubGroups.AddRange(ids.Where(_groupsById.ContainsKey).Select(i => _groupsById[i]));
        }
        _subGroupRanges.Clear();
    }

    private readonly Dictionary<Group, List<int>> _subGroupRanges = [];
}

/// <summary>
/// Cursor over the streaming link table. Port of StreamingGraphImpl.readLink / readVarInt:
/// a big-endian 7-bit-group varint; bit 0x40 of the first byte signals an explicit group field.
/// </summary>
public sealed class LinkCursor(byte[] table, int position)
{
    private int _position = position;

    public bool HasNext => _position < table.Length;

    public GraphLink Next()
    {
        var first = table[_position++];
        int? group = null;
        int index;
        if ((first & 0x40) != 0)
        {
            group = ReadVarInt(first & 0xbf);
            index = ReadVarInt(table[_position++]);
        }
        else
        {
            index = ReadVarInt(first & 0xbf);
        }
        return new GraphLink(group, index);
    }

    private int ReadVarInt(int initial)
    {
        var temp = initial;
        var value = initial & 0x7f;
        while ((temp & 0x80) != 0)
        {
            temp = table[_position++];
            value = (value << 7) | (temp & 0x7f);
        }
        return value;
    }
}
