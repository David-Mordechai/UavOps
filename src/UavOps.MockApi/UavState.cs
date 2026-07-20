namespace UavOps.MockApi;

/// <summary>
/// In-memory fake UAV state. Intentionally minimal — just enough to return
/// plausible responses so the agent/tool pipeline can be built and demoed
/// before the real UAV control application is available.
/// </summary>
public sealed class UavState
{
    private readonly Lock _lock = new();

    public double Lat { get; private set; } = 31.801447;
    public double Lng { get; private set; } = 34.643497;
    public int SpeedKts { get; private set; } = 105;
    public int AltitudeFt { get; private set; } = 4000;
    public string Mode { get; private set; } = "Orbiting";
    public string? PayloadLockedOn { get; private set; }
    public List<Waypoint> Waypoints { get; private set; } = [];

    public void Navigate(string location, double lat, double lng)
    {
        lock (_lock)
        {
            Lat = lat;
            Lng = lng;
            Mode = "Transiting";
        }
    }

    public void SetSpeed(int speedKts)
    {
        lock (_lock) { SpeedKts = speedKts; }
    }

    public void SetAltitude(int altitudeFt)
    {
        lock (_lock) { AltitudeFt = altitudeFt; }
    }

    public void ReturnToLaunch()
    {
        lock (_lock)
        {
            Mode = "ReturningToLaunch";
        }
    }

    public void PointPayload(string location)
    {
        lock (_lock) { PayloadLockedOn = location; }
    }

    public void ResetPayload()
    {
        lock (_lock) { PayloadLockedOn = null; }
    }

    public void UploadWaypoints(List<Waypoint> waypoints)
    {
        lock (_lock) { Waypoints = waypoints; }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_lock)
        {
            return new TelemetrySnapshot(Lat, Lng, SpeedKts, AltitudeFt, Mode, PayloadLockedOn);
        }
    }
}

public sealed record Waypoint(double Lat, double Lng, int AltitudeFt);

public sealed record TelemetrySnapshot(
    double Lat,
    double Lng,
    int SpeedKts,
    int AltitudeFt,
    string Mode,
    string? PayloadLockedOn);
