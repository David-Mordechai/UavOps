using System.Globalization;
using SkiaSharp;
using UavOps.Agent.Mission;
using UavOps.Onboard.Contracts;

namespace UavOps.Simulator.Camera;

/// <summary>A box drawn onto the live view around something the detector found.</summary>
public sealed record CameraOverlay(GeoPoint Position, double WidthMeters, double HeightMeters, string Text);

/// <summary>
/// The payload camera: renders what a UAV sees from where it is - the ground
/// (<see cref="GroundRenderer"/>) and the scenario objects on it, rotated so the image top is the
/// heading, at the scale its altitude and field of view give (<see cref="CameraModel"/>), then
/// made to look like a sensor: slight blur, grain, haze and vignette, and a telemetry strip like
/// real UAV video burns in.
///
/// Moving things (traffic, driving scenario objects) are drawn where they were at the frame's
/// moment in sim time (<see cref="World.GroundWorld"/>): survey frames are rendered after they were
/// captured, and a close-up is taken as at the frame its candidate was seen in.
/// </summary>
public sealed class CameraRenderer(GroundRenderer ground, ScenarioStore scenario, Imagery.VehiclePhotos? photos = null,
    World.GroundWorld? world = null)
{
    private const int JpegQuality = 82;

    public byte[] RenderJpeg(FrameTelemetry frame, string tailNumber, IReadOnlyList<CameraOverlay>? overlays = null, double? simTime = null)
    {
        using var image = Render(frame, tailNumber, overlays, simTime);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return data.ToArray();
    }

    public SKImage Render(FrameTelemetry frame, string tailNumber, IReadOnlyList<CameraOverlay>? overlays = null, double? simTime = null)
    {
        var at = simTime ?? world?.Clock.At(frame.CapturedAtUtc) ?? 0;
        var width = frame.Width;
        var height = frame.Height;
        var camera = new CameraModel(frame);
        var gsd = camera.MetersPerPixel;

        // Ground frame (local meters) → frame pixels.
        var center = ground.Projection.ToLocal(new GeoPoint(frame.Lat, frame.Lng));
        var heading = frame.HeadingDeg * Math.PI / 180;
        var forward = new Vec2(Math.Sin(heading), Math.Cos(heading));
        var right = new Vec2(Math.Cos(heading), -Math.Sin(heading));
        var a = right.X / gsd;
        var b = right.Y / gsd;
        var c = width / 2.0 - (center.X * right.X + center.Y * right.Y) / gsd;
        var d = -forward.X / gsd;
        var e = -forward.Y / gsd;
        var f = height / 2.0 + (center.X * forward.X + center.Y * forward.Y) / gsd;
        SKPoint ToFrame(Vec2 local) => new((float)(a * local.X + b * local.Y + c), (float)(d * local.X + e * local.Y + f));

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException("Could not create a drawing surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(120, 110, 90));

        DrawGround(canvas, camera, center, gsd, a, b, c, d, e, f);
        DrawTraffic(canvas, camera, frame, gsd, forward, right, ToFrame, at);
        DrawScenarioObjects(canvas, frame, gsd, forward, right, ToFrame, world?.ObjectsAt(at) ?? scenario.All());

        using var raw = surface.Snapshot();
        using var output = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))!;
        var o = output.Canvas;
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(0.55f, 0.55f) })
            o.DrawImage(raw, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blur);
        SensorLook(o, width, height, frame.Seq);
        if (overlays is { Count: > 0 })
            DrawOverlays(o, overlays, gsd, ToFrame);
        BurnIn(o, width, height, frame, tailNumber);
        return output.Snapshot();
    }

    private void DrawGround(SKCanvas canvas, CameraModel camera, Vec2 center, double gsd,
        double a, double b, double c, double d, double e, double f)
    {
        var level = GroundRenderer.LevelFor(gsd);
        // Too many chunks at this level for the view: go coarser.
        while (true)
        {
            var (cx0, cx1, cy0, cy1) = ChunkRange(camera, center, level);
            if ((cx1 - cx0 + 1) * (cy1 - cy0 + 1) <= 16 || level == GroundRenderer.LevelMetersPerPixel.Length - 1)
                break;
            level++;
        }

        var mpp = GroundRenderer.LevelMetersPerPixel[level];
        var size = GroundRenderer.ChunkMeters(level);
        var (x0i, x1i, y0i, y1i) = ChunkRange(camera, center, level);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        for (var cx = x0i; cx <= x1i; cx++)
        for (var cy = y0i; cy <= y1i; cy++)
        {
            var chunk = ground.GetChunk(level, cx, cy);
            // Chunk pixel → local: X = x0 + px·mpp, Y = y1 − py·mpp; then local → frame.
            var x0 = cx * size;
            var y1 = (cy + 1) * size;
            var matrix = new SKMatrix(
                (float)(a * mpp), (float)(-b * mpp), (float)(a * x0 + b * y1 + c),
                (float)(d * mpp), (float)(-e * mpp), (float)(d * x0 + e * y1 + f),
                0, 0, 1);
            canvas.Save();
            canvas.Concat(in matrix);
            canvas.DrawImage(chunk, 0, 0, sampling);
            canvas.Restore();
        }
    }

    private (int X0, int X1, int Y0, int Y1) ChunkRange(CameraModel camera, Vec2 center, int level)
    {
        var size = GroundRenderer.ChunkMeters(level);
        var radius = Math.Sqrt(camera.GroundWidthMeters * camera.GroundWidthMeters + camera.GroundHeightMeters * camera.GroundHeightMeters) / 2 + 2;
        return ((int)Math.Floor((center.X - radius) / size), (int)Math.Floor((center.X + radius) / size),
                (int)Math.Floor((center.Y - radius) / size), (int)Math.Floor((center.Y + radius) / size));
    }

    /// <summary>Background traffic in view, as drawn vehicles at their true size and heading.</summary>
    private void DrawTraffic(SKCanvas canvas, CameraModel camera, FrameTelemetry frame, double gsd, Vec2 forward, Vec2 right,
        Func<Vec2, SKPoint> toFrame, double simTime)
    {
        if (world?.Traffic is null || gsd > 1.5)
            return; // from high up (or wide), a car is under two pixels
        var shadow = Shadow(gsd, forward, right);
        var radius = Math.Sqrt(camera.GroundWidthMeters * camera.GroundWidthMeters + camera.GroundHeightMeters * camera.GroundHeightMeters) / 2 + 10;
        foreach (var (vehicle, position, heading) in world.TrafficNear(new GeoPoint(frame.Lat, frame.Lng), radius, simTime))
        {
            var p = toFrame(ground.Projection.ToLocal(position));
            if (p.X < -50 || p.Y < -50 || p.X > frame.Width + 50 || p.Y > frame.Height + 50)
                continue;
            var paint = VehicleSprites.Colors.GetValueOrDefault(vehicle.Color, VehicleSprites.Colors["grey"]);
            VehicleSprites.Draw(canvas, p, heading - frame.HeadingDeg, 1 / gsd, VehicleLook.Of(vehicle.Kind, paint), shadow);
        }
    }

    /// <summary>Shadow direction in frame pixels per meter of height.</summary>
    private static SKPoint Shadow(double gsd, Vec2 forward, Vec2 right)
    {
        var s = GroundRenderer.SunShadowPerMeter;
        return new SKPoint((float)((s.X * right.X + s.Y * right.Y) / gsd), (float)(-(s.X * forward.X + s.Y * forward.Y) / gsd));
    }

    private void DrawScenarioObjects(SKCanvas canvas, FrameTelemetry frame, double gsd, Vec2 forward, Vec2 right, Func<Vec2, SKPoint> toFrame,
        IReadOnlyList<ScenarioObject> objects)
    {
        var shadow = Shadow(gsd, forward, right);
        foreach (var obj in objects)
        {
            var p = toFrame(ground.Projection.ToLocal(new GeoPoint(obj.Lat, obj.Lng)));
            if (p.X < -50 || p.Y < -50 || p.X > frame.Width + 50 || p.Y > frame.Height + 50)
                continue;
            // A real vehicle photo at its true scale, turned to the object's heading.
            if (obj.Photo is not null && photos?.Get(obj.Photo) is { } photo)
            {
                var scale = (float)(photo.MetersPerPixel / gsd);
                canvas.Save();
                canvas.Translate(p);
                canvas.RotateDegrees((float)(obj.HeadingDeg - frame.HeadingDeg));
                canvas.Scale(scale);
                canvas.DrawImage(photo.Image, -photo.Image.Width / 2f, -photo.Image.Height / 2f, new SKSamplingOptions(SKCubicResampler.Mitchell));
                canvas.Restore();
                continue;
            }
            if (!Enum.TryParse<VehicleKind>(obj.Kind, ignoreCase: true, out var kind))
                kind = VehicleKind.Car;
            var paint = VehicleSprites.Colors.GetValueOrDefault(obj.Color, VehicleSprites.Colors["grey"]);
            VehicleSprites.Draw(canvas, p, obj.HeadingDeg - frame.HeadingDeg, 1 / gsd, VehicleLook.Of(kind, paint), shadow);
        }
    }

    /// <summary>Grain, a little haze and a vignette.</summary>
    private static void SensorLook(SKCanvas canvas, int width, int height, long seed)
    {
        using (var haze = new SKPaint { Color = new SKColor(206, 214, 222, 16) })
            canvas.DrawRect(0, 0, width, height, haze);

        using (var noise = SKShader.CreatePerlinNoiseTurbulence(0.9f, 0.9f, 1, (float)(seed % 997)))
        using (var gray = SKColorFilter.CreateColorMatrix(
               [0.33f, 0.33f, 0.33f, 0, 0, 0.33f, 0.33f, 0.33f, 0, 0, 0.33f, 0.33f, 0.33f, 0, 0, 0, 0, 0, 0, 0.16f]))
        using (var grain = new SKPaint { Shader = noise, ColorFilter = gray, BlendMode = SKBlendMode.Overlay })
            canvas.DrawRect(0, 0, width, height, grain);

        var radius = (float)Math.Sqrt(width * width + height * height) / 2;
        using var vignette = SKShader.CreateRadialGradient(new SKPoint(width / 2f, height / 2f), radius,
            [SKColors.Transparent, SKColors.Transparent, new SKColor(0, 0, 0, 90)], [0f, 0.62f, 1f], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = vignette };
        canvas.DrawRect(0, 0, width, height, paint);
    }

    /// <summary>A box around each thing the detector found, placed by where it is on the ground so
    /// it stays on the object as the UAV moves.</summary>
    private void DrawOverlays(SKCanvas canvas, IReadOnlyList<CameraOverlay> overlays, double gsd, Func<Vec2, SKPoint> toFrame)
    {
        using var box = new SKPaint { Color = new SKColor(80, 255, 120), Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(80, 255, 120), IsAntialias = true };
        using var textShadow = new SKPaint { Color = new SKColor(0, 0, 0, 180), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 17);
        foreach (var overlay in overlays)
        {
            var p = toFrame(ground.Projection.ToLocal(overlay.Position));
            var w = (float)Math.Max(overlay.WidthMeters / gsd, 18) + 8;
            var h = (float)Math.Max(overlay.HeightMeters / gsd, 18) + 8;
            var rect = new SKRect(p.X - w / 2, p.Y - h / 2, p.X + w / 2, p.Y + h / 2);
            canvas.DrawRect(rect, box);
            canvas.DrawText(overlay.Text, rect.Left + 1, rect.Top - 5, SKTextAlign.Left, font, textShadow);
            canvas.DrawText(overlay.Text, rect.Left, rect.Top - 6, SKTextAlign.Left, font, textPaint);
        }
    }

    private static void BurnIn(SKCanvas canvas, int width, int height, FrameTelemetry frame, string tailNumber)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"{tailNumber}  {frame.Lat:F5}N {frame.Lng:F5}E  ALT {frame.AltitudeFt:F0}FT  HDG {frame.HeadingDeg:000}  HFOV {frame.HFovDeg:F0}  {frame.CapturedAtUtc:HH:mm:ss}Z  #{frame.Seq}");
        using var font = new SKFont(SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default, 15);
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 170), IsAntialias = true };
        using var paint = new SKPaint { Color = new SKColor(240, 240, 240, 230), IsAntialias = true };
        canvas.DrawText(text, 11, height - 11, SKTextAlign.Left, font, shadow);
        canvas.DrawText(text, 10, height - 12, SKTextAlign.Left, font, paint);
    }
}
