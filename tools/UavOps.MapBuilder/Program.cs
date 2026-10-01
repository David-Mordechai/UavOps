using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SkiaSharp;
using UavOps.MapBuilder;
using UavOps.Simulator.Imagery;
using UavOps.Simulator.Map;

// Builds UavOps.Simulator's offline satellite basemap and terrain, once (scripts/build-satellite.ps1):
//   basemap.mbtiles - Sentinel-2 L2A true colour (10 m), JPEG web tiles z8..z14
//   terrain.mbtiles - Copernicus DEM GLO-30 (30 m), Terrarium-encoded PNG web tiles z8..z12
// Both sources are free and open (Copernicus); only the internal tiles covering the bounds are
// downloaded (HTTP range requests), cached under --cache so a rerun downloads nothing.

var options = Options.Parse(args);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("UavOps.MapBuilder/1.0 (+offline simulator basemap)");
Directory.CreateDirectory(options.Output);

if (options.Basemap)
    await Basemap.BuildAsync(http, options);
if (options.Terrain)
    Terrain.Build(http, options);
Console.WriteLine("Done.");

internal sealed record Options(double West, double South, double East, double North, string Output, string Cache,
    string From, string To, int MinZoom, int MaxZoom, int TerrainMaxZoom, bool Basemap, bool Terrain)
{
    public static Options Parse(string[] args)
    {
        string Arg(string name, string fallback)
        {
            var i = Array.IndexOf(args, "--" + name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
        var b = Arg("bounds", "34.20,29.45,35.95,33.35").Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var only = Arg("only", "all");
        return new Options(b[0], b[1], b[2], b[3], Arg("out", "."), Arg("cache", ".map-cache"),
            Arg("from", "2025-06-01"), Arg("to", "2025-09-15"),
            int.Parse(Arg("minzoom", "8")), int.Parse(Arg("maxzoom", "14")), int.Parse(Arg("terrain-maxzoom", "12")),
            only is "all" or "basemap", only is "all" or "terrain");
    }

    public IEnumerable<(int X, int Y)> Tiles(int z)
    {
        var (x0, y0) = TileMath.TileAt(North, West, z);
        var (x1, y1) = TileMath.TileAt(South, East, z);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
            yield return (x, y);
    }
}

internal static class Basemap
{
    private sealed record Scene(string Id, string Mgrs, string Date, double Cloud, string Url);

    public static async Task BuildAsync(HttpClient http, Options o)
    {
        var scenes = await FindScenesAsync(http, o);
        Console.WriteLine($"Sentinel-2: {scenes.Count} scene(s): {string.Join(", ", scenes.Select(s => $"{s.Mgrs} {s.Date} ({s.Cloud:F2}% cloud)"))}");
        var readers = scenes.Select(s =>
        {
            var zone = int.Parse(new string(s.Mgrs.TakeWhile(char.IsDigit).ToArray()), CultureInfo.InvariantCulture);
            return (Reader: new CogReader(http, s.Url, Path.Combine(o.Cache, "sentinel")), Zone: zone);
        }).ToList();

        var dates = string.Join(", ", scenes.Select(s => s.Date).Distinct());
        using var mb = new MbTilesWriter(Path.Combine(o.Output, "basemap.mbtiles"), new Dictionary<string, string>
        {
            ["name"] = "Sentinel-2 true colour",
            ["format"] = "jpg",
            ["type"] = "baselayer",
            ["minzoom"] = o.MinZoom.ToString(CultureInfo.InvariantCulture),
            ["maxzoom"] = o.MaxZoom.ToString(CultureInfo.InvariantCulture),
            ["bounds"] = string.Create(CultureInfo.InvariantCulture, $"{o.West},{o.South},{o.East},{o.North}"),
            ["attribution"] = $"Contains modified Copernicus Sentinel data {scenes.Select(s => s.Date[..4]).Distinct().First()} ({dates})"
        });

        // Full resolution from the source, in parallel; then each lower zoom from the one above.
        var z = o.MaxZoom;
        var tiles = o.Tiles(z).ToList();
        var done = 0;
        Parallel.ForEach(tiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, t =>
        {
            if (RenderFromSource(readers, z, t.X, t.Y) is { } jpeg)
                mb.Add(z, t.X, t.Y, jpeg);
            if (Interlocked.Increment(ref done) % 100 == 0)
                Console.WriteLine($"  z{z}: {done}/{tiles.Count}");
        });
        Console.WriteLine($"  z{z}: {mb.Count} tiles");
        for (z = o.MaxZoom - 1; z >= o.MinZoom; z--)
        {
            var before = mb.Count;
            foreach (var (x, y) in o.Tiles(z))
                if (Downsample(mb, z, x, y) is { } jpeg)
                    mb.Add(z, x, y, jpeg);
            Console.WriteLine($"  z{z}: {mb.Count - before} tiles");
        }
    }

    /// <summary>The least cloudy summer scene per MGRS tile, preferring one date for all tiles (no
    /// colour seams between acquisitions); ordered so the preferred scenes are sampled first.</summary>
    private static async Task<List<Scene>> FindScenesAsync(HttpClient http, Options o)
    {
        // Only the fields used (full items are large: a 1000-item page got a 502 from the
        // catalogue), 100 per page, following its "next" links.
        var fields = new { include = new[] { "id", "properties.eo:cloud_cover", "properties.s2:mgrs_tile", "properties.datetime", "assets.visual.href" }, exclude = new[] { "geometry", "links" } };
        JsonNode? body = JsonSerializer.SerializeToNode(new
        {
            collections = new[] { "sentinel-2-l2a" },
            bbox = new[] { o.West, o.South, o.East, o.North },
            datetime = $"{o.From}T00:00:00Z/{o.To}T23:59:59Z",
            query = new Dictionary<string, object> { ["eo:cloud_cover"] = new { lt = 2 } },
            fields,
            limit = 100
        });
        var all = new List<Scene>();
        for (var page = 0; body is not null && page < 50; page++)
        {
            using var response = await http.PostAsync("https://earth-search.aws.element84.com/v1/search",
                new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            all.AddRange(doc.RootElement.GetProperty("features").EnumerateArray().Select(f =>
            {
                var p = f.GetProperty("properties");
                var mgrs = p.TryGetProperty("s2:mgrs_tile", out var m) ? m.GetString()! : f.GetProperty("id").GetString()!.Split('_')[1];
                return new Scene(f.GetProperty("id").GetString()!, mgrs, p.GetProperty("datetime").GetString()![..10],
                    p.GetProperty("eo:cloud_cover").GetDouble(), f.GetProperty("assets").GetProperty("visual").GetProperty("href").GetString()!);
            }));
            body = null;
            if (doc.RootElement.TryGetProperty("links", out var links))
                foreach (var link in links.EnumerateArray())
                    if (link.GetProperty("rel").GetString() == "next" && link.TryGetProperty("body", out var next))
                    {
                        body = JsonNode.Parse(next.GetRawText());
                        body!["fields"] = JsonSerializer.SerializeToNode(fields);
                    }
        }
        Console.WriteLine($"Sentinel-2 catalogue: {all.Count} scenes over {all.Select(s => s.Mgrs).Distinct().Count()} tiles.");
        if (all.Count == 0)
            throw new InvalidOperationException("No Sentinel-2 scenes found for the bounds and dates.");

        var tileIds = all.Select(s => s.Mgrs).Distinct().ToList();
        var bestDate = all.GroupBy(s => s.Date)
            .OrderByDescending(g => g.Select(s => s.Mgrs).Distinct().Count())
            .ThenBy(g => g.Average(s => s.Cloud))
            .First().Key;
        var chosen = new List<Scene>();
        foreach (var id in tileIds)
        {
            var ofTile = all.Where(s => s.Mgrs == id).ToList();
            chosen.Add(ofTile.FirstOrDefault(s => s.Date == bestDate) ?? ofTile.OrderBy(s => s.Cloud).First());
        }
        return chosen.OrderBy(s => s.Date == bestDate ? 0 : 1).ThenBy(s => s.Cloud).ToList();
    }

    private static byte[]? RenderFromSource(List<(CogReader Reader, int Zone)> readers, int z, int x, int y)
    {
        const int size = 256, grid = 16;
        // UTM of a (grid+1)² lattice over the tile, interpolated in between: over one z14 tile the
        // error is far below a Sentinel pixel.
        var lattice = new (double E, double N)[readers.Count][,];
        for (var r = 0; r < readers.Count; r++)
        {
            lattice[r] = new (double, double)[grid + 1, grid + 1];
            for (var j = 0; j <= grid; j++)
            for (var i = 0; i <= grid; i++)
            {
                var (lat, lng) = TileMath.ToGeo(z, x, y, i * size / (double)grid, j * size / (double)grid, size);
                lattice[r][i, j] = Utm.FromLatLng(lat, lng, readers[r].Zone, north: true);
            }
        }

        var pixels = new byte[size * size * 4];
        var any = false;
        var full = true;
        for (var py = 0; py < size; py++)
        for (var px = 0; px < size; px++)
        {
            var gx = (px + 0.5) / size * grid;
            var gy = (py + 0.5) / size * grid;
            var i0 = Math.Min((int)gx, grid - 1);
            var j0 = Math.Min((int)gy, grid - 1);
            var fx = gx - i0;
            var fy = gy - j0;
            for (var r = 0; r < readers.Count; r++)
            {
                var l = lattice[r];
                var e = Lerp(Lerp(l[i0, j0].E, l[i0 + 1, j0].E, fx), Lerp(l[i0, j0 + 1].E, l[i0 + 1, j0 + 1].E, fx), fy);
                var n = Lerp(Lerp(l[i0, j0].N, l[i0 + 1, j0].N, fx), Lerp(l[i0, j0 + 1].N, l[i0 + 1, j0 + 1].N, fx), fy);
                if (SampleRgb(readers[r].Reader, e, n) is not { } rgb)
                    continue;
                var o = (py * size + px) * 4;
                pixels[o] = rgb.R;
                pixels[o + 1] = rgb.G;
                pixels[o + 2] = rgb.B;
                pixels[o + 3] = 255;
                any = true;
                break;
            }
            if (pixels[(py * size + px) * 4 + 3] == 0)
                full = false;
        }
        return any ? Encode(pixels, size, full) : null;
    }

    /// <summary>Bilinear RGB at a UTM point; null outside the scene or where it has no data (0,0,0).</summary>
    private static (byte R, byte G, byte B)? SampleRgb(CogReader reader, double e, double n)
    {
        var col = (e - reader.OriginX) / reader.PixelX - 0.5;
        var row = (reader.OriginY - n) / reader.PixelY - 0.5;
        if (col < 0 || row < 0 || col >= reader.Width - 1 || row >= reader.Height - 1)
            return null;
        var c0 = (int)col;
        var r0 = (int)row;
        var fx = col - c0;
        var fy = row - r0;
        double rr = 0, gg = 0, bb = 0;
        for (var k = 0; k < 4; k++)
        {
            var c = c0 + (k & 1);
            var r = r0 + (k >> 1);
            if (Pixel(reader, c, r) is not { } p)
                return null;
            var w = ((k & 1) == 1 ? fx : 1 - fx) * ((k >> 1) == 1 ? fy : 1 - fy);
            rr += p.R * w;
            gg += p.G * w;
            bb += p.B * w;
        }
        return ((byte)Math.Round(rr), (byte)Math.Round(gg), (byte)Math.Round(bb));
    }

    private static (byte R, byte G, byte B)? Pixel(CogReader reader, int col, int row)
    {
        var tile = reader.Tile(col / reader.TileWidth, row / reader.TileHeight);
        if (tile is null)
            return null;
        var o = ((row % reader.TileHeight) * reader.TileWidth + col % reader.TileWidth) * reader.Samples;
        var (r, g, b) = (tile[o], tile[o + 1], tile[o + 2]);
        return r == 0 && g == 0 && b == 0 ? null : (r, g, b);
    }

    private static byte[]? Downsample(MbTilesWriter mb, int z, int x, int y)
    {
        using var surface = SKSurface.Create(new SKImageInfo(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var any = false;
        var full = true;
        using var paint = new SKPaint();
        for (var k = 0; k < 4; k++)
        {
            var (cx, cy) = (2 * x + (k & 1), 2 * y + (k >> 1));
            if (mb.Get(z + 1, cx, cy) is not { } child)
            {
                full = false;
                continue;
            }
            if (IsPng(child))
                full = false;
            using var image = SKImage.FromEncodedData(child);
            surface.Canvas.DrawImage(image, SKRect.Create(128 * (k & 1), 128 * (k >> 1), 128, 128),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            any = true;
        }
        if (!any)
            return null;
        using var snapshot = surface.Snapshot();
        using var data = full ? snapshot.Encode(SKEncodedImageFormat.Jpeg, 85) : snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>JPEG where the tile is all imagery; PNG, transparent where there's none, for tiles on
    /// the edge of the area (black there showed as bands beside the country when zoomed out).</summary>
    private static byte[] Encode(byte[] rgba, int size, bool full)
    {
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bitmap.GetPixels(), rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = full ? image.Encode(SKEncodedImageFormat.Jpeg, 85) : image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static bool IsPng(byte[] data) => data.Length > 4 && data[0] == 0x89 && data[1] == (byte)'P';

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}

internal static class Terrain
{
    public static void Build(HttpClient http, Options o)
    {
        var readers = new Dictionary<(int Lat, int Lng), CogReader>();
        for (var lat = (int)Math.Floor(o.South); lat <= (int)Math.Floor(o.North); lat++)
        for (var lng = (int)Math.Floor(o.West); lng <= (int)Math.Floor(o.East); lng++)
        {
            var name = string.Create(CultureInfo.InvariantCulture, $"Copernicus_DSM_COG_10_N{lat:00}_00_E{lng:000}_00_DEM");
            try
            {
                readers[(lat, lng)] = new CogReader(http, $"https://copernicus-dem-30m.s3.amazonaws.com/{name}/{name}.tif", Path.Combine(o.Cache, "dem"));
                Console.WriteLine($"DEM {name}");
            }
            catch (FileNotFoundException)
            {
                // Copernicus publishes no tile for a square that is all sea: it's sea level.
                Console.WriteLine($"DEM {name}: none (sea)");
            }
        }

        using var mb = new MbTilesWriter(Path.Combine(o.Output, "terrain.mbtiles"), new Dictionary<string, string>
        {
            ["name"] = "Copernicus DEM GLO-30 (Terrarium)",
            ["format"] = "png",
            ["encoding"] = "terrarium",
            ["minzoom"] = o.MinZoom.ToString(CultureInfo.InvariantCulture),
            ["maxzoom"] = o.TerrainMaxZoom.ToString(CultureInfo.InvariantCulture),
            ["bounds"] = string.Create(CultureInfo.InvariantCulture, $"{o.West},{o.South},{o.East},{o.North}"),
            ["attribution"] = "Copernicus DEM GLO-30 © DLR e.V. 2010-2014 and © Airbus Defence and Space GmbH 2014-2018, provided under COPERNICUS by the European Union and ESA"
        });

        for (var z = o.MinZoom; z <= o.TerrainMaxZoom; z++)
        {
            var tiles = o.Tiles(z).ToList();
            Parallel.ForEach(tiles, t => mb.Add(z, t.X, t.Y, RenderTile(readers, z, t.X, t.Y)));
            Console.WriteLine($"  terrain z{z}: {tiles.Count} tiles");
        }
    }

    private static byte[] RenderTile(Dictionary<(int, int), CogReader> readers, int z, int x, int y)
    {
        const int size = 256;
        using var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        for (var py = 0; py < size; py++)
        for (var px = 0; px < size; px++)
        {
            var (lat, lng) = TileMath.ToGeo(z, x, y, px + 0.5, py + 0.5, size);
            var elevation = Elevation(readers, lat, lng);
            // Terrarium: (R × 256 + G + B / 256) − 32768 metres.
            var v = elevation + 32768;
            var r = (byte)Math.Clamp(Math.Floor(v / 256), 0, 255);
            var g = (byte)Math.Clamp(Math.Floor(v) % 256, 0, 255);
            var b = (byte)Math.Clamp(Math.Floor((v - Math.Floor(v)) * 256), 0, 255);
            bitmap.SetPixel(px, py, new SKColor(r, g, b, 255));
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Bilinear elevation (m); 0 where there's no DEM (the sea).</summary>
    private static double Elevation(Dictionary<(int, int), CogReader> readers, double lat, double lng)
    {
        if (!readers.TryGetValue(((int)Math.Floor(lat), (int)Math.Floor(lng)), out var reader))
            return 0;
        var col = Math.Clamp((lng - reader.OriginX) / reader.PixelX - 0.5, 0, reader.Width - 1.001);
        var row = Math.Clamp((reader.OriginY - lat) / reader.PixelY - 0.5, 0, reader.Height - 1.001);
        var c0 = (int)col;
        var r0 = (int)row;
        var fx = col - c0;
        var fy = row - r0;
        double Value(int c, int r)
        {
            var tile = reader.Tile(c / reader.TileWidth, r / reader.TileHeight);
            if (tile is null)
                return 0;
            var v = BitConverter.ToSingle(tile, ((r % reader.TileHeight) * reader.TileWidth + c % reader.TileWidth) * 4);
            return float.IsFinite(v) && v > -1000 ? v : 0;
        }
        return (Value(c0, r0) * (1 - fx) + Value(c0 + 1, r0) * fx) * (1 - fy) + (Value(c0, r0 + 1) * (1 - fx) + Value(c0 + 1, r0 + 1) * fx) * fy;
    }
}
