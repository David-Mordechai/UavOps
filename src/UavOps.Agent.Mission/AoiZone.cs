namespace UavOps.Agent.Mission;

/// <summary>A named area of interest: a simple polygon (not self-intersecting, no holes), vertices
/// in order, not closed (the last vertex doesn't repeat the first).</summary>
public sealed record AoiZone(string Name, IReadOnlyList<GeoPoint> Vertices)
{
    public AoiZoneSummary Summarize()
    {
        var projection = GeoProjection.Around(Vertices.ToList());
        var local = Vertices.Select(projection.ToLocal).ToList();
        return new AoiZoneSummary(
            Name,
            Vertices.Count,
            Math.Round(Polygon.Area(local) / 1_000_000, 3),
            projection.ToGeo(Polygon.Centroid(local)),
            new GeoPoint(Vertices.Min(v => v.Lat), Vertices.Min(v => v.Lng)),
            new GeoPoint(Vertices.Max(v => v.Lat), Vertices.Max(v => v.Lng)));
    }
}

public sealed record AoiZoneSummary(
    string Name,
    int VertexCount,
    double AreaSqKm,
    GeoPoint Centroid,
    GeoPoint SouthWest,
    GeoPoint NorthEast);
