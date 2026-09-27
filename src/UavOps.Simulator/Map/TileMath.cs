namespace UavOps.Simulator.Map;

/// <summary>Web Mercator (slippy map) tile math.</summary>
public static class TileMath
{
    public static (int X, int Y) TileAt(double lat, double lng, int z)
    {
        var (fx, fy) = Fraction(lat, lng, z);
        var n = 1 << z;
        return (Math.Clamp((int)Math.Floor(fx), 0, n - 1), Math.Clamp((int)Math.Floor(fy), 0, n - 1));
    }

    /// <summary>Tile coordinates with the fractional part: (3.5, 7.25) is inside tile (3, 7).</summary>
    public static (double X, double Y) Fraction(double lat, double lng, int z)
    {
        var n = 1 << z;
        var latRad = lat * Math.PI / 180;
        var x = (lng + 180) / 360 * n;
        var y = (1 - Math.Log(Math.Tan(latRad) + 1 / Math.Cos(latRad)) / Math.PI) / 2 * n;
        return (x, y);
    }

    /// <summary>A point inside tile (z, x, y), given in that tile's 0..extent coordinates.</summary>
    public static (double Lat, double Lng) ToGeo(int z, int x, int y, double px, double py, int extent)
    {
        var n = 1 << z;
        var fx = x + px / extent;
        var fy = y + py / extent;
        var lng = fx / n * 360 - 180;
        var lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * fy / n))) * 180 / Math.PI;
        return (lat, lng);
    }
}
