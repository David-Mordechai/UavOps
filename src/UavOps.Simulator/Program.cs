using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.StaticFiles;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;
using UavOps.Simulator;
using UavOps.Simulator.Camera;
using UavOps.Simulator.Imagery;
using UavOps.Simulator.Map;

var builder = WebApplication.CreateBuilder(args);

var simOptions = builder.Configuration.GetSection(SimOptions.SectionName).Get<SimOptions>() ?? new SimOptions();
var scenarioOptions = builder.Configuration.GetSection(ScenarioOptions.SectionName).Get<ScenarioOptions>() ?? new ScenarioOptions();

builder.Services.AddSingleton(simOptions);
builder.Services.AddSingleton(new ScenarioStore(scenarioOptions, simOptions.VehiclePhotos));
// Who plays the onboard agent's eyes: the onboard detection service (a vision model over the
// camera frames), or the tag-matching fallback that needs no model. The client is registered
// either way and stays idle under Simulated.
builder.Services.AddHttpClient(nameof(OnboardDetectorClient), c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(sp => new OnboardDetectorClient(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OnboardDetectorClient)),
    simOptions, sp.GetRequiredService<SurveyFrameBuffer>(), sp.GetRequiredService<ILogger<OnboardDetectorClient>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnboardDetectorClient>());
if (simOptions.UsesOnboardDetector)
    builder.Services.AddSingleton<IOnboardDetector>(sp => sp.GetRequiredService<OnboardDetectorClient>());
else
    builder.Services.AddSingleton<IOnboardDetector, SimulatedDetector>();

// The payload camera: real aerial photos where there are any (imagery/, scripts/fetch-imagery.ps1),
// elsewhere the ground drawn from the same offline map the page shows (plain terrain when that
// hasn't been built); the scenario objects on it, as real vehicle photos where configured.
builder.Services.AddSingleton(sp => new ImageryLayer(
    Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "imagery"), sp.GetRequiredService<ILogger<ImageryLayer>>()));
builder.Services.AddSingleton(sp => new VehiclePhotos(sp.GetRequiredService<ImageryLayer>(), simOptions.VehiclePhotos));
builder.Services.AddSingleton(sp =>
{
    var path = Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().WebRootPath, "map", "israel.pmtiles");
    var tiles = File.Exists(path) ? new PmTilesReader(path) : null;
    UavOps.Agent.Contracts.KnownPoints.TryResolve("home", out var lat, out var lng);
    return new GroundRenderer(tiles, new GeoPoint(lat, lng), sp.GetRequiredService<ImageryLayer>(), simOptions.TrafficDensity);
});
builder.Services.AddSingleton<CameraRenderer>();
builder.Services.AddSingleton<SurveyFrameBuffer>();
builder.Services.AddSingleton<SurveyCameraWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SurveyCameraWorker>());
builder.Services.AddSingleton<SimFleet>();
builder.Services.AddSingleton<SimulatorCommandHandler>();
builder.Services.AddSingleton<FleetConnectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FleetConnectionService>());
builder.Services.AddSingleton<FlightTickService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FlightTickService>());
// Read-only here: the same zones McpMoav plans routes over, drawn on the map.
builder.Services.AddSingleton<IAoiZoneStore>(new SqliteAoiZoneStore(MissionOptions.ResolveDatabasePath(simOptions.AoiDatabasePath)));
builder.Services.AddSignalR();

var app = builder.Build();

app.UseDefaultFiles();

// The offline map's tile archive and glyph ranges have extensions static files doesn't know,
// and would otherwise 404. Range requests (how MapLibre reads a .pmtiles file) are on by default.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".pmtiles"] = "application/octet-stream";
contentTypes.Mappings[".pbf"] = "application/x-protobuf";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    // The page's own files change during development; without this a browser keeps using its
    // cached copy after an update. (Tiles and vendor scripts are fine to cache.)
    OnPrepareResponse = ctx =>
    {
        if (Path.GetExtension(ctx.File.Name) is ".html" or ".js" or ".css" && !ctx.Context.Request.Path.StartsWithSegments("/vendor"))
            ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});

app.MapHub<SimHub>("/simHub");

