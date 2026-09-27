using System.Buffers.Binary;
using System.IO.Compression;

namespace UavOps.Simulator.Map;

/// <summary>
/// Reads tiles out of a PMTiles v3 archive - the same <c>wwwroot/map/israel.pmtiles</c> the page
/// draws - so the simulated camera can draw the ground from the map data. Only what that file
/// uses: gzip (or no) compression, root and leaf directories. Thread-safe.
/// </summary>
public sealed class PmTilesReader : IDisposable
{
    private const int HeaderLength = 127;

    private readonly FileStream _file;
    private readonly object _lock = new();
    private readonly Dictionary<(ulong Offset, ulong Length), List<Entry>> _leafCache = new();
    private readonly List<Entry> _root;
    private readonly ulong _leafDirsOffset;
    private readonly ulong _tileDataOffset;
    private readonly byte _internalCompression;
    private readonly byte _tileCompression;

    public PmTilesReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = Read(0, HeaderLength);
        if (header.Length < HeaderLength || System.Text.Encoding.ASCII.GetString(header, 0, 7) != "PMTiles" || header[7] != 3)
            throw new InvalidDataException($"{path} is not a PMTiles v3 archive.");

        var rootOffset = U64(header, 8);
        var rootLength = U64(header, 16);
        _leafDirsOffset = U64(header, 40);
        _tileDataOffset = U64(header, 56);
        _internalCompression = header[97];
        _tileCompression = header[98];
        MinZoom = header[100];
        MaxZoom = header[101];
        _root = ParseDirectory(Decompress(Read(rootOffset, (int)rootLength), _internalCompression));
    }

    public int MinZoom { get; }
    public int MaxZoom { get; }

    /// <summary>The tile's decompressed bytes (an MVT for this archive), or null if there is none.</summary>
    public byte[]? GetTile(int z, int x, int y)
    {
        var id = TileId(z, x, y);
        var directory = _root;
        for (var depth = 0; depth < 4; depth++)
        {
            var entry = Find(directory, id);
            if (entry is null)
                return null;
            if (entry.Value.RunLength > 0)
            {
                var data = Read(_tileDataOffset + entry.Value.Offset, (int)entry.Value.Length);
                return Decompress(data, _tileCompression);
            }
            directory = Leaf(entry.Value.Offset, entry.Value.Length);
        }
        return null;
    }

    public void Dispose() => _file.Dispose();

    private List<Entry> Leaf(ulong offset, ulong length)
    {
        lock (_lock)
        {
            if (_leafCache.TryGetValue((offset, length), out var cached))
                return cached;
        }
        var leaf = ParseDirectory(Decompress(Read(_leafDirsOffset + offset, (int)length), _internalCompression));
        lock (_lock)
        {
            if (_leafCache.Count > 256)
                _leafCache.Clear();
            _leafCache[(offset, length)] = leaf;
        }
        return leaf;
    }

    /// <summary>The entry covering <paramref name="id"/>: the last one starting at or before it,
    /// if it's a leaf pointer (run length 0) or its run reaches <paramref name="id"/>.</summary>
    private static Entry? Find(List<Entry> entries, ulong id)
    {
        int lo = 0, hi = entries.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (entries[mid].TileId <= id)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (found < 0)
            return null;
        var e = entries[found];
        if (e.RunLength == 0 || id < e.TileId + e.RunLength)
            return e;
        return null;
    }

    private static List<Entry> ParseDirectory(byte[] data)
    {
        var pos = 0;
        var count = (int)Varint(data, ref pos);
        var ids = new ulong[count];
        var runs = new ulong[count];
        var lengths = new ulong[count];
        var offsets = new ulong[count];

        ulong last = 0;
        for (var i = 0; i < count; i++)
            ids[i] = last += Varint(data, ref pos);
        for (var i = 0; i < count; i++)
            runs[i] = Varint(data, ref pos);
        for (var i = 0; i < count; i++)
            lengths[i] = Varint(data, ref pos);
        for (var i = 0; i < count; i++)
        {
            var v = Varint(data, ref pos);
            // 0 means "right after the previous entry's data".
            offsets[i] = v == 0 && i > 0 ? offsets[i - 1] + lengths[i - 1] : v - 1;
        }

        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
            entries.Add(new Entry(ids[i], offsets[i], lengths[i], runs[i]));
        return entries;
    }

    /// <summary>PMTiles tile id: all tiles of lower zooms, then the Hilbert index within zoom z.</summary>
    public static ulong TileId(int z, int x, int y)
    {
        ulong acc = 0;
        for (var t = 0; t < z; t++)
            acc += 1UL << (2 * t);

        long n = 1L << z, tx = x, ty = y;
        ulong d = 0;
        for (var s = n / 2; s > 0; s /= 2)
        {
            long rx = (tx & s) > 0 ? 1 : 0;
            long ry = (ty & s) > 0 ? 1 : 0;
            d += (ulong)(s * s * ((3 * rx) ^ ry));
            if (ry == 0)
            {
                if (rx == 1)
                {
                    tx = n - 1 - tx;
                    ty = n - 1 - ty;
                }
                (tx, ty) = (ty, tx);
            }
        }
        return acc + d;
    }

    private byte[] Read(ulong offset, int length)
    {
        var buffer = new byte[length];
        lock (_lock)
        {
            _file.Seek((long)offset, SeekOrigin.Begin);
            _file.ReadExactly(buffer);
        }
        return buffer;
    }

    private static byte[] Decompress(byte[] data, byte compression)
    {
        // 1 = none, 2 = gzip (0 = unknown: sniff the gzip magic).
        if (compression == 1 || (compression == 0 && !(data.Length > 2 && data[0] == 0x1f && data[1] == 0x8b)))
            return data;
        if (compression is not (0 or 2))
            throw new NotSupportedException($"PMTiles compression {compression} isn't supported (only gzip).");
        using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static ulong U64(byte[] b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset));

    private static ulong Varint(byte[] data, ref int pos)
    {
        ulong result = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = data[pos++];
            result |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
                return result;
        }
    }

    private readonly record struct Entry(ulong TileId, ulong Offset, ulong Length, ulong RunLength);
}
