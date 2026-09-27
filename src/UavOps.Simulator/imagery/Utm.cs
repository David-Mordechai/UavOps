namespace UavOps.Simulator.Imagery;

/// <summary>
/// WGS84 latitude/longitude to and from UTM (transverse Mercator, Snyder's series), for the
/// aerial photos, which are georeferenced in UTM. Accurate to well under a centimetre inside a zone.
/// </summary>
public static class Utm
{
    private const double A = 6378137.0;
    private const double F = 1 / 298.257223563;
    private const double K0 = 0.9996;
    private static readonly double E2 = F * (2 - F);
    private static readonly double Ep2 = E2 / (1 - E2);

    public static double CentralMeridian(int zone) => (zone - 1) * 6 - 180 + 3;

    public static (double Easting, double Northing) FromLatLng(double lat, double lng, int zone, bool north)
    {
        var phi = lat * Math.PI / 180;
        var lambda = (lng - CentralMeridian(zone)) * Math.PI / 180;
        var sin = Math.Sin(phi);
        var cos = Math.Cos(phi);
        var tan = Math.Tan(phi);
        var n = A / Math.Sqrt(1 - E2 * sin * sin);
        var t = tan * tan;
        var c = Ep2 * cos * cos;
        var a = cos * lambda;
        var m = Meridian(phi);

        var easting = K0 * n * (a + (1 - t + c) * Math.Pow(a, 3) / 6 + (5 - 18 * t + t * t + 72 * c - 58 * Ep2) * Math.Pow(a, 5) / 120) + 500000;
        var northing = K0 * (m + n * tan * (a * a / 2 + (5 - t + 9 * c + 4 * c * c) * Math.Pow(a, 4) / 24
                        + (61 - 58 * t + t * t + 600 * c - 330 * Ep2) * Math.Pow(a, 6) / 720));
        if (!north)
            northing += 10000000;
        return (easting, northing);
    }

    public static (double Lat, double Lng) ToLatLng(double easting, double northing, int zone, bool north)
    {
        var x = easting - 500000;
        var y = north ? northing : northing - 10000000;
        var m = y / K0;
        var mu = m / (A * (1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * Math.Pow(E2, 3) / 256));
        var e1 = (1 - Math.Sqrt(1 - E2)) / (1 + Math.Sqrt(1 - E2));
        var phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
                   + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                   + 151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu)
                   + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);
        var sin = Math.Sin(phi1);
        var cos = Math.Cos(phi1);
        var tan = Math.Tan(phi1);
        var n1 = A / Math.Sqrt(1 - E2 * sin * sin);
        var t1 = tan * tan;
        var c1 = Ep2 * cos * cos;
        var r1 = A * (1 - E2) / Math.Pow(1 - E2 * sin * sin, 1.5);
        var d = x / (n1 * K0);

        var lat = phi1 - n1 * tan / r1 * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * Ep2) * Math.Pow(d, 4) / 24
                  + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * Ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        var lng = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
                   + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * Ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return (lat * 180 / Math.PI, CentralMeridian(zone) + lng * 180 / Math.PI);
    }

    private static double Meridian(double phi) =>
        A * ((1 - E2 / 4 - 3 * E2 * E2 / 64 - 5 * Math.Pow(E2, 3) / 256) * phi
             - (3 * E2 / 8 + 3 * E2 * E2 / 32 + 45 * Math.Pow(E2, 3) / 1024) * Math.Sin(2 * phi)
             + (15 * E2 * E2 / 256 + 45 * Math.Pow(E2, 3) / 1024) * Math.Sin(4 * phi)
             - 35 * Math.Pow(E2, 3) / 3072 * Math.Sin(6 * phi));
}