app.MapGet("/api/state", (FlightTickService tick) => Results.Json(tick.Snapshot()));

app.MapGet("/api/zones", async (IAoiZoneStore zones, CancellationToken ct) =>
{
    var features = new JsonArray();
    foreach (var zone in await zones.ListAsync(ct))
    {
        features.Add(new JsonObject
        {
            ["type"] = "Feature",
            ["properties"] = new JsonObject { ["name"] = zone.Name },
            ["geometry"] = JsonNode.Parse(GeoJsonPolygon.Write(zone.Vertices))
        });
    }
    return Results.Json(new JsonObject { ["type"] = "FeatureCollection", ["features"] = features });
});

app.MapGet("/api/objects", (ScenarioStore scenario) => scenario.All());

app.MapPost("/api/objects", (NewObject body, ScenarioStore scenario) =>
    string.IsNullOrWhiteSpace(body.Label)
        ? Results.BadRequest("A label is required, e.g. 'white van'.")
        : Results.Ok(scenario.Add(body.Label, body.Tags, body.Lat, body.Lng, kind: body.Kind, color: body.Color, headingDeg: body.HeadingDeg)));

// What a placed object looks like from straight above, facing up: its real vehicle photo, or the
// drawn vehicle. The page shows it on the map at true size (X-Meters-Per-Pixel) and heading.
var spriteCache = new LruCache<string, byte[]>(64);
app.MapGet("/api/objects/{id}/sprite.png", (string id, ScenarioStore scenario, VehiclePhotos photos, HttpContext http) =>
{
    if (scenario.All().FirstOrDefault(o => o.Id == id) is not { } obj)
        return Results.NotFound();
    var key = $"{obj.Photo}|{obj.Kind}|{obj.Color}";
    double metersPerPixel;
    if (obj.Photo is not null && photos.Get(obj.Photo) is { } photo)
    {
        metersPerPixel = photo.MetersPerPixel;
        if (!spriteCache.TryGet(key, out var png))
        {
            using var data = photo.Image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            spriteCache.Add(key, png = data.ToArray());
        }
        http.Response.Headers["X-Meters-Per-Pixel"] = metersPerPixel.ToString("R", CultureInfo.InvariantCulture);
        return Results.File(png, "image/png");
    }

    const double pixelsPerMeter = 50;
    metersPerPixel = 1 / pixelsPerMeter;
    if (!spriteCache.TryGet(key, out var drawn))
    {
        var kind = Enum.TryParse<VehicleKind>(obj.Kind, ignoreCase: true, out var k) ? k : VehicleKind.Car;
        var look = VehicleLook.Of(kind, VehicleSprites.Colors.GetValueOrDefault(obj.Color, VehicleSprites.Colors["grey"]));
        var w = (int)Math.Ceiling((look.WidthMeters + 1) * pixelsPerMeter);
        var h = (int)Math.Ceiling((look.LengthMeters + 1) * pixelsPerMeter);
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(w, h));
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        VehicleSprites.Draw(surface.Canvas, new SkiaSharp.SKPoint(w / 2f, h / 2f), 0, pixelsPerMeter, look, new SkiaSharp.SKPoint(0, 0));
        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        spriteCache.Add(key, drawn = data.ToArray());
    }
    http.Response.Headers["X-Meters-Per-Pixel"] = metersPerPixel.ToString("R", CultureInfo.InvariantCulture);
    return Results.File(drawn, "image/png");
});

app.MapDelete("/api/objects/{id}", (string id, ScenarioStore scenario) =>
    scenario.Remove(id) ? Results.NoContent() : Results.NotFound());

app.MapPost("/api/reset", (SimFleet fleet) =>
{
    fleet.Reset();
    return Results.NoContent();
});

app.MapPost("/api/timescale", (TimeScaleBody body, SimOptions options) =>
{
    options.TimeScale = Math.Clamp(body.Value, 0, 100);
    return Results.Ok(new { timeScale = options.TimeScale });
});

