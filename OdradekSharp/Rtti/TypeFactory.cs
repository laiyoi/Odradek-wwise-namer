using System.Text.Json;

namespace OdradekSharp.Rtti;

public static class TypeFlags
{
    /// <summary>ERTTIAttrFlags.ATTR_DONT_SERIALIZE_BINARY — see ClassAttrInfo.java:7.</summary>
    public const int DontSerializeBinary = 2;
}

public sealed class ClassAttrInfo
{
    public required string Name { get; init; }
    public required TypeInfo Type { get; init; }
    public int Offset { get; init; }
    public int Flags { get; init; }
    public string? Group { get; init; }
    public string? Min { get; init; }
    public string? Max { get; init; }
    public string? Comment { get; init; }
    /// <summary>Explicit "property": true in types.json (ClassAttrInfo.isProperty()).</summary>
    public bool IsProperty { get; init; }
    public bool IsSerialized => (Flags & TypeFlags.DontSerializeBinary) == 0;
    public override string ToString() =>
        $"{(IsSerialized ? " " : "*")}{Type.Name} {Name} @{Offset} (flags 0x{Flags:X}{(IsProperty ? ", property" : "")})";
}

public sealed class ClassBaseInfo
{
    public required ClassTypeInfo Type { get; init; }
    public int Offset { get; init; }
}

public abstract class TypeInfo
{
    public required string Name { get; init; }
    public abstract string Kind { get; }
    public override string ToString() => $"{Name} ({Kind})";
}

public sealed class AtomTypeInfo : TypeInfo
{
    /// <summary>Name of the underlying base atom ("String", "uint32", "StringHash", ...).</summary>
    public required string BaseType { get; init; }
    /// <summary>Underlying base atom type (odradek resolves base_type through the type factory).</summary>
    public required TypeInfo BaseTypeInfo { get; init; }
    public override string Kind => "atom";
}

public sealed class ClassTypeInfo : TypeInfo
{
    public int Version { get; init; }
    public int Flags { get; init; }
    public int? ExplicitSize { get; init; }
    public List<ClassBaseInfo> Bases { get; } = [];
    public List<ClassAttrInfo> Attrs { get; } = [];
    public List<string> Messages { get; } = [];
    /// <summary>Binary read order: port of DS2TypeFactory.orderedAttrs().</summary>
    public List<ClassAttrInfo> OrderedAttrs { get; internal set; } = [];
    /// <summary>JSON/display order: port of ClassTypeInfo.serializedAttrs() (bases first, extension bases last of the bases).</summary>
    public List<ClassAttrInfo> SerializedAttrs { get; internal set; } = [];
    public override string Kind => "compound";

    /// <summary>Size in bytes when the layout is fully known from attrs.</summary>
    public int Size
    {
        get
        {
            if (ExplicitSize is int s && s > 0) return s;
            var max = 0;
            foreach (var b in Bases) max = Math.Max(max, b.Offset + b.Type.Size());
            foreach (var a in Attrs) max = Math.Max(max, a.Offset + a.Type.Size());
            return max;
        }
    }
}

public sealed class ContainerTypeInfo : TypeInfo
{
    public required string ContainerKind { get; init; }
    public required TypeInfo ItemType { get; init; }
    public override string Kind => "container";
}

public sealed class EnumTypeInfo : TypeInfo
{
    public required string BaseType { get; init; }
    public int SizeBytes { get; init; } = 4;
    public List<(string Name, long Value)> Values { get; } = [];
    public override string Kind => "enum";
}

/// <summary>kind "enum bitset": fixed-size bitset backed by an integer atom (types.json "type" + "size").</summary>
public sealed class BitSetTypeInfo : TypeInfo
{
    public required TypeInfo BaseType { get; init; }
    public int SizeBytes { get; init; }
    public override string Kind => "bitset";
}

public sealed class PointerTypeInfo : TypeInfo
{
    /// <summary>Pointer flavour: Ref / UUIDRef / cptr / StreamingRef / WeakPtr (types.json "type").</summary>
    public required string PointerKind { get; init; }
    /// <summary>Pointed-to type (types.json "item_type").</summary>
    public required TypeInfo TargetType { get; init; }
    public override string Kind => "pointer";
}

