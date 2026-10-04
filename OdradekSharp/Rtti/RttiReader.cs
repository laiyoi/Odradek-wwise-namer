using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;

namespace OdradekSharp.Rtti;

/// <summary>One decoded link entry of a streaming group (StreamingGraph.Link).</summary>
public readonly record struct GraphLink(int? Group, int Index);

/// <summary>
/// Streaming-side hooks used by the reader (port of StreamingObjectReader's overrides).
/// </summary>
public interface IStreamingContext
{
    bool ResolveLinks { get; }
    GraphLink NextLink();
    void OnStreamingDataSource(TypedObject dataSource);
    object? ResolveLink(PointerTypeInfo info, GraphLink link);
}

/// <summary>
/// Port of odradek's AbstractTypeReader + DS2TypeReader: sequential attribute deserialization
/// driven by types.json. Pointers are 1 presence byte in the stream; their target comes from the
/// streaming link table (see IStreamingContext).
/// </summary>
public sealed class RttiReader(TypeFactory factory)
{
    private readonly TypeFactory _factory = factory;
    private readonly Dictionary<long, ClassTypeInfo> _byHash = BuildHashIndex(factory);

    public TypeFactory Factory => _factory;

    public IStreamingContext? Context { get; set; }
    /// <summary>When true, unimplemented cases (bitset, missing callbacks) are skipped instead of throwing.</summary>
    public bool Lenient { get; set; }
    public List<string> Warnings { get; } = [];

    private static Dictionary<long, ClassTypeInfo> BuildHashIndex(TypeFactory factory)
    {
        var map = new Dictionary<long, ClassTypeInfo>();
        foreach (var name in factory.KnownNames)
        {
            var info = factory.Resolve(name);
            if (info is not ClassTypeInfo cls) continue;
            var hash = Hashes.TypeHash(name);
            if (!map.TryAdd(hash, cls))
                throw new InvalidDataException($"Type id collision for {name}");
        }
        return map;
    }

    public ClassTypeInfo TypeById(long hash) => _byHash.TryGetValue(hash, out var t)
        ? t
        : throw new KeyNotFoundException($"Unknown type id: 0x{hash:X16}");

    public bool TryTypeById(long hash, out ClassTypeInfo type) => _byHash.TryGetValue(hash, out type!);

    /// <summary>
    /// File-level object: u64 typeHash | i32 payloadSize | payload | i32 numLinks (must be 0).
    /// Port of DS2TypeReader.readObject (DS2TypeReader.java:23-43).
    /// </summary>
    public TypedObject ReadFileObject(BinaryReader reader)
    {
        var hash = reader.ReadLong();
        var size = reader.ReadInt();
        var type = TypeById(hash);
        var start = reader.Position;
        var obj = ReadCompound(type, reader);
        var end = reader.Position;
        if (end - start != size)
            throw new InvalidDataException($"Size mismatch for {type.Name}: {end - start} (actual) != {size} (expected)");
        var numLinks = reader.ReadInt();
        if (numLinks != 0)
            throw new InvalidDataException($"Expected 0 links, got {numLinks}");
        return obj;
    }

    public TypedObject ReadCompound(ClassTypeInfo type, BinaryReader reader)
    {
        var obj = new TypedObject { Type = type };
        FillCompound(type, reader, obj);
        return obj;
    }

    /// <summary>Port of AbstractTypeReader.fillCompound (AbstractTypeReader.java:41-53).</summary>
    public void FillCompound(ClassTypeInfo type, BinaryReader reader, TypedObject target)
    {
        foreach (var attr in type.OrderedAttrs)
        {
            var value = Read(attr.Type, reader);
            target.Fields[attr.Name] = value;
        }
        TypeCallbacks.Read(this, reader, target);
        if (Context is { ResolveLinks: true } && target.Type.Name == "StreamingDataSource")
            Context.OnStreamingDataSource(target);
    }

    public object? Read(TypeInfo info, BinaryReader reader) => info switch
    {
        AtomTypeInfo t => ReadAtom(t, reader),
        EnumTypeInfo t => ReadEnum(t, reader),
        ClassTypeInfo t => ReadCompound(t, reader),
        ContainerTypeInfo t => ReadContainer(t, reader),
        PointerTypeInfo t => ReadPointer(t, reader),
        BitSetTypeInfo t => ReadBitSet(t, reader),
        _ => throw new NotSupportedException($"Unsupported type: {info.Name}"),
    };

