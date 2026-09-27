using UavOps.Agent.Mission;

namespace UavOps.Onboard.Contracts;

/// <summary>
/// The payload camera's geometry, shared so the simulator that draws a frame and the detector
/// that reads one agree on where every pixel is. The camera looks straight down (nadir), the top
/// of the image points along the UAV's heading, and the ground is flat.
/// </summary>
public sealed class CameraModel
{
    private const double MetersPerFoot = 0.3048;

    private readonly GeoProjection _projection;
    private readonly Vec2 _forward;
    private readonly Vec2 _right;

    public CameraModel(double lat, double lng, double altitudeFt, double headingDeg, double hFovDeg, int width, int height)
    {
        Width = width;
        Height = height;
        GroundWidthMeters = GroundWidthFor(altitudeFt, hFovDeg);
        GroundHeightMeters = GroundWidthMeters * height / width;
        _projection = new GeoProjection(new GeoPoint(lat, lng));
        var heading = headingDeg * Math.PI / 180;
        _forward = new Vec2(Math.Sin(heading), Math.Cos(heading)); // compass: 0 north, 90 east
        _right = new Vec2(Math.Cos(heading), -Math.Sin(heading));
    }

    public CameraModel(FrameTelemetry t) : this(t.Lat, t.Lng, t.AltitudeFt, t.HeadingDeg, t.HFovDeg, t.Width, t.Height) { }

    public int Width { get; }
    public int Height { get; }
    public double GroundWidthMeters { get; }
    public double GroundHeightMeters { get; }

    /// <summary>Meters on the ground per pixel.</summary>
    public double MetersPerPixel => GroundWidthMeters / Width;

    public static double GroundWidthFor(double altitudeFt, double hFovDeg) =>
        2 * Math.Max(altitudeFt, 0) * MetersPerFoot * Math.Tan(hFovDeg * Math.PI / 360);

    /// <summary>A pixel (x right, y down) to the ground point under it.</summary>
    public GeoPoint PixelToGeo(double x, double y) => _projection.ToGeo(PixelToLocal(x, y));

    /// <summary>A normalized 0-1000 coordinate (as VLM boxes use) to the ground point under it.</summary>
    public GeoPoint NormalizedToGeo(double nx, double ny) => PixelToGeo(nx / 1000 * Width, ny / 1000 * Height);

    /// <summary>A ground point to its pixel; outside [0,Width)×[0,Height) means out of frame.</summary>
    public (double X, double Y) GeoToPixel(GeoPoint point)
    {
        var local = _projection.ToLocal(point);
        var right = local.X * _right.X + local.Y * _right.Y;
        var forward = local.X * _forward.X + local.Y * _forward.Y;
        return ((right / GroundWidthMeters + 0.5) * Width, (0.5 - forward / GroundHeightMeters) * Height);
    }

    public bool Contains(GeoPoint point)
    {
        var (x, y) = GeoToPixel(point);
        return x >= 0 && x < Width && y >= 0 && y < Height;
    }

    /// <summary>The frame's ground outline, clockwise from top-left, as [lng, lat] pairs for GeoJSON.</summary>
    public List<double[]> FootprintLngLat() =>
        new[] { (0.0, 0.0), (Width, 0.0), (Width, Height), (0.0, Height) }
            .Select(c => PixelToGeo(c.Item1, c.Item2))
            .Select(p => new[] { p.Lng, p.Lat })
            .ToList();

    private Vec2 PixelToLocal(double x, double y)
    {
        var right = (x / Width - 0.5) * GroundWidthMeters;
        var forward = (0.5 - y / Height) * GroundHeightMeters;
        return _right * right + _forward * forward;
    }
}
