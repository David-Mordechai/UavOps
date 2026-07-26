namespace UavOps.ControlApi.Services;

/// <summary>
/// Stand-in for a real points registry. Maps a human-friendly location name to coordinates
/// so navigation and payload-pointing commands have something to resolve.
/// </summary>
public static class KnownPoints
{
    private static readonly Dictionary<string, (double Lat, double Lng)> Points = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = (31.801447, 34.643497),
        ["target alpha"] = (31.812000, 34.660000),
        ["target bravo"] = (31.790000, 34.630000),
    };

    public static bool TryResolve(string name, out double lat, out double lng)
    {
        if (Points.TryGetValue(name, out var p))
        {
            (lat, lng) = p;
            return true;
        }

        lat = 0;
        lng = 0;
        return false;
    }
}