// The real aerial photos as map tiles (transparent PNG, web mercator), for the page to lay over
// the base map. 204 where there's no photo.
var imageryTiles = new LruCache<(int Z, int X, int Y), byte[]?>(512);
app.MapGet("/api/imagery/{z:int}/{x:int}/{y:int}.png", (int z, int x, int y, ImageryLayer imagery) =>
{
    if (z < 12 || z > 22 || !imagery.Ground.Any())
        return Results.NoContent();
    if (!imageryTiles.TryGet((z, x, y), out var png))
    {
        var (north, west) = TileMath.ToGeo(z, x, y, 0, 0, 256);
        var (south, east) = TileMath.ToGeo(z, x, y, 256, 256, 256);
        png = null;
        if (imagery.Ground.Any(i => i.Bounds.SouthWest.Lat < north && i.Bounds.NorthEast.Lat > south && i.Bounds.SouthWest.Lng < east && i.Bounds.NorthEast.Lng > west))
        {
            using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(256, 256));
            surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
            var metersPerPixel = (east - west) * 111_195 * Math.Cos(north * Math.PI / 180) / 256;
            imagery.Draw(surface.Canvas, 256, 256, (px, py) =>
            {
                var (lat, lng) = TileMath.ToGeo(z, x, y, px, py, 256);
                return new GeoPoint(lat, lng);
            }, metersPerPixel);
            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            png = data.ToArray();
        }
        imageryTiles.Add((z, x, y), png);
    }
    return png is null ? Results.NoContent() : Results.File(png, "image/png");
});

// The photos' extents and credits, for the page.
app.MapGet("/api/imagery", (ImageryLayer imagery) => imagery.Ground.Select(i => new
{
    i.Name, i.Title, i.Attribution, i.License,
    bounds = new[] { i.Bounds.SouthWest.Lng, i.Bounds.SouthWest.Lat, i.Bounds.NorthEast.Lng, i.Bounds.NorthEast.Lat }
}));

// Tells the page whether the offline tile archive has been built (scripts/build-offline-map.ps1);
// without it the page draws a plain grid instead.
app.MapGet("/api/map", (IWebHostEnvironment env) =>
    Results.Ok(new { offlineTiles = env.WebRootFileProvider.GetFileInfo("map/israel.pmtiles").Exists }));

// ----- The payload camera -----

// Live view: MJPEG (what an <img> shows natively), rendered only while someone watches, with a
// box around everything this UAV's onboard agent has found in its current search.
app.MapGet("/api/uavs/{tail}/camera.mjpg", async (string tail, HttpContext http, SimFleet fleet, CameraRenderer camera,
    SimOptions options, OnboardDetectorClient onboard) =>
{
    if (fleet.CameraNow(tail, DateTime.UtcNow) is null)
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    const string boundary = "frame";
    http.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
    http.Response.Headers.CacheControl = "no-store";
    var interval = TimeSpan.FromSeconds(1 / Math.Clamp(options.LiveCameraFps, 0.5, 15));
    var ct = http.RequestAborted;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            var frame = fleet.CameraNow(tail, started)!;
            var missionId = fleet.View().Uavs.FirstOrDefault(u => u.TailNumber == tail)?.MissionId;
            var overlays = options.UsesOnboardDetector
                ? onboard.OverlaysFor(tail, missionId)
                : fleet.DetectionsOf(tail).Select(d => new CameraOverlay(new GeoPoint(d.Lat, d.Lng), 6, 3, $"{d.Label} {d.Confidence:0.00}")).ToList();
            var jpeg = await Task.Run(() => camera.RenderJpeg(frame, tail, overlays), ct);
            await http.Response.WriteAsync($"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n", ct);
            await http.Response.Body.WriteAsync(jpeg, ct);
            await http.Response.WriteAsync("\r\n", ct);
            await http.Response.Body.FlushAsync(ct);
            var wait = interval - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct);
        }
    }
    catch (OperationCanceledException)
    {
        // The viewer went away.
    }
});

