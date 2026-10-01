using System.Collections.Concurrent;
using SkiaSharp;
using UavOps.Agent.Mission;
using UavOps.Simulator.Imagery;
using UavOps.Simulator.Map;

namespace UavOps.Simulator.Camera;

/// <summary>
/// The ground as a nadir camera sees it, drawn from the offline OSM map
/// (<see cref="PmTilesReader"/>): landuse and landcover as textured fills, water, roads with
/// markings, buildings with shadows, trees, and parked cars (moving traffic is drawn per frame).
///
/// Where OSM has no buildings mapped (most of this area), buildings are added along the roads so
/// streets look built-up. Traffic never includes a white van, so the only white van in view is one
/// placed as a scenario object; white cars, pickups and trucks are there as distractors.
///
/// Drawn in square chunks at a few fixed resolutions (<see cref="LevelMetersPerPixel"/>) and
/// cached, so a camera frame is just a few cached images drawn rotated and scaled. Everything
/// random is seeded by position, so a place always looks the same, across chunks and levels.
/// </summary>
public sealed class GroundRenderer
{
    /// <summary>Chunk resolutions. The finest is for the payload's zoom close-ups.</summary>
    public static readonly double[] LevelMetersPerPixel = [0.03, 0.15, 0.6, 2.4];
    public const int ChunkPixels = 1024;

    private static readonly HashSet<string> Layers = ["landuse", "landcover", "park", "water", "waterway", "transportation", "building", "aeroway"];

    /// <summary>Shadow cast per meter of height, in local meters (sun to the south-west, early afternoon).</summary>
    public static readonly Vec2 SunShadowPerMeter = new(0.28, 0.42);

    private readonly PmTilesReader? _tiles;
    private readonly ImageryLayer? _imagery;
    private readonly LruCache<(int Level, int X, int Y), SKImage> _chunks = new(64);
    private readonly LruCache<(int X, int Y), Dictionary<string, VectorTileLayer>> _decoded = new(48);
    private readonly ConcurrentDictionary<(int, int, int), Lazy<SKImage>> _rendering = new();
    private readonly int _zoom;

    private readonly double _trafficDensity;

    public GroundRenderer(PmTilesReader? tiles, GeoPoint origin, ImageryLayer? imagery = null, double trafficDensity = 1)
    {
        _tiles = tiles;
        _imagery = imagery;
        _trafficDensity = Math.Clamp(trafficDensity, 0, 1);
        _zoom = tiles?.MaxZoom ?? 14;
        Projection = new GeoProjection(origin);
    }

    /// <summary>The ground's local frame (meters east/north of a fixed origin).</summary>
    public GeoProjection Projection { get; }

    public bool HasMapData => _tiles is not null;

    public static double ChunkMeters(int level) => LevelMetersPerPixel[level] * ChunkPixels;

    /// <summary>The finest level that isn't sharper than needed for <paramref name="metersPerPixel"/>.</summary>
    public static int LevelFor(double metersPerPixel)
    {
        var level = 0;
        for (var i = 0; i < LevelMetersPerPixel.Length; i++)
            if (LevelMetersPerPixel[i] <= metersPerPixel * 1.05)
                level = i;
        return level;
    }

    public SKImage GetChunk(int level, int cx, int cy)
    {
        var key = (level, cx, cy);
        if (_chunks.TryGet(key, out var cached))
            return cached;
        var lazy = _rendering.GetOrAdd(key, k => new Lazy<SKImage>(() => RenderChunk(k.Item1, k.Item2, k.Item3)));
        var image = lazy.Value;
        _chunks.Add(key, image);
        _rendering.TryRemove(key, out _);
        return image;
    }

    // ----- One chunk -----

