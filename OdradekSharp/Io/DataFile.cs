namespace OdradekSharp.Io;

/// <summary>
/// Random-access reader for game data files. Mirrors odradek's
/// StreamingGraphStorage.mount (DirectStorageReader if the file starts with "DSAR", else a plain file).
/// </summary>
public abstract class DataFile : IDisposable
{
    public abstract byte[] Read(long offset, int length);
    public abstract long Size { get; }
    public abstract void Dispose();

    /// <summary>Opens a file, transparently handling the DSAR (DirectStorage + LZ4) container.</summary>
    public static DataFile Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        Span<byte> magic = stackalloc byte[4];
        stream.ReadExactly(magic);
        stream.Position = 0;
        if (magic[0] == (byte)'D' && magic[1] == (byte)'S' && magic[2] == (byte)'A' && magic[3] == (byte)'R')
            return new DirectStorageFile(path, stream);
        return new PlainFile(stream);
    }
}

internal sealed class PlainFile(FileStream stream) : DataFile
{
    public override long Size => stream.Length;

    public override byte[] Read(long offset, int length)
    {
        var buffer = new byte[length];
        lock (stream)
        {
            stream.Position = offset;
            stream.ReadExactly(buffer, 0, length);
        }
        return buffer;
    }

    public override void Dispose() => stream.Dispose();
}

/// <summary>
/// DirectStorage archive ("DSAR") reader — port of odradek's DirectStorageReader +
/// ChunkedBinaryReader (odradek-core/.../io/DirectStorageReader.java, ChunkedBinaryReader.java).
/// </summary>
internal sealed class DirectStorageFile : DataFile
{
    public readonly record struct Chunk(long Offset, long CompressedOffset, int Size, int CompressedSize);

    private readonly FileStream _stream;
    private readonly Chunk[] _chunks;
    private readonly long _totalSize;

    private readonly byte[] _compressed;
    private readonly byte[] _decompressed;
    private Chunk? _cached;

    public DirectStorageFile(string path, FileStream stream)
    {
        _stream = stream;
        var header = new byte[32];
        stream.ReadExactly(header);
        var r = new BinaryReader(header);
        var magic = r.ReadUInt();
        if (magic != 0x52415344) throw new InvalidDataException("Invalid DSAR magic");
        var versionMajor = r.ReadUShort();
        var versionMinor = r.ReadUShort();
        if (versionMajor != 3 && versionMinor != 1)
            throw new InvalidDataException($"Unsupported archive version {versionMajor}.{versionMinor}");
        var chunkCount = r.ReadInt();
        var firstChunkOffset = r.ReadInt();
        _totalSize = r.ReadLong();

        _chunks = new Chunk[chunkCount];
        var table = new byte[chunkCount * 32];
        stream.Position = 32;
        stream.ReadExactly(table);
        var tr = new BinaryReader(table);
        var maxCompressed = 0;
        var maxSize = 0;
        for (var i = 0; i < chunkCount; i++)
        {
            var offset = tr.ReadLong();
            var compressedOffset = tr.ReadLong();
            var size = tr.ReadInt();
            var compressedSize = tr.ReadInt();
            var type = tr.ReadByte();
            tr.Skip(7);
            if (type != 3) throw new InvalidDataException($"Unsupported chunk compression type: {type}");
            _chunks[i] = new Chunk(offset, compressedOffset, size, compressedSize);
            maxCompressed = Math.Max(maxCompressed, compressedSize);
            maxSize = Math.Max(maxSize, size);
        }
        if (_chunks.Length > 0 && firstChunkOffset != 32 + chunkCount * 32)
            Console.Error.WriteLine($"warning: DSAR firstChunkOffset mismatch in {path}");
        _compressed = new byte[maxCompressed];
        _decompressed = new byte[maxSize];
    }

    public override long Size => _totalSize;

    public override byte[] Read(long offset, int length)
    {
        var result = new byte[length];
        var pos = offset;
        var written = 0;
        while (written < length)
        {
            var chunk = FindChunk(pos);
            var chunkOffset = (int)(pos - chunk.Offset);
            var n = Math.Min(chunk.Size - chunkOffset, length - written);
            if (n <= 0) throw new EndOfStreamException();

            if (_cached is not { } c || c.Offset != chunk.Offset)
            {
                lock (_stream)
                {
                    _stream.Position = chunk.CompressedOffset;
                    _stream.ReadExactly(_compressed, 0, chunk.CompressedSize);
                }
                Lz4.DecompressBlock(_compressed.AsSpan(0, chunk.CompressedSize), _decompressed.AsSpan(0, chunk.Size));
                _cached = chunk;
            }

            Array.Copy(_decompressed, chunkOffset, result, written, n);
            pos += n;
            written += n;
        }
        return result;
    }

    private Chunk FindChunk(long position)
    {
        // chunk table is sorted by offset and contiguous; binary search the last chunk with Offset <= position
        var lo = 0;
        var hi = _chunks.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_chunks[mid].Offset <= position) lo = mid; else hi = mid - 1;
        }
        return _chunks[lo];
    }

    public override void Dispose() => _stream.Dispose();
}
