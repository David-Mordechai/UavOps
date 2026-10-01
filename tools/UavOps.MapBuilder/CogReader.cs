using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace UavOps.MapBuilder;

/// <summary>
/// Reads a Cloud-Optimized GeoTIFF over HTTP, one internal tile at a time (range requests), with
/// every fetched byte range cached on disk so a rerun downloads nothing. Only what the open sources
/// used here need: classic little-endian TIFF, tiled, DEFLATE, horizontal (2) or floating-point (3)
/// predictor, 8-bit samples or 32-bit floats, full resolution (IFD 0) only.
/// </summary>
public sealed class CogReader
{
    private const int HeaderBytes = 1 << 18;

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _cacheDir;
    private readonly byte[] _header;
    private readonly long[] _offsets;
    private readonly long[] _counts;
    private readonly object _lock = new();
    private readonly Dictionary<int, byte[]> _decoded = [];
    private readonly LinkedList<int> _recent = new();

    public int Width { get; }
    public int Height { get; }
    public int TileWidth { get; }
    public int TileHeight { get; }
    public int Samples { get; }
    public int BitsPerSample { get; }
    public bool IsFloat { get; }
    public int Predictor { get; }
    /// <summary>Georeferencing: the top-left corner and the pixel size, in the file's own CRS.</summary>
    public double OriginX { get; }
    public double OriginY { get; }
    public double PixelX { get; }
    public double PixelY { get; }

    public int TilesAcross => (Width + TileWidth - 1) / TileWidth;
    public int TilesDown => (Height + TileHeight - 1) / TileHeight;

    public CogReader(HttpClient http, string url, string cacheRoot)
    {
        _http = http;
        _url = url;
        _cacheDir = Path.Combine(cacheRoot, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..16]);
        Directory.CreateDirectory(_cacheDir);
        _header = Fetch(0, HeaderBytes, "header");