    private SKImage RenderChunk(int level, int cx, int cy)
    {
        var mpp = LevelMetersPerPixel[level];
        var size = ChunkMeters(level);
        var x0 = cx * size;
        var y1 = (cy + 1) * size;
        var ctx = new ChunkContext(level, mpp, x0, y1, size);

        using var surface = SKSurface.Create(new SKImageInfo(ChunkPixels, ChunkPixels, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(Palette.Soil);
        Texture(canvas, null, ctx, 0.35f, frequencyMeters: 9, seed: 1);

        var features = _tiles is null ? new ChunkFeatures() : CollectFeatures(ctx);

        foreach (var (path, cls) in features.Landuse)
            Fill(canvas, path, ctx, Palette.Landuse(cls), 0.25f, 14, 2);
        foreach (var (path, cls, sub) in features.Landcover)
            DrawLandcover(canvas, path, ctx, cls, sub);
        foreach (var (path, cls) in features.Water)
            Fill(canvas, path, ctx, cls == "swimming_pool" ? Palette.Pool : Palette.Water, 0.12f, 30, 3);
        foreach (var (path, width) in features.Waterways)
            Stroke(canvas, path, Palette.Water, (float)(width / mpp));

        if (mpp <= 0.6)
            DrawSyntheticBuildings(canvas, ctx, features);

        DrawRoads(canvas, ctx, features);

        foreach (var (path, height, seed) in features.Buildings)
            DrawBuilding(canvas, path, ctx, height, seed);

        if (mpp <= 0.6)
        {
            DrawTrees(canvas, ctx, features);
            if (_trafficDensity > 0)
                DrawTraffic(canvas, ctx, features, _trafficDensity);
        }

        // Real aerial photos where there are any: they are the ground there, drawn over everything
        // above (their no-data border stays transparent, so the drawn ground shows around them).
        _imagery?.Draw(canvas, ChunkPixels, ChunkPixels, (x, y) => Projection.ToGeo(ctx.ToLocal(new SKPoint((float)x, (float)y))), mpp);

        foreach (var f in features.Disposables)
            f.Dispose();
        return surface.Snapshot();
    }

    private sealed record ChunkContext(int Level, double Mpp, double X0, double Y1, double Size)
    {
        public SKPoint ToPixel(Vec2 local) => new((float)((local.X - X0) / Mpp), (float)((Y1 - local.Y) / Mpp));
        public Vec2 ToLocal(SKPoint p) => new(X0 + p.X * Mpp, Y1 - p.Y * Mpp);

        public bool Near(Vec2 local, double margin) =>
            local.X > X0 - margin && local.X < X0 + Size + margin && local.Y > Y1 - Size - margin && local.Y < Y1 + margin;

        /// <summary>Global pixel offset at this level, so textures line up across chunks.</summary>
        public SKMatrix TextureAnchor => SKMatrix.CreateTranslation((float)(-X0 / Mpp), (float)(Y1 / Mpp));
    }

    private sealed class ChunkFeatures
    {
        public List<(SKPath Path, string Class)> Landuse { get; } = [];
        public List<(SKPath Path, string Class, string Sub)> Landcover { get; } = [];
        public List<(SKPath Path, string Class)> Water { get; } = [];
        public List<(SKPath Path, double Width)> Waterways { get; } = [];
        public List<Road> Roads { get; } = [];
        public List<(SKPath Path, double Height, int Seed)> Buildings { get; } = [];
        /// <summary>Areas where nothing gets built: parks, fields, woods, water, sand, pitches.</summary>
        public List<SKPath> OpenLand { get; } = [];
        public List<IDisposable> Disposables { get; } = [];
    }

    /// <summary>A road piece in local meters, and the tile that owns it (for placing things once).</summary>
    private sealed record Road(string Class, List<Vec2> Points, double WidthMeters, SKPath Path, (Vec2 Min, Vec2 Max) OwnerTile);

    private ChunkFeatures CollectFeatures(ChunkContext ctx)
    {
        var features = new ChunkFeatures();
        var corners = new[]
        {
            Projection.ToGeo(new Vec2(ctx.X0, ctx.Y1)),
            Projection.ToGeo(new Vec2(ctx.X0 + ctx.Size, ctx.Y1 - ctx.Size))
        };
        var (tx0, ty0) = TileMath.TileAt(corners[0].Lat, corners[0].Lng, _zoom);
        var (tx1, ty1) = TileMath.TileAt(corners[1].Lat, corners[1].Lng, _zoom);

        for (var tx = Math.Min(tx0, tx1); tx <= Math.Max(tx0, tx1); tx++)
        for (var ty = Math.Min(ty0, ty1); ty <= Math.Max(ty0, ty1); ty++)
        {
            var layers = Decoded(tx, ty);
            if (layers is null)
                continue;
            var toLocal = TileToLocal(tx, ty, layers.Values.FirstOrDefault()?.Extent ?? 4096);
            var owner = (toLocal(0, 4096), toLocal(4096, 0));
            var ownerBox = (new Vec2(Math.Min(owner.Item1.X, owner.Item2.X), Math.Min(owner.Item1.Y, owner.Item2.Y)),
                            new Vec2(Math.Max(owner.Item1.X, owner.Item2.X), Math.Max(owner.Item1.Y, owner.Item2.Y)));

            foreach (var (name, layer) in layers)
            {
                foreach (var feature in layer.Features)
                {
                    var parts = feature.Parts.Select(p => p.Select(v => toLocal(v.X, v.Y)).ToList()).ToList();
                    if (!parts.SelectMany(p => p).Any(v => ctx.Near(v, 400)))
                        continue;
                    var cls = feature.Get("class") ?? "";
                    var sub = feature.Get("subclass") ?? "";
                    var isArea = feature.Type == GeometryType.Polygon;

                    switch (name)
                    {
                        case "landuse" when isArea:
                            features.Landuse.Add((Track(features, PolygonPath(parts, ctx)), cls));
                            if (cls is "pitch" or "cemetery" or "playground")
                                features.OpenLand.Add(Track(features, PolygonPath(parts, ctx)));
                            break;
                        case "landcover" when isArea:
                        case "park" when isArea:
                            var landcoverClass = name == "park" ? "grass" : cls;
                            features.Landcover.Add((Track(features, PolygonPath(parts, ctx)), landcoverClass, name == "park" ? "park" : sub));
                            features.OpenLand.Add(Track(features, PolygonPath(parts, ctx)));
                            break;
                        case "water" when isArea:
                            features.Water.Add((Track(features, PolygonPath(parts, ctx)), cls));
                            features.OpenLand.Add(Track(features, PolygonPath(parts, ctx)));
                            break;
                        case "waterway":
                            features.Waterways.Add((Track(features, LinePath(parts, ctx)), cls == "river" ? 12 : 3));
                            break;
                        case "transportation" when !isArea:
                            foreach (var part in parts.Where(p => p.Count >= 2))
                            {
                                var width = RoadWidth(cls, sub);
                                features.Roads.Add(new Road(cls, part, width, Track(features, LinePath([part], ctx)), ownerBox));
                            }
                            break;
                        case "transportation" when isArea:
                            features.Landuse.Add((Track(features, PolygonPath(parts, ctx)), "pedestrian"));
                            break;
                        case "aeroway" when isArea:
                            features.Landuse.Add((Track(features, PolygonPath(parts, ctx)), "aeroway"));
                            break;
                        case "building" when isArea:
                            var seed = Hash(parts[0][0]);
                            features.Buildings.Add((Track(features, PolygonPath(parts, ctx)), feature.GetNumber("render_height", 8), seed));
                            break;
                    }
                }
            }
        }
        return features;
    }

    private static SKPath Track(ChunkFeatures features, SKPath path)
    {
        features.Disposables.Add(path);
        return path;
    }

    private Dictionary<string, VectorTileLayer>? Decoded(int x, int y)
    {
        if (_decoded.TryGet((x, y), out var cached))
            return cached;
        var bytes = _tiles!.GetTile(_zoom, x, y);
        var layers = bytes is null ? new Dictionary<string, VectorTileLayer>() : VectorTileDecoder.Decode(bytes, Layers);
        _decoded.Add((x, y), layers);
        return layers;
    }

    /// <summary>Tile coordinates to local meters. Affine from three corners: Mercator is linear
    /// enough across one z14 tile (~2.4 km) for a picture.</summary>
    private Func<int, int, Vec2> TileToLocal(int tx, int ty, int extent)
    {
        Vec2 Corner(int px, int py)
        {
            var (lat, lng) = TileMath.ToGeo(_zoom, tx, ty, px, py, extent);
            return Projection.ToLocal(new GeoPoint(lat, lng));
        }
        var o = Corner(0, 0);
        var ex = Corner(extent, 0) - o;
        var ey = Corner(0, extent) - o;
        return (px, py) => o + ex * ((double)px / extent) + ey * ((double)py / extent);
    }

    private static SKPath PolygonPath(List<List<Vec2>> parts, ChunkContext ctx)
    {
        using var builder = new SKPathBuilder { FillType = SKPathFillType.EvenOdd };
        foreach (var ring in parts.Where(r => r.Count >= 3))
            builder.AddPoly(ring.Select(ctx.ToPixel).ToArray(), close: true);
        return builder.Detach();
    }

    private static SKPath LinePath(List<List<Vec2>> parts, ChunkContext ctx)
    {
        using var builder = new SKPathBuilder();
        foreach (var line in parts.Where(l => l.Count >= 2))
            builder.AddPoly(line.Select(ctx.ToPixel).ToArray(), close: false);
        return builder.Detach();
    }

    // ----- Drawing -----

    private static void Fill(SKCanvas canvas, SKPath path, ChunkContext ctx, SKColor color, float texture, double frequencyMeters, int seed)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
        Texture(canvas, path, ctx, texture, frequencyMeters, seed);
    }

    /// <summary>Perlin noise laid over a region as grayscale light/dark variation.</summary>
    private static void Texture(SKCanvas canvas, SKPath? clip, ChunkContext ctx, float amount, double frequencyMeters, int seed)
    {
        if (amount <= 0)
            return;
        var frequency = (float)(ctx.Mpp / frequencyMeters);
        using var noise = SKShader.CreatePerlinNoiseFractalNoise(frequency, frequency, 4, seed).WithLocalMatrix(ctx.TextureAnchor);
        using var gray = SKColorFilter.CreateColorMatrix(
        [
            0.33f, 0.33f, 0.33f, 0, 0,
            0.33f, 0.33f, 0.33f, 0, 0,
            0.33f, 0.33f, 0.33f, 0, 0,
            0, 0, 0, 0, amount
        ]);
        using var paint = new SKPaint { Shader = noise, ColorFilter = gray, BlendMode = SKBlendMode.Overlay, IsAntialias = true };
        if (clip is null)
            canvas.DrawPaint(paint);
        else
            canvas.DrawPath(clip, paint);
    }

    private static void Stroke(SKCanvas canvas, SKPath path, SKColor color, float width, float[]? dashes = null)
    {
        using var paint = new SKPaint
        {
            Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(width, 0.6f),
            StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true
        };
        if (dashes is not null)
        {
            paint.PathEffect = SKPathEffect.CreateDash(dashes, 0);
            paint.StrokeCap = SKStrokeCap.Butt;
        }
        canvas.DrawPath(path, paint);
    }

    private static void DrawLandcover(SKCanvas canvas, SKPath path, ChunkContext ctx, string cls, string sub)
    {
        var color = Palette.Landcover(cls, sub);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);

        if (cls == "farmland" && sub != "orchard")
        {
            // Plough lines, at an angle that depends on the field.
            var angle = (Hash(new Vec2(path.Bounds.MidX, path.Bounds.MidY)) & 0xff) / 255f * 180;
            var spacing = (float)(3.5 / ctx.Mpp);
            if (spacing >= 1.5f)
            {
                using var stripes = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(spacing, 0),
                    [new SKColor(0, 0, 0, 34), new SKColor(255, 255, 255, 18)], SKShaderTileMode.Mirror)
                    .WithLocalMatrix(SKMatrix.CreateRotationDegrees(angle));
                using var stripePaint = new SKPaint { Shader = stripes, IsAntialias = true };
                canvas.DrawPath(path, stripePaint);
            }
        }
        Texture(canvas, path, ctx, cls is "sand" ? 0.2f : 0.45f, cls is "wood" ? 5 : 11, cls.Length + sub.Length);
    }

    private static void DrawRoads(SKCanvas canvas, ChunkContext ctx, ChunkFeatures features)
    {
        var mpp = (float)ctx.Mpp;
        // Wider first, so smaller roads join on top.
        var roads = features.Roads.OrderByDescending(r => r.WidthMeters).ToList();
        foreach (var road in roads.Where(r => r.Class is not ("path" or "track" or "rail") && !r.Class.EndsWith("_construction")))
            Stroke(canvas, road.Path, Palette.Curb, (float)((road.WidthMeters + 1.2) / mpp));
        foreach (var road in roads)
        {
            var (color, width) = road.Class switch
            {
                "path" => (Palette.Footpath, road.WidthMeters),
                "track" => (Palette.Dirt, road.WidthMeters),
                "rail" => (Palette.Ballast, road.WidthMeters),
                _ when road.Class.EndsWith("_construction") => (Palette.Dirt, road.WidthMeters),
                _ => (Palette.Asphalt, road.WidthMeters)
            };
            Stroke(canvas, road.Path, color, (float)(width / mpp));
        }

        // Rails, lane markings, centre lines: only where a pixel is small enough to show them.
        if (ctx.Mpp > 0.6)
            return;
        foreach (var road in roads)
        {
            if (road.Class == "rail")
            {
                Stroke(canvas, road.Path, Palette.Rail, (float)(1.6 / mpp), [(float)(0.3 / mpp), (float)(0.4 / mpp)]);
                continue;
            }
            if (road.Class is "secondary" or "primary" or "trunk" or "motorway" or "tertiary")
                Stroke(canvas, road.Path, Palette.Marking, (float)(0.15 / mpp), [(float)(3 / mpp), (float)(6 / mpp)]);
        }
    }

    private static void DrawBuilding(SKCanvas canvas, SKPath path, ChunkContext ctx, double height, int seed)
    {
        var shadow = ShadowPixels(ctx, height);
        using (var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 80), IsAntialias = true })
        {
            canvas.Save();
            canvas.Translate(shadow);
            canvas.DrawPath(path, shadowPaint);
            canvas.Restore();
        }
        var roof = Palette.Roof(seed);
        using var paint = new SKPaint { Color = roof, IsAntialias = true };
        canvas.DrawPath(path, paint);
        Stroke(canvas, path, VehicleSprites.Shade(roof, 0.8f), (float)(0.4 / ctx.Mpp));
        Texture(canvas, path, ctx, 0.18f, 4, seed & 0xff);
    }

    private static SKPoint ShadowPixels(ChunkContext ctx, double height) =>
        new((float)(SunShadowPerMeter.X * height / ctx.Mpp), (float)(-SunShadowPerMeter.Y * height / ctx.Mpp));

    /// <summary>
    /// Buildings where OSM has none: on a global 26 m grid, a cell becomes a building when it sits
    /// 5-24 m back from a street, isn't on open land, and isn't too close to another road. Aligned
    /// with the nearest street. Drawn under the roads, which cover any overlap.
    /// </summary>
    private void DrawSyntheticBuildings(SKCanvas canvas, ChunkContext ctx, ChunkFeatures features)
    {
        const double cell = 26;
        var streets = features.Roads.Where(r => r.Class is "minor" or "tertiary" or "secondary" or "service" or "primary").ToList();
        if (streets.Count == 0)
            return;
        var index = new SegmentIndex(streets, 40);

        var gx0 = (int)Math.Floor((ctx.X0 - 30) / cell);
        var gx1 = (int)Math.Ceiling((ctx.X0 + ctx.Size + 30) / cell);
        var gy0 = (int)Math.Floor((ctx.Y1 - ctx.Size - 30) / cell);
        var gy1 = (int)Math.Ceiling((ctx.Y1 + 30) / cell);

        for (var gx = gx0; gx <= gx1; gx++)
        for (var gy = gy0; gy <= gy1; gy++)
        {
            var rng = new Random(HashInts(gx, gy, 17));
            if (rng.NextDouble() < 0.15)
                continue;
            var center = new Vec2((gx + 0.3 + rng.NextDouble() * 0.4) * cell, (gy + 0.3 + rng.NextDouble() * 0.4) * cell);
            var nearest = index.Nearest(center);
            if (nearest is null)
                continue;
            var (road, distance, direction) = nearest.Value;
            var setback = distance - road.WidthMeters / 2;
            if (setback < 7 || setback > 26)
                continue;
            var pixel = ctx.ToPixel(center);
            if (features.OpenLand.Any(p => p.Contains(pixel.X, pixel.Y)))
                continue;

            var frontage = 9 + rng.NextDouble() * 9;
            var depth = 9 + rng.NextDouble() * 8;
            var height = 6 + rng.NextDouble() * 20;

            var along = direction;
            var across = new Vec2(direction.Y, -direction.X);
            if (index.Nearest(center + along * (frontage / 2)) is { } a && a.Distance < a.Road.WidthMeters / 2 + 2)
                continue;
            var corners = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }
                .Select(c => ctx.ToPixel(center + along * (c.Item1 * frontage / 2) + across * (c.Item2 * depth / 2)))
                .ToArray();
            using var builder = new SKPathBuilder();
            builder.AddPoly(corners, close: true);
            using var path = builder.Detach();
            DrawBuilding(canvas, path, ctx, height, HashInts(gx, gy, 3));
        }
    }

    private static void DrawTrees(SKCanvas canvas, ChunkContext ctx, ChunkFeatures features)
    {
        foreach (var (path, cls, sub) in features.Landcover)
        {
            var spacing = (cls, sub) switch
            {
                ("wood", _) => 5.5,
                (_, "orchard") => 6.0,
                (_, "park" or "garden") => 11.0,
                (_, "scrub") => 9.0,
                _ => 0.0
            };
            if (spacing == 0)
                continue;
            var bounds = path.Bounds;
            var topLeft = ctx.ToLocal(new SKPoint(bounds.Left, bounds.Top));
            var bottomRight = ctx.ToLocal(new SKPoint(bounds.Right, bounds.Bottom));
            var gx0 = (int)Math.Floor(Math.Max(topLeft.X, ctx.X0) / spacing);
            var gx1 = (int)Math.Ceiling(Math.Min(bottomRight.X, ctx.X0 + ctx.Size) / spacing);
            var gy0 = (int)Math.Floor(Math.Max(bottomRight.Y, ctx.Y1 - ctx.Size) / spacing);
            var gy1 = (int)Math.Ceiling(Math.Min(topLeft.Y, ctx.Y1) / spacing);
            var orchard = sub == "orchard";

            using var crown = new SKPaint { IsAntialias = true };
            using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 70), IsAntialias = true };
            for (var gx = gx0; gx <= gx1; gx++)
            for (var gy = gy0; gy <= gy1; gy++)
            {
                var rng = new Random(HashInts(gx, gy, (int)(spacing * 10)));
                if (!orchard && rng.NextDouble() < (sub is "park" or "garden" ? 0.55 : 0.2))
                    continue;
                var jitter = orchard ? 0.1 : 0.8;
                var p = new Vec2((gx + 0.5 + (rng.NextDouble() - 0.5) * jitter) * spacing, (gy + 0.5 + (rng.NextDouble() - 0.5) * jitter) * spacing);
                var pixel = ctx.ToPixel(p);
                if (!path.Contains(pixel.X, pixel.Y))
                    continue;
                var radius = (float)((orchard ? 1.6 : 1.8 + rng.NextDouble() * 2.2) / ctx.Mpp);
                var shadow = ShadowPixels(ctx, orchard ? 3 : 6);
                canvas.DrawCircle(pixel.X + shadow.X, pixel.Y + shadow.Y, radius, shade);
                crown.Color = new SKColor((byte)(48 + rng.Next(25)), (byte)(78 + rng.Next(30)), (byte)(38 + rng.Next(18)));
                canvas.DrawCircle(pixel, radius, crown);
                crown.Color = new SKColor(255, 255, 255, 28);
                canvas.DrawCircle(pixel.X - radius * 0.3f, pixel.Y - radius * 0.3f, radius * 0.5f, crown);
            }
        }
    }

    /// <summary>Parked cars along the kerbs of streets. Each slot is owned by the tile its position
    /// falls in, so a road split across tiles doesn't get two sets of cars. Moving traffic isn't
    /// drawn here (these chunks are cached): it's <see cref="World.Traffic"/>, drawn per frame.</summary>
    private static void DrawTraffic(SKCanvas canvas, ChunkContext ctx, ChunkFeatures features, double density)
    {
        var shadow = ShadowPixels(ctx, 1);
        foreach (var road in features.Roads)
        {
            var parked = road.Class switch
            {
                "minor" => 0.32,
                "service" => 0.35,
                "tertiary" => 0.18,
                "secondary" => 0.06,
                _ => 0.0
            } * density;
            if (parked == 0)
                continue;

            var travelled = 0.0;
            for (var i = 0; i + 1 < road.Points.Count; i++)
            {
                var a = road.Points[i];
                var b = road.Points[i + 1];
                var segment = b - a;
                var length = segment.Length;
                if (length < 0.5)
                    continue;
                var along = segment * (1 / length);
                var right = new Vec2(along.Y, -along.X);
                for (var s = (6.5 - travelled % 6.5) % 6.5; s < length; s += 6.5)
                {
                    var at = a + along * s;
                    if (!InBox(at, road.OwnerTile) || !ctx.Near(at, 15))
                        continue;
                    foreach (var side in new[] { -1, 1 })
                    {
                        var rng = new Random(Hash(at) ^ side * 7919);
                        var roll = rng.NextDouble();
                        // Parked at the kerb, facing the traffic direction of that side.
                        if (roll >= parked)
                            continue;
                        var offset = road.WidthMeters / 2 - 1.1;
                        var position = at + right * (offset * side);
                        var direction = along * side;
                        // Compass-style angle (0 = up/north, clockwise) is the canvas rotation too.
                        var heading = Math.Atan2(direction.X, direction.Y) * 180 / Math.PI;
                        VehicleSprites.Draw(canvas, ctx.ToPixel(position), heading, 1 / ctx.Mpp, BackgroundVehicle(rng), shadow);
                    }
                }
                travelled += length;
            }
        }
    }

    private static VehicleLook BackgroundVehicle(Random rng)
    {
        var (kind, color) = VehicleSprites.Background(rng);
        return VehicleLook.Of(kind, VehicleSprites.Colors[color]);
    }

    private static bool InBox(Vec2 p, (Vec2 Min, Vec2 Max) box) =>
        p.X >= box.Min.X && p.X < box.Max.X && p.Y >= box.Min.Y && p.Y < box.Max.Y;

    private static double RoadWidth(string cls, string sub) => cls switch
    {
        "motorway" => 22,
        "trunk" => 18,
        "primary" => 14,
        "secondary" => 12,
        "tertiary" => 9,
        "minor" => 7,
        "service" => 4.5,
        "track" => 3,
        "path" => sub == "cycleway" ? 2.2 : 1.8,
        "rail" => 3.2,
        "pier" => 4,
        _ when cls.EndsWith("_construction") => 6,
        _ => 5
    };

    private static int Hash(Vec2 v) => HashInts((int)Math.Round(v.X * 2), (int)Math.Round(v.Y * 2), 0);

    private static int HashInts(int a, int b, int c)
    {
        unchecked
        {
            var h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)c * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            return (int)(h & 0x7fffffff);
        }
    }

    /// <summary>Nearest street segment lookup on a coarse grid.</summary>
    private sealed class SegmentIndex
    {
        private readonly double _cell;
        private readonly Dictionary<(int, int), List<(Road Road, Vec2 A, Vec2 B)>> _cells = new();

        public SegmentIndex(IEnumerable<Road> roads, double cell)
        {
            _cell = cell;
            foreach (var road in roads)
            for (var i = 0; i + 1 < road.Points.Count; i++)
            {
                var a = road.Points[i];
                var b = road.Points[i + 1];
                var steps = Math.Max(1, (int)Math.Ceiling((b - a).Length / cell));
                for (var s = 0; s <= steps; s++)
                {
                    var p = a + (b - a) * ((double)s / steps);
                    var key = ((int)Math.Floor(p.X / cell), (int)Math.Floor(p.Y / cell));
                    if (!_cells.TryGetValue(key, out var list))
                        _cells[key] = list = [];
                    if (list.Count == 0 || list[^1].A != a || list[^1].B != b)
                        list.Add((road, a, b));
                }
            }
        }

        public (Road Road, double Distance, Vec2 Direction)? Nearest(Vec2 p)
        {
            var cx = (int)Math.Floor(p.X / _cell);
            var cy = (int)Math.Floor(p.Y / _cell);
            (Road, double, Vec2)? best = null;
            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (!_cells.TryGetValue((cx + dx, cy + dy), out var list))
                    continue;
                foreach (var (road, a, b) in list)
                {
                    var ab = b - a;
                    var len2 = ab.X * ab.X + ab.Y * ab.Y;
                    if (len2 < 1e-6)
                        continue;
                    var t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
                    var d = (a + ab * t - p).Length;
                    if (best is null || d < best.Value.Item2)
                        best = (road, d, ab * (1 / Math.Sqrt(len2)));
                }
            }
            return best;
        }
    }
}

