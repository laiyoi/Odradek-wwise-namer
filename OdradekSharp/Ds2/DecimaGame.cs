using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;
using OdradekSharp.Rtti;

namespace OdradekSharp.Ds2;

/// <summary>
/// Entry point of the port: opens a DS2 installation, loads the type factory and the streaming graph,
/// and exposes object search / reading.
/// Mirrors odradek's DS2Game (odradek-game-ds2/.../game/DS2Game.java).
/// </summary>
public sealed class DecimaGame : IDisposable
{
    public string Root { get; }
    public TypeFactory Types { get; }
    public RttiReader Reader { get; }
    public StreamingGraph Graph { get; }
    public StreamingObjectReader Objects { get; }

    private readonly Dictionary<int, List<TypedObject>> _groupCache = [];

    public string StreamingGraphPath { get; private set; } = "";

    private DecimaGame(string root, TypeFactory types, RttiReader reader, StreamingGraph graph)
    {
        Root = root;
        Types = types;
        Reader = reader;
        Graph = graph;
        Objects = new StreamingObjectReader(reader, graph, root);
    }

    /// <summary>Recognises a DS2 installation the same way odradek does: DS2.exe in the root.</summary>
    public static bool Supports(string gameDir) => File.Exists(Path.Combine(gameDir, "DS2.exe"));

    public static DecimaGame Open(string gameDir, string? typesJson = null, string? extensionsJson = null)
    {
        if (!Directory.Exists(gameDir)) throw new DirectoryNotFoundException(gameDir);
        if (!Supports(gameDir))
            Console.Error.WriteLine($"warning: no DS2.exe found in {gameDir} (continuing anyway)");

        typesJson ??= Path.Combine(AppContext.BaseDirectory, "Data", "types.json");
        extensionsJson ??= Path.Combine(Path.GetDirectoryName(typesJson)!, "extensions.json");

        var types = TypeFactory.Load(typesJson, extensionsJson);
        var reader = new RttiReader(types);
        var graphPath = Path.Combine(gameDir, "LocalCacheWinGame", "package", "streaming_graph.core");
        if (!File.Exists(graphPath)) throw new FileNotFoundException("streaming graph not found", graphPath);

        var graph = StreamingGraph.Build(graphPath, types, reader, gameDir);
        graph.GameRoot = gameDir;
        var game = new DecimaGame(gameDir, types, reader, graph) { StreamingGraphPath = graphPath };
        return game;
    }

    /// <summary>
    /// Enumerates all objects whose type matches <paramref name="typeName"/> (including derived types).
    /// This is metadata-only: no object payload is read.
    /// </summary>
    public IEnumerable<(ObjectId Id, ClassTypeInfo Type)> FindObjects(string typeName, bool includeDerived = true)
    {
        var target = Types.Resolve(typeName);
        if (target is not ClassTypeInfo targetClass)
            throw new ArgumentException($"{typeName} is not a compound type", nameof(typeName));

        foreach (var group in Graph.Groups)
        {
            for (var i = 0; i < group.Types.Count; i++)
            {
                var type = group.Types[i];
                var matches = includeDerived
                    ? StreamingObjectReader.IsAssignableFrom(targetClass, type)
                    : type.Name == targetClass.Name;
                if (matches) yield return (new ObjectId(group.Id, i), type);
            }
        }
    }

    /// <summary>Type name of an object without reading its payload (ObjectIdHolder.objectType).</summary>
    public ClassTypeInfo ObjectType(ObjectId id) => Graph.GetGroup(id.GroupId).Types[id.ObjectIndex];

    /// <summary>Reads the whole group and returns the object at the given index (odradek's readObject).</summary>
    public TypedObject ReadObject(ObjectId id, bool readSubgroups = true)
    {
        var objects = ReadGroup(id.GroupId, readSubgroups);
        if (id.ObjectIndex < 0 || id.ObjectIndex >= objects.Count)
            throw new ArgumentOutOfRangeException(nameof(id),
                $"object index {id.ObjectIndex} out of range for group {id.GroupId} ({objects.Count} objects)");
        return objects[id.ObjectIndex];
    }

    public IReadOnlyList<TypedObject> ReadGroup(int groupId, bool readSubgroups = true)
    {
        if (_groupCache.TryGetValue(groupId, out var cached)) return cached;
        var result = Objects.ReadGroup(groupId, readSubgroups);
        _groupCache[groupId] = result.Objects;
        return result.Objects;
    }

    /// <summary>Reads raw bytes of a StreamingDataSource (locator -> file/offset/length).</summary>
    public byte[] ReadDataSource(TypedObject dataSource)
    {
        var locator = (long)dataSource.Fields["Locator"]!;
        var fileId = (int)(locator & 0xffffff);
        var fileOffset = (long)((ulong)locator >> 24);
        var offset = Convert.ToInt64(dataSource.Fields["Offset"]!);
        var length = Convert.ToInt32(dataSource.Fields["Length"]!);
        var file = Graph.Files[fileId];
        using var data = DataFile.Open(StreamingGraph.ResolveGamePath(Root, file));
        return data.Read(fileOffset + offset, length);
    }

    public void Dispose() => Types.Dispose();
}
