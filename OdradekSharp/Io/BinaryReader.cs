using System.Buffers.Binary;

namespace OdradekSharp.Io;

/// <summary>
/// Little-endian binary reader over a byte buffer. Port of odradek's BinaryReader / BytesBinaryReader
/// (see odradek-core/.../io/BinaryReader.java). No alignment, no padding.
/// </summary>
public sealed class BinaryReader
{
    private readonly ReadOnlyMemory<byte> _memory;
    public int Position { get; set; }
    public int Length => _memory.Length;
    public int Remaining => _memory.Length - Position;

    public BinaryReader(ReadOnlyMemory<byte> memory) => _memory = memory;

    public static BinaryReader Wrap(ReadOnlyMemory<byte> memory) => new(memory);

    private ReadOnlySpan<byte> Span(int count)
    {
        if (count < 0) throw new InvalidDataException($"Negative read length: {count}");
        if (Position + count > _memory.Length)
            throw new EndOfStreamException($"Read of {count} bytes at {Position} exceeds buffer of {_memory.Length}");
        var span = _memory.Span.Slice(Position, count);
        Position += count;
        return span;
    }

    public byte ReadByte() => Span(1)[0];
    public sbyte ReadSByte() => unchecked((sbyte)Span(1)[0]);
    public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(Span(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Span(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Span(4));
    public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Span(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Span(8));
    public ulong ReadULong() => BinaryPrimitives.ReadUInt64LittleEndian(Span(8));
    public Half ReadHalf() => BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(Span(2)));
    public float ReadFloat() => BitConverter.Int32BitsToSingle(ReadInt());
    public double ReadDouble() => BitConverter.Int64BitsToDouble(ReadLong());
    public byte[] ReadBytes(int count) => Span(count).ToArray();
    public void Skip(int count) => Span(count);
    public ReadOnlySpan<byte> ReadSpan(int count) => Span(count);

    /// <summary>odsradek readBool(BoolFormat.BYTE): only 0 and 1 are legal (BinaryReader.java:145-156).</summary>
    public bool ReadBool()
    {
        var value = Span(1)[0];
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException($"Unexpected value for bool: {value}"),
        };
    }

    /// <summary>int32 length prefix + UTF-8 bytes (odradek-BinaryReader.readString with INT_LENGTH).</summary>
    public string ReadString()
    {
        var length = ReadInt();
        if (length == 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(Span(length));
    }

    /// <summary>u16 length prefix + UTF-8 bytes (StringFormat.SHORT_LENGTH), used by callbacks.</summary>
    public string ReadShortString()
    {
        var length = ReadUShort();
        if (length == 0) return string.Empty;
        return System.Text.Encoding.UTF8.GetString(Span(length));
    }

    public string ReadString(int byteCount, System.Text.Encoding encoding)
    {
        if (byteCount == 0) return string.Empty;
        return encoding.GetString(Span(byteCount));
    }
}

public static class Hashes
{
    /// <summary>
    /// MurmurHash3 x64 128-bit (seed configurable). odradek uses
    /// wtf.reversed.toolbox HashFunction.murmur3(42); HashCode.asLong() returns the first 8 bytes (h1)
    /// in little-endian order, which is what DS2TypeFactory.computeTypeId() and the DS2 type table use.
    /// </summary>
    public static (ulong H1, ulong H2) Murmur3X64_128(ReadOnlySpan<byte> data, ulong seed)
    {
        const ulong c1 = 0x87c37b91114253d5UL;
        const ulong c2 = 0x4cf5ad432745937fUL;

        var h1 = seed;
        var h2 = seed;

        var length = data.Length;
        var nblocks = length / 16;

        for (var i = 0; i < nblocks; i++)
        {
            var k1 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 16, 8));
            var k2 = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(i * 16 + 8, 8));

            k1 *= c1; k1 = Rotl64(k1, 31); k1 *= c2; h1 ^= k1;
            h1 = Rotl64(h1, 27); h1 += h2; h1 = h1 * 5 + 0x52dce729UL;
            k2 *= c2; k2 = Rotl64(k2, 33); k2 *= c1; h2 ^= k2;
            h2 = Rotl64(h2, 31); h2 += h1; h2 = h2 * 5 + 0x38495ab5UL;
        }

        var tail = data[(nblocks * 16)..];
        ulong t1 = 0, t2 = 0;
        for (var i = tail.Length - 1; i >= 8; i--) t2 = (t2 << 8) | tail[i];
        for (var i = Math.Min(tail.Length, 8) - 1; i >= 0; i--) t1 = (t1 << 8) | tail[i];

        if (tail.Length > 8)
        {
            t2 *= c2; t2 = Rotl64(t2, 33); t2 *= c1; h2 ^= t2;
        }
        if (tail.Length > 0)
        {
            t1 *= c1; t1 = Rotl64(t1, 31); t1 *= c2; h1 ^= t1;
        }

        h1 ^= (ulong)length; h2 ^= (ulong)length;
        h1 += h2; h2 += h1;
        h1 = Fmix64(h1); h2 = Fmix64(h2);
        h1 += h2; h2 += h1;
        return (h1, h2);
    }

    private static ulong Rotl64(ulong x, int r) => (x << r) | (x >> (64 - r));

    private static ulong Fmix64(ulong k)
    {
        k ^= k >> 33;
        k *= 0xff51afd7ed558ccdUL;
        k ^= k >> 33;
        k *= 0xc4ceb9fe1a85ec53UL;
        k ^= k >> 33;
        return k;
    }

    /// <summary>Type id for a DS2 type name: murmur3(seed 42)("00000001_" + name).asLong() (DS2TypeFactory.java:19-23).</summary>
    public static long TypeHash(string typeName)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("00000001_" + typeName);
        var (h1, _) = Murmur3X64_128(bytes, 42);
        return unchecked((long)h1);
    }

    /// <summary>
    /// CRC-32C (Castagnoli, reflected) with init 0 and xorout 0 — DecimaHash.crc32()
    /// (DecimaHash.java:7). Used to validate String atoms.
    /// </summary>
    public static uint Crc32C(ReadOnlySpan<byte> data)
    {
        const uint poly = 0x82F63B78;
        uint crc = 0;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (poly & (uint)-(int)(crc & 1));
        }
        return crc;
    }
}
