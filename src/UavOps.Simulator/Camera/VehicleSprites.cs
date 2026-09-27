using SkiaSharp;

namespace UavOps.Simulator.Camera;

public enum VehicleKind { Car, Van, Pickup, Truck, Bus }

/// <summary>A vehicle's look: what it is, its paint, and its real size in meters.</summary>
public sealed record VehicleLook(VehicleKind Kind, SKColor Paint, double LengthMeters, double WidthMeters)
{
    public static (double Length, double Width) DefaultSize(VehicleKind kind) => kind switch
    {
        VehicleKind.Van => (5.4, 2.05),
        VehicleKind.Pickup => (5.3, 1.9),
        VehicleKind.Truck => (8.5, 2.5),
        VehicleKind.Bus => (12.0, 2.55),
        _ => (4.5, 1.8)
    };

    public static VehicleLook Of(VehicleKind kind, SKColor paint)
    {
        var (length, width) = DefaultSize(kind);
        return new VehicleLook(kind, paint, length, width);
    }
}

/// <summary>
/// Top-down vehicles as a nadir camera sees them, drawn from their real anatomy front to back
/// (fractions of length):
/// <list type="bullet">
/// <item>car: hood, sloped windshield, roof, rear window, trunk;</item>
/// <item>van: short hood, windshield, then one long flat roof to the back (ribbed), no rear window;</item>
/// <item>pickup: hood, windshield, short cab roof, rear cab window, open bed with side walls;</item>
/// <item>truck: separate cab, a gap, a long cargo box;</item>
/// <item>bus: flat front, a long roof with AC units and hatches, window strips along both sides.</item>
/// </list>
/// With shading across the body (curved panels catch light along the middle), wheels peeking out,
/// mirrors, and a soft shadow whose length follows height. At true scale, so what a model can make
/// out depends on altitude the way it would for a real camera.
/// </summary>
public static class VehicleSprites
{
    public static readonly IReadOnlyDictionary<string, SKColor> Colors = new Dictionary<string, SKColor>(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = new(238, 239, 237),
        ["silver"] = new(182, 186, 190),
        ["grey"] = new(118, 122, 126),
        ["gray"] = new(118, 122, 126),
        ["black"] = new(30, 32, 35),
        ["blue"] = new(40, 72, 142),
        ["red"] = new(172, 30, 32),
        ["green"] = new(42, 102, 62),
        ["yellow"] = new(224, 188, 42),
        ["orange"] = new(216, 112, 32),
        ["brown"] = new(112, 78, 52),
        ["beige"] = new(208, 192, 162)
    };

    private static readonly SKColor Glass = new(28, 36, 46);
    private static readonly SKColor Tyre = new(22, 22, 22);

