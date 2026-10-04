namespace OdradekSharp.Rtti;

/// <summary>(group id, object index) — port of odradek's ObjectId (ObjectId.java).</summary>
public readonly record struct ObjectId(int GroupId, int ObjectIndex)
{
    public override string ToString() => $"{GroupId}:{ObjectIndex}";
    public static ObjectId Parse(string s)
    {
        var parts = s.Split(':');
        return new ObjectId(int.Parse(parts[0]), int.Parse(parts[1]));
    }
}

/// <summary>
/// Dynamic typed object: a type plus its deserialized fields, keyed by attribute name.
/// odradek generates Java classes per type (DS2.&lt;Name&gt;); the C# port keeps the RTTI dynamic
/// so that no code generation is required.
/// </summary>
public sealed class TypedObject
{
    public required ClassTypeInfo Type { get; init; }
    public Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    public ObjectId? Id { get; set; }

    public object? this[string name] => Fields.TryGetValue(name, out var v) ? v : null;

    public T? Get<T>(string name) => Fields.TryGetValue(name, out var v) && v is T t ? t : default;

    public IReadOnlyList<object?> GetList(string name) =>
        Fields.TryGetValue(name, out var v) && v is IReadOnlyList<object?> list ? list : [];

    public override string ToString() => $"{Type.Name} {{ {Fields.Count} fields }}";
}

/// <summary>Enum value with its declared type (name lookup is lazy).</summary>
public readonly record struct EnumValue(EnumTypeInfo Type, long Value)
{
    public string Name
    {
        get
        {
            foreach (var (name, value) in Type.Values)
                if (value == Value) return name;
            return Value.ToString();
        }
    }
    public override string ToString() => $"{Type.Name}.{Name}";
}

/// <summary>A resolved object reference (Ref / cptr / WeakPtr / StreamingRef).</summary>
public sealed record ObjectRef(string PointerKind, ObjectId Id, TypedObject? Target)
{
    public override string ToString() => $"{PointerKind}({Id})";
}

/// <summary>A UUIDRef payload (16 raw bytes, GGUUID layout).</summary>
public sealed record UuidRef(byte[] Data)
{
    public Guid ToGuid()
    {
        // GGUUIDExtension.toDisplayString(): data3..data0 reversed byte order, then the rest as-is.
        Span<byte> b = stackalloc byte[16];
        Data.CopyTo(b);
        return new Guid(
            BitConverter.ToUInt32(Data, 0), BitConverter.ToUInt16(Data, 4), BitConverter.ToUInt16(Data, 6),
            Data[8], Data[9], Data[10], Data[11], Data[12], Data[13], Data[14], Data[15]);
    }
    public override string ToString() => Convert.ToHexString(Data);
}
