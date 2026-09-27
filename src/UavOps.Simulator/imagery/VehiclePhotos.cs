using SkiaSharp;

namespace UavOps.Simulator.Imagery;

/// <summary>A real vehicle, cut out of one of the aerial photos (<c>Simulator:VehiclePhotos</c>).</summary>
public sealed class VehiclePhotoConfig
{
    public string Name { get; set; } = "";
    /// <summary>The image in imagery.json it's cut from.</summary>
    public string Image { get; set; } = "";
    /// <summary>Its centre, in the image's full-resolution pixels.</summary>
    public double Col { get; set; }
    public double Row { get; set; }
    public double LengthPx { get; set; }
    public double WidthPx { get; set; }
    /// <summary>Which way its front points in the image, degrees clockwise from the image's up.</summary>
    public double HeadingDeg { get; set; }
    /// <summary>What it is, so a scenario object of this kind and colour looks like it.</summary>
    public string Kind { get; set; } = "";
    public string Color { get; set; } = "";
}

/// <summary>
/// Real vehicle photos for scenario objects: each cut from an aerial photo, rotated to face up,
/// edges feathered, and cached. Drawn at true scale (the source photo's own meters per pixel), so a
/// placed van is exactly as big as the van it was photographed as.
/// </summary>
public sealed class VehiclePhotos(ImageryLayer imagery, IReadOnlyList<VehiclePhotoConfig> configs)
{
    private readonly Dictionary<string, (SKImage Image, double MetersPerPixel)?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IReadOnlyList<VehiclePhotoConfig> All => configs;

    /// <summary>The first photo of this kind and colour, if there is one.</summary>
    public VehiclePhotoConfig? Matching(string kind, string color) =>
        configs.FirstOrDefault(c => c.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase) && c.Color.Equals(color, StringComparison.OrdinalIgnoreCase));

    /// <summary>The photo, facing up, and its scale; null if it or its source image is missing.</summary>
    public (SKImage Image, double MetersPerPixel)? Get(string name)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached))
                return cached;
            var config = configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            (SKImage, double)? result = null;
            if (config is not null && imagery.Find(config.Image) is { } source)
            {
                // A soft edge about a fifth of the width wide, so the cut blends into any ground.
                var feather = Math.Max(config.WidthPx * 0.12, 4);
                var image = imagery.Cutout(config.Image, config.Col, config.Row, config.LengthPx, config.WidthPx, config.HeadingDeg, feather);
                if (image is not null)
                    result = (image, source.Tiff.PixelSizeX);
            }
            _cache[name] = result;
            return result;
        }
    }
}