    /// <param name="center">Where the vehicle is, in canvas pixels.</param>
    /// <param name="headingDeg">Which way its front points, in canvas degrees (0 = up, clockwise).</param>
    /// <param name="pixelsPerMeter">Canvas scale.</param>
    /// <param name="shadow">Shadow offset in canvas pixels per meter of height.</param>
    public static void Draw(SKCanvas canvas, SKPoint center, double headingDeg, double pixelsPerMeter, VehicleLook look, SKPoint shadow)
    {
        var ppm = (float)pixelsPerMeter;
        var length = (float)look.LengthMeters * ppm;
        var width = (float)look.WidthMeters * ppm;
        if (length < 1.2f)
        {
            // Sub-pixel at this altitude: one dot of the paint colour.
            using var dot = new SKPaint { Color = look.Paint, IsAntialias = true };
            canvas.DrawCircle(center, Math.Max(length, 0.6f) / 2, dot);
            return;
        }

        var height = look.Kind switch { VehicleKind.Truck => 3.4f, VehicleKind.Bus => 3.2f, VehicleKind.Van => 2.4f, VehicleKind.Pickup => 1.8f, _ => 1.45f };

        // Soft shadow, stretched by height.
        using (var shadowPaint = new SKPaint { Color = new SKColor(0, 0, 0, 95), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.35f * ppm, 0.4f)) })
        {
            canvas.Save();
            canvas.Translate(center.X + shadow.X * height * 0.8f, center.Y + shadow.Y * height * 0.8f);
            canvas.RotateDegrees((float)headingDeg);
            canvas.DrawRoundRect(-width / 2, -length / 2, width, length, width * 0.25f, width * 0.25f, shadowPaint);
            canvas.Restore();
        }

        canvas.Save();
        canvas.Translate(center.X, center.Y);
        canvas.RotateDegrees((float)headingDeg);
        // Local frame: front is -Y (up), right is +X.
        var body = new SKRect(-width / 2, -length / 2, width / 2, length / 2);
        using var paint = new SKPaint { IsAntialias = true };

        Wheels(canvas, paint, body, look.Kind, ppm);

        switch (look.Kind)
        {
            case VehicleKind.Car:
                Shell(canvas, paint, look.Paint, body, radius: 0.28f);
                Panel(canvas, paint, look.Paint, body, 0.02f, 0.27f, 0.08f, 0.9f);        // hood
                Windshield(canvas, paint, body, 0.27f, 0.40f, 0.1f);
                Panel(canvas, paint, look.Paint, body, 0.40f, 0.74f, 0.12f, 1.08f);       // roof
                Windshield(canvas, paint, body, 0.74f, 0.84f, 0.13f);                     // rear window
                Panel(canvas, paint, look.Paint, body, 0.84f, 0.98f, 0.1f, 0.93f);        // trunk
                Mirrors(canvas, paint, look.Paint, body, 0.31f, ppm);
                break;

            case VehicleKind.Van:
                Shell(canvas, paint, look.Paint, body, radius: 0.16f);
                Panel(canvas, paint, look.Paint, body, 0.02f, 0.13f, 0.1f, 0.88f);        // short hood
                Windshield(canvas, paint, body, 0.13f, 0.24f, 0.08f);
                Panel(canvas, paint, look.Paint, body, 0.24f, 0.985f, 0.05f, 1.1f);       // long flat roof
                Ribs(canvas, look.Paint, body, 0.34f, 0.95f, 5, ppm);
                Mirrors(canvas, paint, look.Paint, body, 0.16f, ppm);
                break;

            case VehicleKind.Pickup:
                Shell(canvas, paint, look.Paint, body, radius: 0.2f);
                Panel(canvas, paint, look.Paint, body, 0.02f, 0.24f, 0.08f, 0.9f);        // hood
                Windshield(canvas, paint, body, 0.24f, 0.35f, 0.1f);
                Panel(canvas, paint, look.Paint, body, 0.35f, 0.52f, 0.12f, 1.08f);       // cab roof
                Windshield(canvas, paint, body, 0.52f, 0.555f, 0.16f);                    // small rear cab window
                // Open cargo bed: bright side walls and tailgate around a dark ribbed floor.
                paint.Color = look.Paint;
                canvas.DrawRect(Band(body, 0.575f, 0.995f, 0.02f), paint);
                var floor = Band(body, 0.6f, 0.965f, 0.15f);
                paint.Color = new SKColor(52, 50, 48);
                canvas.DrawRect(floor, paint);
                using (var rib = new SKPaint { Color = new SKColor(84, 82, 78), StrokeWidth = Math.Max(0.06f * ppm, 0.4f), IsAntialias = true })
                    for (var i = 1; i <= 3; i++)
                    {
                        var x = floor.Left + floor.Width * i / 4;
                        canvas.DrawLine(x, floor.Top, x, floor.Bottom, rib);
                    }
                Mirrors(canvas, paint, look.Paint, body, 0.27f, ppm);
                break;

            case VehicleKind.Truck:
                // Cab.
                paint.Color = Shade(look.Paint, 0.85f);
                canvas.DrawRoundRect(Band(body, 0.0f, 0.2f, 0.02f), width * 0.12f, width * 0.12f, paint);
                Windshield(canvas, paint, body, 0.02f, 0.06f, 0.06f);
                Panel(canvas, paint, look.Paint, body, 0.06f, 0.19f, 0.06f, 1.05f);
                // Cargo box, a little wider than the cab.
                paint.Color = new SKColor(40, 40, 40);
                canvas.DrawRect(Band(body, 0.2f, 0.225f, 0.2f), paint);
                Panel(canvas, paint, look.Paint, body, 0.225f, 1.0f, -0.02f, 1.02f);
                Ribs(canvas, look.Paint, body, 0.3f, 0.97f, 7, ppm);
                break;

            case VehicleKind.Bus:
                Shell(canvas, paint, look.Paint, body, radius: 0.1f);
                paint.Color = Glass;
                canvas.DrawRect(Band(body, 0.005f, 0.035f, 0.05f), paint);                // front screen
                // Side windows: a dark strip along each long edge.
                canvas.DrawRect(new SKRect(body.Left + width * 0.02f, body.Top + length * 0.06f, body.Left + width * 0.1f, body.Top + length * 0.95f), paint);
                canvas.DrawRect(new SKRect(body.Right - width * 0.1f, body.Top + length * 0.06f, body.Right - width * 0.02f, body.Top + length * 0.95f), paint);
                Panel(canvas, paint, look.Paint, body, 0.04f, 0.98f, 0.12f, 1.04f);
                paint.Color = new SKColor(158, 160, 158);
                canvas.DrawRoundRect(Band(body, 0.28f, 0.42f, 0.24f), 2, 2, paint);       // AC units
                canvas.DrawRoundRect(Band(body, 0.6f, 0.7f, 0.26f), 2, 2, paint);
                paint.Color = new SKColor(90, 96, 104);
                canvas.DrawRect(Band(body, 0.12f, 0.17f, 0.34f), paint);                  // roof hatches
                canvas.DrawRect(Band(body, 0.82f, 0.87f, 0.34f), paint);
                break;
        }
        canvas.Restore();
    }

    /// <summary>The body outline, a shade darker than the panels on top of it (its sides).</summary>
    private static void Shell(SKCanvas canvas, SKPaint paint, SKColor color, SKRect body, float radius)
    {
        paint.Color = Shade(color, 0.72f);
        canvas.DrawRoundRect(body, body.Width * radius, body.Width * radius, paint);
    }

    /// <summary>A body panel, shaded across its width like a curved surface catching the light.</summary>
    private static void Panel(SKCanvas canvas, SKPaint paint, SKColor color, SKRect body, float from, float to, float inset, float brightness)
    {
        var r = Band(body, from, to, inset);
        using var shading = SKShader.CreateLinearGradient(
            new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
            [Shade(color, 0.86f * brightness), Shade(color, 1.0f * brightness), Shade(color, 0.9f * brightness)],
            [0f, 0.45f, 1f], SKShaderTileMode.Clamp);
        paint.Shader = shading;
        canvas.DrawRoundRect(r, r.Width * 0.14f, r.Width * 0.14f, paint);
        paint.Shader = null;
    }

    private static void Windshield(SKCanvas canvas, SKPaint paint, SKRect body, float from, float to, float inset)
    {
        // A trapezoid: glass narrows toward the roof.
        var r = Band(body, from, to, inset);
        var narrow = r.Width * 0.08f;
        using var builder = new SKPathBuilder();
        builder.AddPoly([new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top), new SKPoint(r.Right - narrow, r.Bottom), new SKPoint(r.Left + narrow, r.Bottom)], close: true);
        using var path = builder.Detach();
        using var glass = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom),
            [new SKColor(52, 64, 78), Glass, new SKColor(60, 72, 86)], SKShaderTileMode.Clamp);
        paint.Shader = glass;
        canvas.DrawPath(path, paint);
        paint.Shader = null;
    }

    /// <summary>Roof ribs / cargo box seams across the body.</summary>
    private static void Ribs(SKCanvas canvas, SKColor color, SKRect body, float from, float to, int count, float ppm)
    {
        using var rib = new SKPaint { Color = Shade(color, 0.8f), StrokeWidth = Math.Max(0.08f * ppm, 0.4f), IsAntialias = true };
        for (var i = 0; i < count; i++)
        {
            var y = body.Top + body.Height * (from + (to - from) * i / Math.Max(count - 1, 1));
            canvas.DrawLine(body.Left + body.Width * 0.12f, y, body.Right - body.Width * 0.12f, y, rib);
        }
    }

    private static void Wheels(SKCanvas canvas, SKPaint paint, SKRect body, VehicleKind kind, float ppm)
    {
        var axles = kind switch
        {
            VehicleKind.Bus => new[] { 0.16f, 0.78f },
            VehicleKind.Truck => [0.1f, 0.68f, 0.82f],
            VehicleKind.Van => [0.17f, 0.8f],
            _ => [0.19f, 0.8f]
        };
        paint.Color = Tyre;
        var tyreLength = 0.65f * ppm;
        var tyreWidth = Math.Max(0.22f * ppm, 0.5f);
        foreach (var a in axles)
        {
            var y = body.Top + body.Height * a;
            canvas.DrawRect(body.Left - tyreWidth * 0.35f, y - tyreLength / 2, tyreWidth, tyreLength, paint);
            canvas.DrawRect(body.Right - tyreWidth * 0.65f, y - tyreLength / 2, tyreWidth, tyreLength, paint);
        }
    }

    private static void Mirrors(SKCanvas canvas, SKPaint paint, SKColor color, SKRect body, float at, float ppm)
    {
        paint.Color = Shade(color, 0.75f);
        var y = body.Top + body.Height * at;
        var size = Math.Max(0.2f * ppm, 0.5f);
        canvas.DrawRect(body.Left - size, y, size, size * 0.8f, paint);
        canvas.DrawRect(body.Right, y, size, size * 0.8f, paint);
    }

    /// <summary>A band across the body, <paramref name="from"/>..<paramref name="to"/> of its length
    /// from the front, inset by a fraction of its width on each side.</summary>
    private static SKRect Band(SKRect body, float from, float to, float inset) =>
        new(body.Left + body.Width * inset, body.Top + body.Height * from, body.Right - body.Width * inset, body.Top + body.Height * to);

    public static SKColor Shade(SKColor c, float k) =>
        new((byte)Math.Clamp(c.Red * k, 0, 255), (byte)Math.Clamp(c.Green * k, 0, 255), (byte)Math.Clamp(c.Blue * k, 0, 255), c.Alpha);
}
