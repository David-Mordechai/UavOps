namespace UavOps.MockApi;

/// <summary>
/// In-memory fake UAV state for one vehicle. Intentionally minimal — just enough to return
/// plausible responses so the agent/tool pipeline can be built and demoed before the real UAV
/// control application is available. One instance per tail number, held by <see cref="UavRegistry"/>.
/// </summary>
public sealed class UavState(double startLat, double startLng)
{
    private readonly Lock _lock = new();

    public double Lat { get; private set; } = startLat;
    public double Lng { get; private set; } = startLng;
    public int SpeedKts { get; private set; } = 105;
    public int AltitudeFt { get; private set; } = 4000;
    public string Mode { get; private set; } = "Orbiting";
    public string? PayloadLockedOn { get; private set; }
    public List<Waypoint> Waypoints { get; private set; } = [];

    // Ground Data Terminal (antenna/datalink) state — placeholder fields until real GDT tool
    // definitions are provided; kept on the vehicle state rather than a separate class since
    // this is explicitly throwaway.
    public string GdtLinkState { get; private set; } = "Connected";
    public int GdtSignalStrengthPercent { get; private set; } = 92;
    public string GdtTrackingMode { get; private set; } = "Auto";

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

    public void SetGdtTrackingMode(string mode)
    {
        lock (_lock) { GdtTrackingMode = mode; }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_lock)
        {
            return new TelemetrySnapshot(Lat, Lng, SpeedKts, AltitudeFt, Mode, PayloadLockedOn);
        }
    }

    public GdtLinkStatus GdtStatus()
    {
        lock (_lock)
        {
            return new GdtLinkStatus(GdtLinkState, GdtSignalStrengthPercent, GdtTrackingMode);
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

public sealed record GdtLinkStatus(string LinkState, int SignalStrengthPercent, string TrackingMode);