/// <summary>Colours of an Israeli coastal-plain town from the air.</summary>
internal static class Palette
{
    public static readonly SKColor Soil = new(190, 172, 136);
    public static readonly SKColor Water = new(46, 86, 110);
    public static readonly SKColor Pool = new(78, 168, 196);
    public static readonly SKColor Asphalt = new(74, 76, 79);
    public static readonly SKColor Curb = new(170, 166, 158);
    public static readonly SKColor Marking = new(232, 232, 226);
    public static readonly SKColor Dirt = new(168, 146, 108);
    public static readonly SKColor Footpath = new(196, 190, 178);
    public static readonly SKColor Ballast = new(116, 108, 98);
    public static readonly SKColor Rail = new(58, 54, 50);

    public static SKColor Landuse(string cls) => cls switch
    {
        "residential" or "neighbourhood" => new(176, 170, 158),
        "industrial" or "railway" => new(162, 160, 154),
        "commercial" or "retail" => new(170, 166, 160),
        "school" or "university" or "college" or "library" or "kindergarten" or "hospital" => new(184, 176, 160),
        "pitch" => new(92, 138, 78),
        "playground" => new(192, 164, 124),
        "cemetery" => new(150, 150, 128),
        "military" => new(170, 164, 140),
        "pedestrian" => new(192, 186, 176),
        "aeroway" => new(150, 150, 146),
        _ => new(180, 170, 150)
    };

    public static SKColor Landcover(string cls, string sub) => (cls, sub) switch
    {
        (_, "orchard") => new(128, 128, 84),
        ("farmland", _) => new(164, 150, 98),
        ("grass", "scrub") => new(140, 138, 96),
        ("grass", _) => new(112, 134, 76),
        ("wood", _) => new(84, 104, 60),
        ("sand", _) => new(222, 204, 164),
        ("wetland", _) => new(98, 120, 96),
        ("rock", _) => new(160, 154, 142),
        _ => new(150, 146, 110)
    };

    /// <summary>Flat roofs: mostly light concrete, some white, some sun-faded, a few tiled.</summary>
    public static SKColor Roof(int seed) => (seed % 10) switch
    {
        0 or 1 => new(222, 220, 214),
        2 => new(200, 196, 188),
        3 => new(168, 164, 158),
        4 => new(178, 92, 68),
        5 => new(188, 178, 160),
        6 => new(140, 138, 134),
        7 => new(208, 200, 184),
        _ => new(194, 190, 182)
    };
}