        if (_header[0] != 'I' || _header[1] != 'I' || BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(2)) != 42)
            throw new NotSupportedException($"{url}: only classic little-endian TIFF is supported.");
        var ifd = (int)BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
        var tags = ReadIfd(ifd);

        Width = (int)Scalar(tags, 256);
        Height = (int)Scalar(tags, 257);
        TileWidth = (int)Scalar(tags, 322);
        TileHeight = (int)Scalar(tags, 323);
        Samples = (int)Scalar(tags, 277, 1);
        BitsPerSample = (int)Array(tags, 258)[0];
        IsFloat = Scalar(tags, 339, 1) == 3;
        Predictor = (int)Scalar(tags, 317, 1);
        if (Scalar(tags, 259) != 8 && Scalar(tags, 259) != 32946)
            throw new NotSupportedException($"{url}: compression {Scalar(tags, 259)} (only DEFLATE).");
        if (Scalar(tags, 284, 1) != 1)
            throw new NotSupportedException($"{url}: only interleaved (chunky) samples.");
        _offsets = Array(tags, 324);
        _counts = Array(tags, 325);

        var scale = Doubles(tags, 33550);
        var tie = Doubles(tags, 33922);
        PixelX = scale[0];
        PixelY = scale[1];
        OriginX = tie[3] - tie[0] * PixelX;
        OriginY = tie[4] + tie[1] * PixelY;
    }

    /// <summary>The decoded pixels of internal tile (tx, ty): bytes, Samples per pixel, row by row,
    /// TileWidth × TileHeight (floats as 4 little-endian bytes each). Null where the tile is empty.</summary>
    public byte[]? Tile(int tx, int ty)
    {
        if (tx < 0 || ty < 0 || tx >= TilesAcross || ty >= TilesDown)
            return null;
        var index = ty * TilesAcross + tx;
        lock (_lock)
        {
            if (_decoded.TryGetValue(index, out var hit))
            {
                _recent.Remove(index);
                _recent.AddFirst(index);
                return hit;
            }
        }
        if (_counts[index] == 0)
            return null;
        var raw = Fetch(_offsets[index], (int)_counts[index], $"t{index}");
        var pixels = Decode(raw);
        lock (_lock)
        {
            _decoded[index] = pixels;
            _recent.AddFirst(index);
            while (_recent.Count > 16)
            {
                _decoded.Remove(_recent.Last!.Value);
                _recent.RemoveLast();
            }
        }
        return pixels;
    }

    private byte[] Decode(byte[] compressed)
    {
        var bytesPerSample = BitsPerSample / 8;
        var rowBytes = TileWidth * Samples * bytesPerSample;
        var output = new byte[rowBytes * TileHeight];
        using (var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress))
        {
            var read = 0;
            int n;
            while (read < output.Length && (n = z.Read(output, read, output.Length - read)) > 0)
                read += n;
        }

        if (Predictor == 2)
        {
            // Horizontal differencing, per sample, 8-bit.
            for (var row = 0; row < TileHeight; row++)
            {
                var o = row * rowBytes;
                for (var i = Samples; i < rowBytes; i++)
                    output[o + i] = (byte)(output[o + i] + output[o + i - Samples]);
            }
        }
        else if (Predictor == 3)
        {
            // Floating point: bytes differenced across the row, stored as byte planes (most
            // significant first); undo both and write little-endian floats.
            var row = new byte[rowBytes];
            var perRow = TileWidth * Samples;
            for (var r = 0; r < TileHeight; r++)
            {
                var o = r * rowBytes;
                for (var i = 1; i < rowBytes; i++)
                    output[o + i] = (byte)(output[o + i] + output[o + i - 1]);
                Buffer.BlockCopy(output, o, row, 0, rowBytes);
                for (var i = 0; i < perRow; i++)
                for (var b = 0; b < bytesPerSample; b++)
                    output[o + i * bytesPerSample + b] = row[(bytesPerSample - 1 - b) * perRow + i];
            }
        }
        return output;
    }

    private byte[] Fetch(long offset, int count, string name)
    {
        var path = Path.Combine(_cacheDir, name + ".bin");
        if (File.Exists(path))
            return File.ReadAllBytes(path);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + count - 1);
                using var response = _http.Send(request);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    throw new FileNotFoundException($"{_url}: not found.");
                response.EnsureSuccessStatusCode();
                var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                File.WriteAllBytes(path + ".part", bytes);
                File.Move(path + ".part", path, overwrite: true);
                return bytes;
            }
            catch (Exception ex) when (attempt < 4 && ex is not FileNotFoundException)
            {
                Thread.Sleep(1000 * attempt);
            }
        }
    }

    // ----- IFD parsing -----

    private sealed record Entry(ushort Type, uint Count, uint ValueOrOffset, int EntryOffset);

    private Dictionary<ushort, Entry> ReadIfd(int offset)
    {
        var tags = new Dictionary<ushort, Entry>();
        var n = BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(offset));
        for (var i = 0; i < n; i++)
        {
            var e = offset + 2 + i * 12;
            tags[BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(e))] = new Entry(
                BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(e + 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(e + 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(e + 8)),
                e + 8);
        }
        return tags;
    }

    private static int TypeSize(ushort type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 or 16 or 17 => 8, _ => 1 };

    private ReadOnlySpan<byte> Values(Entry e)
    {
        var size = TypeSize(e.Type) * (int)e.Count;
        var at = size <= 4 ? e.EntryOffset : (int)e.ValueOrOffset;
        if (at + size > _header.Length)
            throw new NotSupportedException($"{_url}: TIFF tag data beyond the first {HeaderBytes} bytes.");
        return _header.AsSpan(at, size);
    }

    private long Scalar(Dictionary<ushort, Entry> tags, ushort tag, long? fallback = null)
    {
        if (!tags.TryGetValue(tag, out var e))
            return fallback ?? throw new NotSupportedException($"{_url}: TIFF tag {tag} missing.");
        return Array(tags, tag)[0];
    }

    private long[] Array(Dictionary<ushort, Entry> tags, ushort tag)
    {
        var e = tags[tag];
        var v = Values(e);
        var result = new long[e.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = e.Type switch
            {
                3 => BinaryPrimitives.ReadUInt16LittleEndian(v[(i * 2)..]),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(v[(i * 4)..]),
                16 => (long)BinaryPrimitives.ReadUInt64LittleEndian(v[(i * 8)..]),
                _ => v[i]
            };
        }
        return result;
    }

    private double[] Doubles(Dictionary<ushort, Entry> tags, ushort tag)
    {
        var e = tags[tag];
        var v = Values(e);
        var result = new double[e.Count];
        for (var i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadDoubleLittleEndian(v[(i * 8)..]);
        return result;
    }
}