public static class TypeInfoExtensions
{
    /// <summary>Fixed serialized size in bytes, or 0 when not fixed (containers, pointers, strings...).</summary>
    public static int Size(this TypeInfo info) => info switch
    {
        ClassTypeInfo c => c.Size,
        AtomTypeInfo a => a.BaseType switch
        {
            "bool" or "int8" or "uint8" or "tchar" => 1,
            "int16" or "uint16" or "HalfFloat" => 2,
            "int" or "int32" or "uint" or "uint32" or "float" => 4,
            "int64" or "uint64" or "double" or "MusicTime" => 8,
            "StringHash" => 8,
            "uint128" => 16,
            "GGUUID" => 16,
            _ => 0,
        },
        EnumTypeInfo e => e.BaseType switch
        {
            "int8" or "uint8" or "bool" => 1,
            "int16" or "uint16" => 2,
            "int64" or "uint64" => 8,
            _ => 4,
        },
        _ => 0,
    };
}

/// <summary>
/// Loads odradek's types.json (+ extensions.json) and exposes TypeInfo graph.
/// Resolution is lazy so that mutually recursive or out-of-order definitions work.
/// </summary>
public sealed class TypeFactory : IDisposable
{
    private readonly JsonDocument _doc;
    private readonly JsonDocument? _ext;
    private readonly Dictionary<string, JsonElement> _raw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeInfo> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _extends = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TypeInfo> Types => _types;
    public IReadOnlyList<string> KnownNames { get; }

