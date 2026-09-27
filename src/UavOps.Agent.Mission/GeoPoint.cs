namespace UavOps.Agent.Mission;

public readonly record struct GeoPoint(double Lat, double Lng)
{
    public override string ToString() => FormattableString.Invariant($"{Lat:F5}, {Lng:F5}");
}
