using BinaryReader = OdradekSharp.Io.BinaryReader;
using OdradekSharp.Io;

namespace OdradekSharp.Rtti;

/// <summary>
/// Port of the 14 callbacks registered by odradek-game-ds2 (module-info.java:52-67).
/// These consume "MsgReadBinary" extra data that lives right after the regular attributes, so the byte
/// counts must match odradek exactly or every following object will be misaligned.
/// </summary>
public static class TypeCallbacks
{
    public static void Read(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        // odradek checks `target instanceof ExtraBinaryDataHolder`, and the generated interface of a
        // derived type extends its base's interface — so a type *inherits* the MsgReadBinary callback of
        // its ancestors. Verified on the real game: ShaderFromFileResource : ShaderResource consumes the
        // extra 45 bytes of ShaderResourceCallback (70-byte objects instead of 25).
        var declaring = FindMsgReadBinary(obj.Type);
        if (declaring is null) return;
        switch (declaring.Name)
        {
            case "DataBufferResource": DataBufferResource(reader, r, obj); break;
            case "DebugMouseCursorPS4": DebugMouseCursorPS4(r, obj); break;
            case "IndexArrayResource": IndexArrayResource(reader, r, obj); break;
            case "LocalizedTextResource": LocalizedTextResource(reader, r, obj); break;
            case "ShaderResource": ShaderResource(reader, r, obj); break;
            case "Texture": Texture(reader, r, obj); break;
            case "TextureList": TextureList(reader, r, obj); break;
            case "UITexture": UITexture(reader, r, obj); break;
            case "UITextureFrames": UITextureFrames(reader, r, obj); break;
            case "VertexArrayResource": VertexArrayResource(reader, r, obj); break;
            case "ZivaRTResource": ZivaRTResource(r, obj); break;
            case "FacialRigSettingWithLODResource":
            case "PhysicsRagdollResource":
            case "PhysicsShapeResource":
                throw new NotSupportedException(
                    $"Callback for '{declaring.Name}' (object {obj.Type.Name}) is not ported " +
                    "(odradek uses the Jolt/RigLogic libraries)");
            default:
                throw new NotSupportedException(
                    $"Missing callback for '{declaring.Name}' (object {obj.Type.Name}) " +
                    $"required to read extra data at position {r.Position}");
        }
    }

    /// <summary>The nearest type in the hierarchy that declares the MsgReadBinary message.</summary>
    public static ClassTypeInfo? FindMsgReadBinary(ClassTypeInfo type)
    {
        if (type.Messages.Contains("MsgReadBinary")) return type;
        foreach (var b in type.Bases)
        {
            var found = FindMsgReadBinary(b.Type);
            if (found is not null) return found;
        }
        return null;
    }

    private static byte[] MurmurHashValue(BinaryReader r) => r.ReadBytes(16);

    private static TypedObject Nested(RttiReader reader, BinaryReader r, string typeName)
    {
        var type = (ClassTypeInfo)reader.Factory.Resolve(typeName);
        // odradek uses a plain DS2TypeReader here: streaming hooks (locator/link resolution) do NOT apply.
        var sub = new RttiReader(reader.Factory) { Lenient = reader.Lenient, Context = null };
        return sub.ReadCompound(type, r);
    }