// Survey frames for the onboard detector, in order: the next one after ?after=, waiting briefly
// for it; telemetry travels in X-Frame-* headers (the dev stand-in for MISB KLV metadata).
app.MapGet("/api/uavs/{tail}/frames/next", async (string tail, long after, int? waitMs, SurveyFrameBuffer frames, HttpContext http) =>
{
    var frame = await frames.NextAfterAsync(tail, after, TimeSpan.FromMilliseconds(Math.Clamp(waitMs ?? 10000, 0, 30000)), http.RequestAborted);
    return frame is null ? Results.NoContent() : FrameResult(frame, http);
});

app.MapGet("/api/uavs/{tail}/frames/{seq:long}.jpg", (string tail, long seq, SurveyFrameBuffer frames, HttpContext http) =>
    frames.Get(tail, seq) is { } frame ? FrameResult(frame, http) : Results.NotFound());

// The payload's zoom: slew to a ground point and take a close-up showing widthMeters of ground
// (square, ?pixels= across, default ZoomPixels) from where the UAV is now. What the onboard detector uses to look at
// a candidate properly instead of cropping the survey frame.
app.MapGet("/api/uavs/{tail}/zoom", async (string tail, double lat, double lng, double widthMeters, int? pixels,
    SimFleet fleet, CameraRenderer camera, SimOptions options, HttpContext http) =>
{
    if (fleet.CameraNow(tail, DateTime.UtcNow, nadir: true) is not { } now)
        return Results.NotFound();
    var target = new GeoPoint(lat, lng);
    var range = GeoProjection.DistanceMeters(new GeoPoint(now.Lat, now.Lng), target);
    if (range > options.MaxZoomRangeMeters)
        return Results.Conflict($"{tail} is {range:F0} m from that point; the payload reaches {options.MaxZoomRangeMeters:F0} m.");
    var width = Math.Clamp(widthMeters, 2, 200);
    var altitudeMeters = Math.Max(now.AltitudeFt, 1) * 0.3048;
    var hfov = 2 * Math.Atan(width / 2 / altitudeMeters) * 180 / Math.PI;
    var size = Math.Clamp(pixels ?? options.ZoomPixels, 128, 1024);
    var zoom = now with { Lat = lat, Lng = lng, HFovDeg = hfov, Width = size, Height = size };
    var jpeg = await Task.Run(() => camera.RenderJpeg(zoom, tail + " ZOOM"));
    return FrameResult(new SurveyFrame(zoom, jpeg), http);
});

// The onboard detector's callback: something found.
app.MapPost("/api/onboard/detections", (OnboardDetection detection, OnboardDetectorClient onboard, SimOptions options) =>
{
    if (!options.UsesOnboardDetector)
        return Results.Conflict("Simulator:Detector is Simulated; onboard detections aren't accepted.");
    onboard.Receive(detection);
    return Results.Accepted();
});

app.Run();

static IResult FrameResult(SurveyFrame frame, HttpContext http)
{
    var t = frame.Telemetry;
    var h = http.Response.Headers;
    h[FrameHeaders.Seq] = t.Seq.ToString(CultureInfo.InvariantCulture);
    h[FrameHeaders.CapturedAtUtc] = t.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture);
    h[FrameHeaders.Lat] = t.Lat.ToString("R", CultureInfo.InvariantCulture);
    h[FrameHeaders.Lng] = t.Lng.ToString("R", CultureInfo.InvariantCulture);
    h[FrameHeaders.AltitudeFt] = t.AltitudeFt.ToString("R", CultureInfo.InvariantCulture);
    h[FrameHeaders.HeadingDeg] = t.HeadingDeg.ToString("R", CultureInfo.InvariantCulture);
    h[FrameHeaders.HFovDeg] = t.HFovDeg.ToString("R", CultureInfo.InvariantCulture);
    h[FrameHeaders.Width] = t.Width.ToString(CultureInfo.InvariantCulture);
    h[FrameHeaders.Height] = t.Height.ToString(CultureInfo.InvariantCulture);
    if (t.MissionId is not null)
        h[FrameHeaders.MissionId] = t.MissionId;
    return Results.File(frame.Jpeg, "image/jpeg");
}

internal sealed record NewObject(string Label, List<string>? Tags, double Lat, double Lng, string? Kind, string? Color, double? HeadingDeg);
internal sealed record TimeScaleBody(double Value);
