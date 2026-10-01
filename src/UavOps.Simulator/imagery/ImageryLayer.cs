using System.Text.Json;
using SkiaSharp;
using UavOps.Agent.Mission;
using UavOps.Simulator.Camera;

namespace UavOps.Simulator.Imagery;

/// <summary>An aerial photo from imagery.json, with where it is.</summary>
public sealed record ImageryImage(string Name, string Role, string Title, string Attribution, string License, GeoTiff Tiff)
{
    public (GeoPoint SouthWest, GeoPoint NorthEast) Bounds { get; init; }
}

/// <summary>
/// The real aerial photos (<c>imagery/imagery.json</c>, fetched by scripts/fetch-imagery.ps1),
/// drawn into any canvas whose pixels map to the ground affinely - a ground chunk, a map tile.
/// Picks the photo's resolution level to suit the canvas, decodes its tiles on demand (cached),
/// and leaves its no-data border transparent so whatever was drawn underneath shows through.
/// Missing files are skipped: the simulator then just draws the OSM ground.
/// </summary>
public sealed class ImageryLayer
{
    private readonly LruCache<(string Image, int Level, int X, int Y), SKImage?> _tiles = new(256);

    public ImageryLayer(string directory, ILogger<ImageryLayer>? logger = null)
    {
        var manifest = Path.Combine(directory, "imagery.json");
        if (!File.Exists(manifest))
            return;
        using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
        foreach (var entry in doc.RootElement.GetProperty("images").EnumerateArray())
        {
            var file = Path.Combine(directory, entry.GetProperty("file").GetString()!);
            var name = entry.GetProperty("name").GetString()!;
            if (!File.Exists(file))
            {
                logger?.LogInformation("Aerial photo '{Name}' not downloaded ({File}); run scripts/fetch-imagery.ps1 to use it.", name, file);
                continue;
            }
            GeoTiff tiff;
            try
            {
                tiff = new GeoTiff(file);
            }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or KeyNotFoundException)
            {
                // One photo in a format this reader can't take mustn't stop the simulator.
                logger?.LogWarning("Aerial photo '{Name}' skipped: {Message}", name, ex.Message);
                continue;
            }
            var corners = new[] { (0.0, 0.0), (tiff.FullWidth, 0.0), (0.0, tiff.FullHeight), (tiff.FullWidth, tiff.FullHeight) }
                .Select(c => tiff.PixelToUtm(c.Item1, c.Item2))
                .Select(u => Utm.ToLatLng(u.E, u.N, tiff.UtmZone, tiff.North))
                .ToList();
            Images.Add(new ImageryImage(name, entry.GetProperty("role").GetString() ?? "ground",
                entry.GetProperty("title").GetString() ?? name, entry.GetProperty("attribution").GetString() ?? "",
                entry.GetProperty("license").GetString() ?? "", tiff)
            {
                Bounds = (new GeoPoint(corners.Min(c => c.Lat), corners.Min(c => c.Lng)), new GeoPoint(corners.Max(c => c.Lat), corners.Max(c => c.Lng)))
            });
            logger?.LogInformation("Aerial photo '{Name}': {W}x{H} px at {Gsd:F3} m, {Levels} levels.", name, tiff.FullWidth, tiff.FullHeight, tiff.PixelSizeX, tiff.Levels.Count);
        }
    }

    public List<ImageryImage> Images { get; } = [];

    public IEnumerable<ImageryImage> Ground => Images.Where(i => i.Role == "ground");

    public ImageryImage? Find(string name) => Images.FirstOrDefault(i => i.Name == name);

    /// <summary>Whether a ground photo's bounding box contains this point.</summary>
    public bool Covers(GeoPoint p) => Ground.Any(i =>
        p.Lat >= i.Bounds.SouthWest.Lat && p.Lat <= i.Bounds.NorthEast.Lat && p.Lng >= i.Bounds.SouthWest.Lng && p.Lng <= i.Bounds.NorthEast.Lng);

    /// <summary>
    /// Draws every ground photo overlapping the canvas. <paramref name="canvasToGeo"/> maps a canvas
    /// pixel to the ground; it's taken as affine (true to well under a pixel at the scales drawn).
    /// </summary>
    public void Draw(SKCanvas canvas, int width, int height, Func<double, double, GeoPoint> canvasToGeo, double metersPerPixel)
    {
        foreach (var image in Ground)
            DrawImage(canvas, image, width, height, canvasToGeo, metersPerPixel);
    }

    private void DrawImage(SKCanvas canvas, ImageryImage image, int width, int height, Func<double, double, GeoPoint> canvasToGeo, double metersPerPixel)
    {
        var tiff = image.Tiff;
        // Full-resolution photo pixel for three canvas corners: an affine map canvas -> photo.
        (double Col, double Row) PhotoPixel(double x, double y)
        {
            var g = canvasToGeo(x, y);
            var (e, n) = Utm.FromLatLng(g.Lat, g.Lng, tiff.UtmZone, tiff.North);
            return tiff.UtmToPixel(e, n);
        }
        var p0 = PhotoPixel(0, 0);
        var px = PhotoPixel(width, 0);
        var py = PhotoPixel(0, height);
        var p1 = PhotoPixel(width, height);

        var minCol = new[] { p0.Col, px.Col, py.Col, p1.Col }.Min();
        var maxCol = new[] { p0.Col, px.Col, py.Col, p1.Col }.Max();
        var minRow = new[] { p0.Row, px.Row, py.Row, p1.Row }.Min();
        var maxRow = new[] { p0.Row, px.Row, py.Row, p1.Row }.Max();
        if (maxCol < 0 || maxRow < 0 || minCol > tiff.FullWidth || minRow > tiff.FullHeight)
            return;

        // The coarsest level still at least as sharp as the canvas.
        var level = tiff.Levels[0];
        foreach (var l in tiff.Levels)
            if (l.Scale * tiff.PixelSizeX <= metersPerPixel * 1.2)
                level = l;

        // Canvas -> full-resolution photo pixel, as a matrix; inverted to draw photo tiles.
        var toPhoto = new SKMatrix(
            (float)((px.Col - p0.Col) / width), (float)((py.Col - p0.Col) / height), (float)p0.Col,
            (float)((px.Row - p0.Row) / width), (float)((py.Row - p0.Row) / height), (float)p0.Row,
            0, 0, 1);
        if (!toPhoto.TryInvert(out var fromPhoto))
            return;

        var tx0 = Math.Max(0, (int)Math.Floor(minCol / level.Scale / level.TileWidth));
        var tx1 = Math.Min(level.TilesAcross - 1, (int)Math.Floor(maxCol / level.Scale / level.TileWidth));
        var ty0 = Math.Max(0, (int)Math.Floor(minRow / level.Scale / level.TileHeight));
        var ty1 = Math.Min(level.TilesDown - 1, (int)Math.Floor(maxRow / level.Scale / level.TileHeight));
        if ((tx1 - tx0 + 1) * (ty1 - ty0 + 1) > 400)
            return; // far zoomed out; not worth decoding hundreds of tiles for a few pixels

        var levelIndex = tiff.Levels.ToList().IndexOf(level);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        for (var ty = ty0; ty <= ty1; ty++)
        for (var tx = tx0; tx <= tx1; tx++)
        {
            var key = (image.Name, levelIndex, tx, ty);
            if (!_tiles.TryGet(key, out var tile))
            {
                tile = level.DecodeTile(tx, ty);
                _tiles.Add(key, tile);
            }
            if (tile is null)
                continue;
            // Tile pixel -> full-resolution photo pixel -> canvas.
            var tileToPhoto = SKMatrix.CreateScaleTranslation((float)level.Scale, (float)level.Scale,
                (float)(tx * level.TileWidth * level.Scale), (float)(ty * level.TileHeight * level.Scale));
            var matrix = fromPhoto.PreConcat(tileToPhoto);
            canvas.Save();
            canvas.Concat(in matrix);
            // Edge tiles are padded to full size with repeats of the last pixels: drawn, that
            // padding smeared streaks off the photo's edge (worst on the small overviews used
            // zoomed out). Only the level's real pixels are drawn.
            var validWidth = Math.Min(level.TileWidth, level.Width - tx * level.TileWidth);
            var validHeight = Math.Min(level.TileHeight, level.Height - ty * level.TileHeight);
            canvas.ClipRect(new SKRect(0, 0, validWidth, validHeight));
            canvas.DrawImage(tile, 0, 0, sampling);
            canvas.Restore();
        }
    }

    /// <summary>A rectangle of a photo at full resolution, rotated so <paramref name="headingDeg"/>
    /// (the direction the thing in it faces, degrees clockwise from the photo's up) points up.</summary>
    public SKImage? Cutout(string imageName, double centerCol, double centerRow, double lengthPx, double widthPx, double headingDeg, double featherPx)
    {
        if (Find(imageName) is not { } image)
            return null;
        var w = (int)Math.Ceiling(widthPx + 2 * featherPx);
        var h = (int)Math.Ceiling(lengthPx + 2 * featherPx);
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Output pixel -> photo pixel: rotate about the centre by the heading.
        var toPhoto = SKMatrix.CreateTranslation((float)centerCol, (float)centerRow)
            .PreConcat(SKMatrix.CreateRotationDegrees((float)headingDeg))
            .PreConcat(SKMatrix.CreateTranslation(-w / 2f, -h / 2f));
        if (!toPhoto.TryInvert(out var fromPhoto))
            return null;
        var level = image.Tiff.Levels[0];
        var reach = (int)Math.Ceiling(Math.Max(w, h) / 2.0 / level.TileWidth) + 1;
        var ctx = (int)(centerCol / level.TileWidth);
        var cty = (int)(centerRow / level.TileHeight);
        for (var ty = cty - reach; ty <= cty + reach; ty++)
        for (var tx = ctx - reach; tx <= ctx + reach; tx++)
        {
            using var tile = level.DecodeTile(tx, ty);
            if (tile is null)
                continue;
            var matrix = fromPhoto.PreConcat(SKMatrix.CreateTranslation(tx * level.TileWidth, ty * level.TileHeight));
            canvas.Save();
            canvas.Concat(in matrix);
            canvas.DrawImage(tile, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
            canvas.Restore();
        }

        // Soft edges: keep a rounded rectangle, fading out over the feather width.
        var inner = new SKRect((float)featherPx, (float)featherPx, (float)(w - featherPx), (float)(h - featherPx));
        using var mask = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        mask.Canvas.Clear(SKColors.Transparent);
        using (var solid = new SKPaint { Color = SKColors.White, IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, (float)(featherPx / 2)) })
            mask.Canvas.DrawRoundRect(inner, (float)(widthPx * 0.2), (float)(widthPx * 0.2), solid);
        using var maskImage = mask.Snapshot();
        using var dstIn = new SKPaint { BlendMode = SKBlendMode.DstIn };
        canvas.DrawImage(maskImage, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), dstIn);
        return surface.Snapshot();
    }
}