    private static void DataBufferResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        obj.Fields["Callback.Count"] = count;
        if (count == 0) return;
        var isStreaming = r.ReadInt() != 0;
        var flags = r.ReadInt();
        var format = r.ReadInt();
        var stride = r.ReadInt();
        obj.Fields["Callback.IsStreaming"] = isStreaming;
        obj.Fields["Callback.Flags"] = flags;
        obj.Fields["Callback.Format"] = new EnumValue((EnumTypeInfo)reader.Factory.Resolve("EDataBufferFormat"), format);
        obj.Fields["Callback.Stride"] = stride;
        if (!isStreaming) obj.Fields["Callback.Data"] = r.ReadBytes(stride * count);
    }

    private static void DebugMouseCursorPS4(BinaryReader r, TypedObject obj)
    {
        var stride = r.ReadInt();
        var count = r.ReadInt();
        obj.Fields["Callback.Stride"] = stride;
        obj.Fields["Callback.Data"] = r.ReadBytes(count);
    }

    private static void IndexArrayResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        var flags = r.ReadInt();
        var format = r.ReadInt();
        var isStreaming = r.ReadInt() != 0;
        var checksum = MurmurHashValue(r);
        obj.Fields["Callback.Count"] = count;
        obj.Fields["Callback.Flags"] = flags;
        obj.Fields["Callback.Format"] = new EnumValue((EnumTypeInfo)reader.Factory.Resolve("EIndexFormat"), format);
        obj.Fields["Callback.IsStreaming"] = isStreaming;
        obj.Fields["Callback.Checksum"] = checksum;
        if (!isStreaming)
        {
            // EIndexFormat: Index16 = 0, Index32 = 1 (types.json); EIndexFormatExtension.stride()
            var stride = format switch { 0 => 2, 1 => 4, _ => throw new NotSupportedException($"Unexpected index format {format}") };
            obj.Fields["Callback.Data"] = r.ReadBytes(count * stride);
        }
    }

    private static void LocalizedTextResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var languages = WrittenLanguages(reader);
        var texts = new List<object?>(languages.Count);
        foreach (var _ in languages)
        {
            var text = r.ReadShortString();
            var secondary = r.ReadShortString();
            var mode = r.ReadByte();
            texts.Add((text, secondary, mode));
        }
        obj.Fields["Callback.Texts"] = texts;
    }

    /// <summary>ELanguageExtension.writtenLanguages(): all values except Unknown, ordered by value.</summary>
    private static List<string> WrittenLanguages(RttiReader reader)
    {
        var en = (EnumTypeInfo)reader.Factory.Resolve("ELanguage");
        return en.Values
            .Where(v => !string.Equals(v.Name, "Unknown", StringComparison.Ordinal))
            .OrderBy(v => v.Value)
            .Select(v => v.Name)
            .ToList();
    }

    private static void ShaderResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        obj.Fields["Callback.Size"] = r.ReadInt();
        obj.Fields["Callback.Unk04"] = MurmurHashValue(r);
        obj.Fields["Callback.Unk14"] = MurmurHashValue(r);
        obj.Fields["Callback.Unk24"] = Nested(reader, r, "StreamingDataSource");
    }

    private static void Texture(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var header = new Dictionary<string, object?>
        {
            ["Type"] = r.ReadShort(),
            ["Width"] = r.ReadShort(),
            ["Height"] = r.ReadShort(),
            ["NumSurfaces"] = r.ReadShort(),
            ["NumMips"] = r.ReadByte(),
            ["PixelFormat"] = r.ReadByte(),
            ["Unk0A"] = r.ReadByte(),
            ["ColorSpace"] = r.ReadByte(),
            ["Unk0C"] = r.ReadByte(),
            ["Unk0D"] = r.ReadByte(),
            ["Unk0E"] = r.ReadByte(),
            ["Unk0F"] = r.ReadByte(),
            ["Checksum"] = MurmurHashValue(r),
        };
        obj.Fields["Callback.Header"] = header;

        var totalSize = r.ReadInt();
        var embeddedSize = r.ReadInt();
        var streamedSize = r.ReadInt();
        var streamedMips = r.ReadInt();
        obj.Fields["Callback.TotalSize"] = totalSize;
        obj.Fields["Callback.EmbeddedSize"] = embeddedSize;
        obj.Fields["Callback.StreamedSize"] = streamedSize;
        obj.Fields["Callback.StreamedMips"] = streamedMips;
        // NOTE: the engine writes totalSize - 12 bytes here (TextureCallback.java:52); do not "fix" it.
        obj.Fields["Callback.Data"] = r.ReadBytes(totalSize - 12);
    }

    private static void TextureList(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        var entries = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = r.ReadInt();
            var length = r.ReadInt();
            var texture = Nested(reader, r, "Texture");
            entries.Add((offset, length, texture));
        }
        obj.Fields["Callback.Entries"] = entries;
    }

    private static void UITexture(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var animated = r.ReadBool();
        var smallSize = r.ReadInt();
        var largeSize = r.ReadInt();
        obj.Fields["Callback.Animated"] = animated;
        obj.Fields["Callback.SmallTextureSize"] = smallSize;
        obj.Fields["Callback.LargeTextureSize"] = largeSize;
        if (smallSize > 0)
            obj.Fields["Callback.SmallTexture"] = animated
                ? Nested(reader, r, "UITextureFrames")
                : Nested(reader, r, "Texture");
        if (largeSize > 0)
            obj.Fields["Callback.LargeTexture"] = animated
                ? Nested(reader, r, "UITextureFrames")
                : Nested(reader, r, "Texture");
    }

    private static void UITextureFrames(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var dataLength = r.ReadInt();
        obj.Fields["Callback.Data"] = r.ReadBytes(dataLength);
        var spanCount = r.ReadInt();
        var spans = new long[spanCount];
        for (var i = 0; i < spanCount; i++) spans[i] = r.ReadLong();
        obj.Fields["Callback.Spans"] = spans;
        obj.Fields["Callback.Width"] = r.ReadInt();
        obj.Fields["Callback.Height"] = r.ReadInt();
        obj.Fields["Callback.PixelFormat"] = r.ReadInt();
        obj.Fields["Callback.Frequency"] = (byte)r.ReadInt();
        obj.Fields["Callback.Size"] = r.ReadInt();
        obj.Fields["Callback.Scale"] = Nested(reader, r, "FSize");
    }

    private static void VertexArrayResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var numVertices = r.ReadInt();
        var numStreams = r.ReadInt();
        var isStreaming = r.ReadBool();
        var streams = new List<object?>(numStreams);
        for (var i = 0; i < numStreams; i++)
        {
            var flags = r.ReadInt();
            var stride = r.ReadInt();
            var elements = r.ReadInt();
            var layout = new byte[elements * 4];
            for (var e = 0; e < elements; e++)
            {
                var b = r.ReadBytes(4);
                Array.Copy(b, 0, layout, e * 4, 4);
            }
            var checksum = MurmurHashValue(r);
            byte[]? data = null;
            if (!isStreaming) data = r.ReadBytes(stride * numVertices);
            streams.Add((flags, stride, elements, layout, checksum, data));
        }
        obj.Fields["Callback.NumVertices"] = numVertices;
        obj.Fields["Callback.NumStreams"] = numStreams;
        obj.Fields["Callback.IsStreaming"] = isStreaming;
        obj.Fields["Callback.Streams"] = streams;
    }

    private static void ZivaRTResource(BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        obj.Fields["Callback.Data"] = r.ReadBytes(count);
    }
}
