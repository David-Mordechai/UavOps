using SkiaSharp;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector.Perception;

/// <summary>
/// Runs the detector on a grid of overlapping tiles of the full-resolution frame instead of the
/// frame shrunk to the engine's square, so a car has 2x the pixels per side. Measured on the Jetson
/// (2026-10-01, the moving red car at the 100 m search scale): found in 2 of 14 frames at 15.6 cm/px
/// (the whole frame in 640 px), 6 of 14 at 7.8 cm/px (a 2x2 tile). Each tile is read in place
/// (<see cref="IRegionDetector"/>), boxes come back in frame coordinates, and an object the overlap
/// shows to two tiles is reported once.
/// </summary>
public sealed class TiledDetector : IObjectDetector, IDetectorTiming
{
    private readonly IRegionDetector _inner;
    private readonly int _columns;
    private readonly int _rows;
    private readonly int _overlap;

    public TiledDetector(IRegionDetector inner, int columns, int rows, int overlapPx)
    {
        if (columns < 1 || rows < 1 || overlapPx < 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Tiles need at least 1 column and 1 row, and a non-negative overlap.");
        _inner = inner;
        _columns = columns;
        _rows = rows;
        _overlap = overlapPx;
        Name = $"{inner.Name} on {columns}x{rows} tiles";
    }

    public string Name { get; }

    /// <summary>Wide enough that each tile is at least the engine's size: the frame is decoded at
    /// full resolution, not the half a single 640-px pass needs.</summary>
    public int InputSize => _inner.InputSize * _columns;

    /// <summary>The tiles' stages added up.</summary>
    public (double Prepare, double Infer, double Decode) LastTiming { get; private set; }

    public IReadOnlyList<DetectedObject> Detect(SKBitmap image, double minScore)
    {
        var all = new List<DetectedObject>();
        var timing = (Prepare: 0.0, Infer: 0.0, Decode: 0.0);
        foreach (var tile in Tiles(image.Width, image.Height, _columns, _rows, _overlap))
        {
            foreach (var found in _inner.Detect(image, tile, minScore))
                all.Add(found with { Box = ToFrame(found.Box, tile, image.Width, image.Height) });
            if (_inner is IDetectorTiming t)
                timing = (timing.Prepare + t.LastTiming.Prepare, timing.Infer + t.LastTiming.Infer, timing.Decode + t.LastTiming.Decode);
        }
        LastTiming = timing;
        return Merge(all);
    }

    /// <summary>The grid's tiles, in pixels: equal size, neighbours sharing <paramref name="overlap"/>
    /// pixels, together covering the frame exactly.</summary>
    public static IReadOnlyList<SKRectI> Tiles(int width, int height, int columns, int rows, int overlap)
    {
        var xs = Spans(width, columns, overlap);
        var ys = Spans(height, rows, overlap);
        return [.. from y in ys from x in xs select new SKRectI(x.Start, y.Start, x.End, y.End)];

        static List<(int Start, int End)> Spans(int length, int count, int overlap)
        {
            if (count == 1)
                return [(0, length)];
            var size = Math.Min(length, (length + (count - 1) * overlap + count - 1) / count);
            var step = (double)(length - size) / (count - 1);
            return [.. Enumerable.Range(0, count).Select(i => (Start: (int)Math.Round(i * step), End: (int)Math.Round(i * step) + size))];
        }
    }

    private static BoundingBox ToFrame(BoundingBox box, SKRectI tile, int width, int height) => new(
        (tile.Left + box.X1 / 1000 * tile.Width) / width * 1000,
        (tile.Top + box.Y1 / 1000 * tile.Height) / height * 1000,
        (tile.Left + box.X2 / 1000 * tile.Width) / width * 1000,
        (tile.Top + box.Y2 / 1000 * tile.Height) / height * 1000);

    /// <summary>
    /// One box per object: the higher-scoring of two that overlap by IoU > 0.5, or where one lies
    /// mostly inside the other (a car cut by a tile's edge is a partial box inside the neighbour's
    /// whole one). Vehicles merge across classes, since two tiles may call the same car "Car" and
    /// "SUV"; anything else only with its own class.
    /// </summary>
    public static IReadOnlyList<DetectedObject> Merge(IReadOnlyList<DetectedObject> found)
    {
        var kept = new List<DetectedObject>();
        foreach (var candidate in found.OrderByDescending(o => o.Score))
            if (!kept.Any(k => SameKind(k.Class, candidate.Class) && Duplicate(k.Box, candidate.Box)))
                kept.Add(candidate);
        return kept;

        static bool SameKind(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase) || (ObjectClasses.IsVehicle(a) && ObjectClasses.IsVehicle(b));
    }

    private static bool Duplicate(BoundingBox a, BoundingBox b)
    {
        var w = Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1);
        var h = Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1);
        if (w <= 0 || h <= 0)
            return false;
        var overlap = w * h;
        var areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        var areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);
        return overlap / (areaA + areaB - overlap) > 0.5 || overlap / Math.Min(areaA, areaB) > 0.8;
    }
}
