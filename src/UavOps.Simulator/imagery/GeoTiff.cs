using System.Buffers.Binary;
using System.IO.Compression;
using SkiaSharp;

namespace UavOps.Simulator.Imagery;

/// <summary>
/// A minimal reader for the tiled, JPEG-compressed GeoTIFFs OpenAerialMap serves (Cloud-Optimized
/// GeoTIFF): classic little-endian TIFF, 512 px tiles with shared JPEG tables, reduced-resolution
/// overviews, an optional 1-bit deflate transparency mask per level (or a GDAL nodata value), and
/// UTM georeferencing (ModelTiepoint + ModelPixelScale). No GDAL. Thread-safe.
/// </summary>
public sealed class GeoTiff : IDisposable
{
    private readonly FileStream _file;
    private readonly object _lock = new();

    public GeoTiff(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var header = Read(0, 8);
        if (header[0] != 'I' || header[1] != 'I' || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) != 42)
            throw new InvalidDataException($"{path}: only little-endian classic TIFF is supported.");

        var ifds = new List<Dictionary<ushort, Entry>>();
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        while (offset != 0 && ifds.Count < 64)
        {
            var count = BinaryPrimitives.ReadUInt16LittleEndian(Read(offset, 2));
            var raw = Read(offset + 2, count * 12 + 4);
            var tags = new Dictionary<ushort, Entry>();
            for (var i = 0; i < count; i++)
            {
                var e = raw.AsSpan(i * 12, 12);
                tags[BinaryPrimitives.ReadUInt16LittleEndian(e)] = new Entry(
                    BinaryPrimitives.ReadUInt16LittleEndian(e[2..]), BinaryPrimitives.ReadUInt32LittleEndian(e[4..]), e[8..12].ToArray());
            }
            ifds.Add(tags);
            offset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(count * 12));
        }

        var full = ifds[0];
        var scale = Doubles(full[33550]);
        var tie = Doubles(full[33922]);
        FullWidth = (int)Number(full[256]);
        FullHeight = (int)Number(full[257]);
        PixelSizeX = scale[0];
        PixelSizeY = scale[1];
        OriginEasting = tie[3] - tie[0] * PixelSizeX;
        OriginNorthing = tie[4] + tie[1] * PixelSizeY;
        (UtmZone, North) = ReadUtmZone(full);
        if (full.TryGetValue(42113, out var nodata) && int.TryParse(Ascii(nodata).Trim('\0', ' '), out var nd))
            Nodata = nd;

        var images = ifds.Where(t => Number(t.GetValueOrDefault((ushort)262)) is 6 or 2).ToList();
        var masks = ifds.Where(t => (Number(t.GetValueOrDefault((ushort)254)) & 4) != 0).ToList();
        Levels = images.Select(t =>
        {
            var width = (int)Number(t[256]);
            var height = (int)Number(t[257]);
            var mask = masks.FirstOrDefault(m => (int)Number(m[256]) == width && (int)Number(m[257]) == height);
            return new Level(this, width, height, (int)Number(t[322]), (int)Number(t[323]),
                Longs(t[324]), Longs(t[325]), t.TryGetValue(347, out var tables) ? Bytes(tables) : null,
                mask is null ? null : Longs(mask[324]), mask is null ? null : Longs(mask[325]), (double)FullWidth / width);
        }).OrderBy(l => l.Scale).ToList();
    }

    public int FullWidth { get; }
    public int FullHeight { get; }
    public double PixelSizeX { get; }
    public double PixelSizeY { get; }
    public double OriginEasting { get; }
    public double OriginNorthing { get; }
    public int UtmZone { get; }
    public bool North { get; }
    public int? Nodata { get; }

    /// <summary>Full resolution first, then each overview.</summary>
    public IReadOnlyList<Level> Levels { get; }

    /// <summary>Ground position (UTM) of a full-resolution pixel.</summary>
    public (double E, double N) PixelToUtm(double col, double row) => (OriginEasting + col * PixelSizeX, OriginNorthing - row * PixelSizeY);

    public (double Col, double Row) UtmToPixel(double e, double n) => ((e - OriginEasting) / PixelSizeX, (OriginNorthing - n) / PixelSizeY);

    public void Dispose() => _file.Dispose();

    /// <summary>One resolution level: its tiles, decoded on demand into images with transparency
    /// where the photo has no data.</summary>
    public sealed class Level(GeoTiff owner, int width, int height, int tileWidth, int tileHeight,
        long[] offsets, long[] counts, byte[]? jpegTables, long[]? maskOffsets, long[]? maskCounts, double scale)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public int TileWidth { get; } = tileWidth;
        public int TileHeight { get; } = tileHeight;
        public int TilesAcross => (Width + TileWidth - 1) / TileWidth;
        public int TilesDown => (Height + TileHeight - 1) / TileHeight;

        /// <summary>Full-resolution pixels per pixel of this level.</summary>
        public double Scale { get; } = scale;

        public SKImage? DecodeTile(int tx, int ty)
        {
            if (tx < 0 || ty < 0 || tx >= TilesAcross || ty >= TilesDown)
                return null;
            var index = ty * TilesAcross + tx;
            if (counts[index] == 0)
                return null;

            var data = owner.Read(offsets[index], (int)counts[index]);
            var jpeg = jpegTables is { Length: > 4 } && data.Length > 2
                // Abbreviated stream: the shared tables (without their EOI) + the tile (without its SOI).
                ? [.. jpegTables.AsSpan(0, jpegTables.Length - 2), .. data.AsSpan(2)]
                : data;
            using var decoded = SKBitmap.Decode(jpeg);
            if (decoded is null)
                return null;

            using var bitmap = new SKBitmap(new SKImageInfo(TileWidth, TileHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.Transparent);
                using var decodedImage = SKImage.FromBitmap(decoded);
                canvas.DrawImage(decodedImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
            }

            var mask = maskOffsets is not null && maskCounts![index] > 0 ? owner.Read(maskOffsets[index], (int)maskCounts[index]) : null;
            // Without a mask the border is black, whatever nodata value is declared (Yatir declares
            // -10000, which an 8-bit JPEG can't hold; its border is 0).
            var nodata = owner.Nodata is >= 0 and <= 255 ? owner.Nodata : 0;
            ApplyTransparency(bitmap, mask, nodata);
            return SKImage.FromBitmap(bitmap);
        }

        private void ApplyTransparency(SKBitmap bitmap, byte[]? deflatedMask, int? nodata)
        {
            if (deflatedMask is null && nodata is null)
                return;
            byte[]? bits = null;
            if (deflatedMask is not null)
            {
                using var z = new ZLibStream(new MemoryStream(deflatedMask), CompressionMode.Decompress);
                using var ms = new MemoryStream();
                z.CopyTo(ms);
                bits = ms.ToArray();
            }
            var rowBytes = (TileWidth + 7) / 8;
            var pixels = bitmap.GetPixelSpan();
            for (var y = 0; y < TileHeight; y++)
            for (var x = 0; x < TileWidth; x++)
            {
                var i = (y * TileWidth + x) * 4;
                bool transparent;
                if (bits is not null)
                {
                    var b = y * rowBytes + x / 8;
                    transparent = b >= bits.Length || (bits[b] & (0x80 >> (x % 8))) == 0;
                }
                else
                {
                    // JPEG blurs the nodata value, so "nodata" is anything within a few levels of it.
                    transparent = pixels[i] <= nodata + 6 && pixels[i + 1] <= nodata + 6 && pixels[i + 2] <= nodata + 6;
                }
                if (transparent)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                }
            }
        }
    }

    private (int Zone, bool North) ReadUtmZone(Dictionary<ushort, Entry> tags)
    {
        var keys = Longs(tags[34735]);
        for (var i = 4; i + 3 < keys.Length; i += 4)
        {
            if (keys[i] != 3072)
                continue;
            var epsg = keys[i + 3];
            if (epsg is >= 32601 and <= 32660)
                return ((int)epsg - 32600, true);
            if (epsg is >= 32701 and <= 32760)
                return ((int)epsg - 32700, false);
            throw new NotSupportedException($"Only UTM (EPSG 326xx/327xx) imagery is supported, not EPSG:{epsg}.");
        }
        throw new InvalidDataException("The GeoTIFF has no projected coordinate system.");
    }

    // ----- Tag values -----

    private sealed record Entry(ushort Type, uint Count, byte[] Inline);

    private static int TypeSize(ushort type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 or 16 => 8, _ => 1 };

    private byte[] Bytes(Entry e)
    {
        var size = (int)e.Count * TypeSize(e.Type);
        return size <= 4 ? e.Inline[..size] : Read(BinaryPrimitives.ReadUInt32LittleEndian(e.Inline), size);
    }

    private string Ascii(Entry e) => System.Text.Encoding.ASCII.GetString(Bytes(e));

    private long Number(Entry? e) => e is null ? 0 : Longs(e)[0];

    private long[] Longs(Entry e)
    {
        var b = Bytes(e);
        var size = TypeSize(e.Type);
        var values = new long[e.Count];
        for (var i = 0; i < values.Length; i++)
        {
            var s = b.AsSpan(i * size, size);
            values[i] = size switch
            {
                1 => s[0],
                2 => BinaryPrimitives.ReadUInt16LittleEndian(s),
                4 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(s)
            };
        }
        return values;
    }

    private double[] Doubles(Entry e)
    {
        var b = Bytes(e);
        return Enumerable.Range(0, (int)e.Count).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(i * 8, 8))).ToArray();
    }

    private byte[] Read(long offset, int length)
    {
        var buffer = new byte[length];
        lock (_lock)
        {
            _file.Seek(offset, SeekOrigin.Begin);
            _file.ReadExactly(buffer);
        }
        return buffer;
    }
}