    /// <summary>Port of DS2TypeReader.readerForAtom (DS2TypeReader.java:77-97).</summary>
    public object? ReadAtom(AtomTypeInfo info, BinaryReader reader)
    {
        var name = info.BaseTypeInfo?.Name ?? info.BaseType;
        return name switch
        {
            "bool" => reader.ReadBool(),
            "wchar" or "tchar" => (char)reader.ReadShort(),
            "uint8" or "int8" => reader.ReadSByte(),
            "uint16" or "int16" => reader.ReadShort(),
            "uint" or "int" or "uint32" or "int32" or "ucs4" => reader.ReadInt(),
            "uint64" or "int64" or "uintptr" or "intptr" => reader.ReadLong(),
            "uint128" => new System.UInt128(reader.ReadULong(), reader.ReadULong()),
            "HalfFloat" => reader.ReadHalf(),
            "float" => reader.ReadFloat(),
            "double" => reader.ReadDouble(),
            "StringHash" => ReadStringHash(reader),
            "String" => ReadStringAtom(reader),
            "WString" => reader.ReadString(reader.ReadInt() * 2, System.Text.Encoding.Unicode),
            "MotionMatchingVecN" => ReadFloats(reader, 72),
            _ => throw new NotSupportedException($"Unknown atom type: {info.Name} ({name})"),
        };
    }

    private static float[] ReadFloats(BinaryReader reader, int count)
    {
        var result = new float[count];
        for (var i = 0; i < count; i++) result[i] = reader.ReadFloat();
        return result;
    }

    /// <summary>DS2TypeReader.StringHashReader: i32 size (must be 4) + i32 hash.</summary>
    private static int ReadStringHash(BinaryReader reader)
    {
        var size = reader.ReadInt();
        if (size != 4) throw new InvalidDataException($"Unexpected string hash size: {size}");
        return reader.ReadInt();
    }

    /// <summary>DS2TypeReader.StringReader: i32 length | i32 crc32 | bytes (UTF-8), CRC-32C validated.</summary>
    private static string ReadStringAtom(BinaryReader reader)
    {
        var length = reader.ReadInt();
        if (length == 0) return string.Empty;
        var hash = reader.ReadInt();
        var data = reader.ReadBytes(length);
        var actual = (int)(Hashes.Crc32C(data) & 0x7fffffff);
        if (hash != actual) throw new InvalidDataException("String is corrupted - mismatched checksum");
        return System.Text.Encoding.UTF8.GetString(data);
    }

    /// <summary>Port of DS2TypeReader.readEnum (DS2TypeReader.java:45-58). NOTE: signed reads.</summary>
    public EnumValue ReadEnum(EnumTypeInfo info, BinaryReader reader)
    {
        long value = info.SizeBytes switch
        {
            1 => reader.ReadSByte(),
            2 => reader.ReadShort(),
            4 => reader.ReadInt(),
            _ => throw new NotSupportedException($"Unexpected enum size: {info.SizeBytes}"),
        };
        return new EnumValue(info, value);
    }

    private object? ReadBitSet(BitSetTypeInfo info, BinaryReader reader)
    {
        if (!Lenient)
            throw new NotSupportedException($"Bitset type '{info.Name}' is not implemented by odradek either");
        var bytes = reader.ReadBytes(info.SizeBytes);
        Warnings.Add($"bitset {info.Name} skipped");
        return bytes;
    }

    /// <summary>Port of DS2TypeReader.readContainer (DS2TypeReader.java:60-138).</summary>
    public IReadOnlyList<object?> ReadContainer(ContainerTypeInfo info, BinaryReader reader)
    {
        var count = reader.ReadInt();
        if (count < 0) throw new InvalidDataException($"Negative container count {count} for {info.Name}");
        var result = new List<object?>(count);
        var hashContainer = info.ContainerKind is "HashMap" or "HashSet";
        for (var i = 0; i < count; i++)
        {
            if (hashContainer) reader.Skip(4); // hash
            result.Add(Read(info.ItemType, reader));
        }
        return result;
    }

    /// <summary>Port of StreamingObjectReader.readPointer (StreamingObjectReader.java:146-155).</summary>
    public object? ReadPointer(PointerTypeInfo info, BinaryReader reader)
    {
        if (!reader.ReadBool()) return null;
        if (info.PointerKind == "UUIDRef")
            return new UuidRef(reader.ReadBytes(16));
        if (Context is null)
            throw new InvalidDataException("Unexpected pointer");
        var link = Context.NextLink();
        return Context.ResolveLink(info, link);
    }
}
