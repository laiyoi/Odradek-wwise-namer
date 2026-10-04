namespace OdradekSharp.Io;

/// <summary>
/// LZ4 block decompressor (raw block format, no frame header), as used by
/// odradek's DirectStorageReader.decompress -> Decompressor.lz4Block().
/// </summary>
public static class Lz4
{
    public static void DecompressBlock(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var s = 0;
        var d = 0;
        while (s < src.Length)
        {
            var token = src[s++];

            // literal length
            var literalLength = token >> 4;
            if (literalLength == 15)
            {
                int b;
                do { b = src[s++]; literalLength += b; } while (b == 255);
            }

            if (literalLength > 0)
            {
                src.Slice(s, literalLength).CopyTo(dst[d..]);
                s += literalLength;
                d += literalLength;
            }

            if (s >= src.Length) break; // last sequence has no match

            var offset = src[s] | (src[s + 1] << 8);
            s += 2;
            if (offset == 0) throw new InvalidDataException("Invalid LZ4 offset 0");

            var matchLength = token & 0x0F;
            if (matchLength == 15)
            {
                int b;
                do { b = src[s++]; matchLength += b; } while (b == 255);
            }
            matchLength += 4;

            var match = d - offset;
            if (match < 0) throw new InvalidDataException("Invalid LZ4 match offset");
            for (var i = 0; i < matchLength; i++) dst[d + i] = dst[match + i];
            d += matchLength;
        }
    }
}
