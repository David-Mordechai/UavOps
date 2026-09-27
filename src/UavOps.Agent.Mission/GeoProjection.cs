namespace UavOps.Agent.Mission;

/// <summary>A point in a <see cref="GeoProjection"/>'s local frame, in meters (X east, Y north).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public static double Cross(Vec2 a, Vec2 b) => a.X * b.Y - a.Y * b.X;
    public static double Distance(Vec2 a, Vec2 b) => (a - b).Length;
}

/// <summary>
/// Equirectangular projection around a fixed origin. At the few-km scale of an AOI its error is
/// well under a meter, which is all route planning and footprint checks need.
/// </summary>
public sealed class GeoProjection
{
    private const double EarthRadiusMeters = 6_371_008.8;
    private const double DegToRad = Math.PI / 180;

    private readonly GeoPoint _origin;
    private readonly double _metersPerDegLat;
    private readonly double _metersPerDegLng;

    public GeoProjection(GeoPoint origin)
    {
        _origin = origin;
        _metersPerDegLat = EarthRadiusMeters * DegToRad;
        _metersPerDegLng = _metersPerDegLat * Math.Cos(origin.Lat * DegToRad);
    }

    /// <summary>Centred on the mean of <paramref name="points"/>.</summary>
    public static GeoProjection Around(IReadOnlyCollection<GeoPoint> points) =>
        new(new GeoPoint(points.Average(p => p.Lat), points.Average(p => p.Lng)));

    public Vec2 ToLocal(GeoPoint p) =>
        new((p.Lng - _origin.Lng) * _metersPerDegLng, (p.Lat - _origin.Lat) * _metersPerDegLat);

    public GeoPoint ToGeo(Vec2 v) =>
        new(_origin.Lat + v.Y / _metersPerDegLat, _origin.Lng + v.X / _metersPerDegLng);

    /// <summary>Great-circle-free approximation, fine at AOI scale.</summary>
    public static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        var projection = new GeoProjection(a);
        return projection.ToLocal(b).Length;
    }
}