    private TypeFactory(JsonDocument doc, JsonDocument? ext)
    {
        _doc = doc;
        _ext = ext;
        foreach (var p in doc.RootElement.EnumerateObject()) _raw[p.Name] = p.Value;
        if (ext is not null)
        {
            var root = ext.RootElement;
            if (root.TryGetProperty("types", out var types))
                foreach (var p in types.EnumerateObject()) _raw[p.Name] = p.Value;
            if (root.TryGetProperty("extends", out var extends))
                foreach (var p in extends.EnumerateObject())
                    _extends[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        KnownNames = _raw.Keys.ToList();
    }

    public static TypeFactory Load(string typesJsonPath, string? extensionsJsonPath = null)
    {
        var doc = JsonDocument.Parse(File.ReadAllBytes(typesJsonPath),
            new JsonDocumentOptions { AllowTrailingCommas = true });
        JsonDocument? ext = null;
        if (extensionsJsonPath is not null && File.Exists(extensionsJsonPath))
            ext = JsonDocument.Parse(File.ReadAllBytes(extensionsJsonPath),
                new JsonDocumentOptions { AllowTrailingCommas = true });
        return new TypeFactory(doc, ext);
    }

    public TypeInfo this[string name] => Resolve(name);

    public bool TryGet(string name, out TypeInfo type)
    {
        try { type = Resolve(name); return true; }
        catch (KeyNotFoundException) { type = null!; return false; }
    }

    public TypeInfo? Find(string name) => _raw.ContainsKey(name) ? Resolve(name) : null;

    public TypeInfo Resolve(string name)
    {
        if (_types.TryGetValue(name, out var cached)) return cached;
        if (!_raw.TryGetValue(name, out var e))
            throw new KeyNotFoundException($"Unknown type: {name}");

        var info = ResolveEntry(name, e);
        _types[name] = info;
        if (info is ClassTypeInfo cls)
        {
            cls.OrderedAttrs = OrderAttrs(cls);
            cls.SerializedAttrs = SerializedAttrsOf(cls);
        }
        return info;
    }

    private TypeInfo ResolveEntry(string name, JsonElement e)
    {
        var kind = e.TryGetProperty("kind", out var k) ? k.GetString()! : throw new InvalidDataException($"Type {name} has no kind");
        switch (kind)
        {
            case "atom":
            {
                var baseName = e.TryGetProperty("base_type", out var bt) ? bt.GetString()! : name;
                return new AtomTypeInfo
                {
                    Name = name,
                    BaseType = baseName,
                    BaseTypeInfo = baseName == name ? null! : Resolve(baseName),
                };
            }

            case "compound":
            {
                var c = new ClassTypeInfo
                {
                    Name = name,
                    Version = e.TryGetProperty("version", out var v) ? v.GetInt32() : 0,
                    Flags = e.TryGetProperty("flags", out var f) ? f.GetInt32() : 0,
                    ExplicitSize = e.TryGetProperty("size", out var s) ? s.GetInt32() : null,
                };
                _types[name] = c; // register before resolving children (recursion)
                if (e.TryGetProperty("bases", out var bases))
                {
                    foreach (var b in bases.EnumerateArray())
                    {
                        var baseType = Resolve(b.GetProperty("type").GetString()!);
                        c.Bases.Add(new ClassBaseInfo
                        {
                            Type = (ClassTypeInfo)baseType,
                            Offset = b.GetProperty("offset").GetInt32(),
                        });
                    }
                }
                if (e.TryGetProperty("messages", out var msgs))
                    foreach (var m in msgs.EnumerateArray()) c.Messages.Add(m.GetString()!);
                if (e.TryGetProperty("attrs", out var attrs))
                {
                    string? category = null;
                    foreach (var a in attrs.EnumerateArray())
                    {
                        if (a.TryGetProperty("category", out var cat))
                        {
                            category = cat.GetString();
                            continue;
                        }
                        c.Attrs.Add(new ClassAttrInfo
                        {
                            Name = a.GetProperty("name").GetString()!,
                            Type = Resolve(a.GetProperty("type").GetString()!),
                            Offset = a.GetProperty("offset").GetInt32(),
                            Flags = a.TryGetProperty("flags", out var af) ? af.GetInt32() : 0,
                            Group = category,
                            Min = a.TryGetProperty("min", out var mn) ? mn.GetString() : null,
                            Max = a.TryGetProperty("max", out var mx) ? mx.GetString() : null,
                            Comment = a.TryGetProperty("comment", out var cm) ? cm.GetString() : null,
                            IsProperty = a.TryGetProperty("property", out var pr) && pr.GetBoolean(),
                        });
                    }
                }
                // Extension types are appended to bases() with offset = -1 (TypeContext.java:158-166).
                if (_extends.TryGetValue(name, out var extensionNames))
                {
                    foreach (var extName in extensionNames)
                        c.Bases.Add(new ClassBaseInfo { Type = (ClassTypeInfo)Resolve(extName), Offset = -1 });
                }
                return c;
            }

            case "container":
                return new ContainerTypeInfo
                {
                    Name = name,
                    ContainerKind = e.TryGetProperty("type", out var ct) ? ct.GetString()! : "Array",
                    ItemType = Resolve(e.GetProperty("item_type").GetString()!),
                };

            case "pointer":
                return new PointerTypeInfo
                {
                    Name = name,
                    PointerKind = e.TryGetProperty("type", out var pt) ? pt.GetString()! : "Ref",
                    TargetType = Resolve(e.GetProperty("item_type").GetString()!),
                };

            case "enum":
            case "enum flags":
            {
                var en = new EnumTypeInfo
                {
                    Name = name,
                    BaseType = "int",
                    SizeBytes = e.TryGetProperty("size", out var es) ? es.GetInt32() : 4,
                };
                if (e.TryGetProperty("values", out var vals))
                    foreach (var val in vals.EnumerateArray())
                        en.Values.Add((
                            val.TryGetProperty("name", out var n2) ? n2.GetString()! : "?",
                            val.TryGetProperty("value", out var v2) ? v2.GetInt64() : 0L));
                return en;
            }

            case "enum bitset":
                return new BitSetTypeInfo
                {
                    Name = name,
                    SizeBytes = e.TryGetProperty("size", out var bs) ? bs.GetInt32() : 0,
                    BaseType = Resolve(e.GetProperty("type").GetString()!),
                };

            default:
                throw new NotSupportedException($"Unknown kind '{kind}' for type '{name}'");
        }
    }

    /// <summary>Extension types attached to a compound via extensions.json ("extends").</summary>
    public IReadOnlyList<ClassTypeInfo> ExtensionsOf(string typeName)
    {
        if (!_extends.TryGetValue(typeName, out var list)) return [];
        return list.Select(n => (ClassTypeInfo)Resolve(n)).ToList();
    }

    private Dictionary<long, string>? _nameByHash;

    /// <summary>
    /// type id -> type name. Type ids are murmur3_x64_128(seed 42)("00000001_" + name), taking h1
    /// (DS2TypeFactory.computeTypeId, DS2TypeFactory.java:19-23) — verified against a real
    /// streaming_graph.core, whose resource header hash equals TypeHash("StreamingGraphResource").
    /// </summary>
    public IReadOnlyDictionary<long, string> NameByHash
    {
        get
        {
            EnsureHashIndex();
            return _nameByHash!;
        }
    }

    public void EnsureHashIndex()
    {
        if (_nameByHash is not null) return;
        var map = new Dictionary<long, string>();
        foreach (var name in KnownNames)
        {
            var hash = Io.Hashes.TypeHash(name);
            if (!map.TryAdd(hash, name))
                throw new InvalidDataException($"Type id collision between {map[hash]} and {name}");
        }
        _nameByHash = map;
    }

    /// <summary>
    /// Port of DS2TypeFactory.sortOrderedAttributes + filterOrderedAttributes
    /// (DS2TypeFactory.java:25-89). The Java side uses its own quicksort whose pivot comes from a
    /// fixed LCG (state = 0x19660D * state + 0x3C6EF35F) precisely because the pivot choice affects the
    /// ordering of equal elements. The same algorithm is ported here verbatim.
    /// </summary>
    private static List<ClassAttrInfo> OrderAttrs(ClassTypeInfo type)
    {
        // NOTE: AbstractTypeFactory.collectOrderedAttrs accumulates the offsets along the base chain
        // (offset + base.offset()), and the comparator sorts by that *absolute* offset. Sorting by the
        // raw attr offset is wrong whenever a base sits at a non-zero offset (e.g. CurveResource :
        // Resource@0, CurveData@32).
        var all = new List<(ClassAttrInfo Attr, int AbsOffset)>();
        Collect(type, 0, all);
        QuickSort(all, Compare);
        return all.Where(a => a.Attr.IsSerialized).Select(a => a.Attr).ToList();

        static void Collect(ClassTypeInfo t, int offset, List<(ClassAttrInfo, int)> into)
        {
            foreach (var b in t.Bases)
            {
                if (b.Offset < 0) continue; // extension type: does not take part in the binary layout
                Collect(b.Type, offset + b.Offset, into);
            }
            foreach (var a in t.Attrs) into.Add((a, offset + a.Offset));
        }

        static int Compare((ClassAttrInfo Attr, int AbsOffset) o1, (ClassAttrInfo Attr, int AbsOffset) o2)
        {
            if (o1.Attr.IsProperty)
            {
                if (!o2.Attr.IsProperty) return 1;
                return string.CompareOrdinal(o1.Attr.Name, o2.Attr.Name);
            }
            if (o2.Attr.IsProperty) return -1;
            return o1.AbsOffset.CompareTo(o2.AbsOffset);
        }
    }

    /// <summary>Port of DS2TypeFactory's LCG-pivot quicksort (DS2TypeFactory.java:47-89).</summary>
    private static void QuickSort<T>(List<T> items, Comparison<T> comparator) =>
        QuickSort(items, comparator, 0, items.Count - 1, 0);

    private static uint QuickSort<T>(List<T> items, Comparison<T> comparator, int left, int right, uint state)
    {
        if (left >= right) return state;
        state = unchecked(0x19660D * state + 0x3C6EF35F);
        var pivot = (int)((state >>> 8) % (uint)(right - left));
        (items[left + pivot], items[right]) = (items[right], items[left + pivot]);
        var start = Partition(items, comparator, left, right);
        state = QuickSort(items, comparator, left, start - 1, state);
        state = QuickSort(items, comparator, start + 1, right, state);
        return state;
    }

    private static int Partition<T>(List<T> items, Comparison<T> comparator, int left, int right)
    {
        var start = left - 1;
        var end = right;
        while (true)
        {
            do { start++; } while (start < end && comparator(items[start], items[right]) < 0);
            do { end--; } while (end > start && comparator(items[right], items[end]) < 0);
            if (start >= end) break;
            (items[start], items[end]) = (items[end], items[start]);
        }
        (items[start], items[right]) = (items[right], items[start]);
        return start;
    }

    /// <summary>
    /// Port of ClassTypeInfo.serializedAttrs() (ClassTypeInfo.java:42-54): bases recursively first
    /// (extension bases are appended to bases() and thus included), then this class' own serialized attrs
    /// in declaration order. This is the order odradek's JSON exporter prints.
    /// </summary>
    private static List<ClassAttrInfo> SerializedAttrsOf(ClassTypeInfo type)
    {
        var attrs = new List<ClassAttrInfo>();
        foreach (var b in type.Bases) attrs.AddRange(SerializedAttrsOf(b.Type));
        foreach (var a in type.Attrs) if (a.IsSerialized) attrs.Add(a);
        return attrs;
    }

    public void Dispose()
    {
        _doc.Dispose();
        _ext?.Dispose();
    }
}
