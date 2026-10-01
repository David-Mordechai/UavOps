using SkiaSharp;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector.Perception;

/// <summary>
/// An object's colour from its pixels, in under a millisecond: no model. From above, a vehicle is
/// mostly roof and bonnet, with dark windscreens and shadow at the edges, so only the middle of the
/// box is read, and dark pixels count only when there's little else (a black car). Each pixel is
/// named (HSV bins), and the most frequent name wins.
/// </summary>
public static class ColourNamer
{
    public static string? Name(SKBitmap image, BoundingBox box)
    {
        // The middle 60% of the box.
        var x1 = (int)((box.X1 + (box.X2 - box.X1) * 0.2) / 1000 * image.Width);
        var x2 = (int)((box.X2 - (box.X2 - box.X1) * 0.2) / 1000 * image.Width);
        var y1 = (int)((box.Y1 + (box.Y2 - box.Y1) * 0.2) / 1000 * image.Height);
        var y2 = (int)((box.Y2 - (box.Y2 - box.Y1) * 0.2) / 1000 * image.Height);
        x1 = Math.Clamp(x1, 0, image.Width - 1);
        x2 = Math.Clamp(x2, x1 + 1, image.Width);
        y1 = Math.Clamp(y1, 0, image.Height - 1);
        y2 = Math.Clamp(y2, y1 + 1, image.Height);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        var step = Math.Max(1, Math.Max(x2 - x1, y2 - y1) / 24); // ~24x24 samples at most
        for (var y = y1; y < y2; y += step)
        for (var x = x1; x < x2; x += step)
        {
            var name = Of(image.GetPixel(x, y));
            counts[name] = counts.GetValueOrDefault(name) + 1;
            total++;
        }
        if (total == 0)
            return null;

        var black = counts.GetValueOrDefault("black");
        var best = counts.Where(c => c.Key != "black").OrderByDescending(c => c.Value).FirstOrDefault();
        // Dark pixels are windows and shadow unless they are most of what's there.
        if (best.Key is null || black > total * 0.6 || best.Value < total * 0.15)
            return black >= (best.Value) ? "black" : best.Key;
        return best.Key;
    }

    /// <summary>One pixel's colour name.</summary>
    public static string Of(SKColor c)
    {
        c.ToHsv(out var h, out var s, out var v); // h 0-360, s and v 0-100
        // Dark and only faintly tinted is black: a black car's roof reads slightly blue in daylight
        // (measured: the scenario's black car came out "blue" on every frame).
        if (v < 22 || (v < 40 && s < 55))
            return "black";
        if (s < 16)
            return v > 72 ? "white" : v < 35 ? "black" : "gray";
        if (s < 30 && v > 80)
            return "white";
        return h switch
        {
            < 14 or >= 340 => v < 45 ? "brown" : "red",
            < 40 => v < 55 || s < 45 ? "brown" : "orange",
            < 68 => v < 50 ? "brown" : "yellow",
            < 165 => "green",
            < 260 => "blue",
            < 300 => "blue", // purple reads as blue at these sizes
            _ => "red",
        };
    }
}
