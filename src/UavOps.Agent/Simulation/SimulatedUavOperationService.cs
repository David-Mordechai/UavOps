using UavOps.Agent.Operations;

namespace UavOps.Agent.Simulation;

/// <summary>
/// In-memory, simulated <see cref="IOperationService"/> — stands in for real vehicle command/
/// telemetry and ground-data-terminal state until a real implementation (talking to actual
/// autopilots/hardware) is available. Explicitly named "Simulated" so the placeholder is never
/// mistaken for the real thing. No real async work happens here — every method wraps its result
/// in <see cref="Task.FromResult{TResult}"/> purely to satisfy <see cref="IOperationService"/>.
/// </summary>
public sealed class SimulatedUavOperationService : IOperationService
{
    private sealed class VehicleState(double startLat, double startLng)
    {
        public readonly object Lock = new();
        public double Lat = startLat;
        public double Lng = startLng;
        public int SpeedKts = 105;
        public int AltitudeFt = 4000;
        public string Mode = "Orbiting";
        public string? PayloadLockedOn;
        public List<Waypoint> Waypoints = [];

        public TelemetrySnapshot Snapshot()
        {
            lock (Lock)
            {
                return new TelemetrySnapshot(Lat, Lng, SpeedKts, AltitudeFt, Mode, PayloadLockedOn);
            }
        }
    }

    private sealed class LinkState
    {
        public string LinkStateName = "Connected";
        public int SignalStrengthPercent = 92;
        public string TrackingMode = "Auto";
    }

    private readonly Dictionary<string, VehicleState> _fleet;
    private readonly Dictionary<string, LinkState> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _linksLock = new();

    public SimulatedUavOperationService()
    {
        _fleet = new Dictionary<string, VehicleState>(StringComparer.OrdinalIgnoreCase)
        {
            ["UAV-1"] = new VehicleState(31.801447, 34.643497),
            ["UAV-2"] = new VehicleState(31.798000, 34.639000),
            ["UAV-3"] = new VehicleState(31.805000, 34.648000),
        };
    }

    public Task<OperationResult> ListFleet(CancellationToken cancellationToken)
    {
        var summaries = _fleet.Select(kvp =>
        {
            var snap = kvp.Value.Snapshot();
            return new UavSummary(kvp.Key, snap.Mode, snap.Lat, snap.Lng);
        }).ToList();

        return Task.FromResult(OperationResult.Ok(summaries));
    }

    public Task<OperationResult> GetTelemetry(string tailNumber, CancellationToken cancellationToken) =>
        Task.FromResult(_fleet.TryGetValue(tailNumber, out var v) ? OperationResult.Ok(v.Snapshot()) : OperationResult.NotFound(tailNumber));

    public Task<OperationResult> Navigate(string tailNumber, string location, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        if (!KnownPoints.TryResolve(location, out var lat, out var lng))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown location '{location}'."));
        }

        lock (v.Lock)
        {
            v.Lat = lat;
            v.Lng = lng;
            v.Mode = "Transiting";
        }

        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> SetSpeed(string tailNumber, int speedKts, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        if (speedKts is < 1 or > 500)
        {
            return Task.FromResult(OperationResult.Invalid("speedKts must be between 1 and 500."));
        }

        lock (v.Lock) { v.SpeedKts = speedKts; }
        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> SetAltitude(string tailNumber, int altitudeFt, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        if (altitudeFt is < 0 or > 60000)
        {
            return Task.FromResult(OperationResult.Invalid("altitudeFt must be between 0 and 60000."));
        }

        lock (v.Lock) { v.AltitudeFt = altitudeFt; }
        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> ReturnToLaunch(string tailNumber, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        lock (v.Lock) { v.Mode = "ReturningToLaunch"; }
        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> PointPayload(string tailNumber, string location, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        if (!KnownPoints.TryResolve(location, out _, out _))
        {
            return Task.FromResult(OperationResult.Invalid($"Unknown location '{location}'."));
        }

        lock (v.Lock) { v.PayloadLockedOn = location; }
        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> ResetPayload(string tailNumber, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        lock (v.Lock) { v.PayloadLockedOn = null; }
        return Task.FromResult(OperationResult.Ok(v.Snapshot()));
    }

    public Task<OperationResult> UploadWaypoints(string tailNumber, List<Waypoint> waypoints, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        lock (v.Lock) { v.Waypoints = waypoints; }
        return Task.FromResult(OperationResult.Ok(waypoints.Count));
    }

    public Task<OperationResult> GetMissionStatus(string tailNumber, CancellationToken cancellationToken)
    {
        if (!_fleet.TryGetValue(tailNumber, out var v))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        lock (v.Lock)
        {
            return Task.FromResult(OperationResult.Ok(new MissionStatus(v.Mode, v.Waypoints.Count)));
        }
    }

    public Task<OperationResult> GetLinkStatus(string tailNumber, CancellationToken cancellationToken)
    {
        if (!TryGetOrCreateLink(tailNumber, out var state))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        return Task.FromResult(OperationResult.Ok(new GdtLinkStatus(state.LinkStateName, state.SignalStrengthPercent, state.TrackingMode)));
    }

    public Task<OperationResult> SetTrackingMode(string tailNumber, string mode, CancellationToken cancellationToken)
    {
        if (!TryGetOrCreateLink(tailNumber, out var state))
        {
            return Task.FromResult(OperationResult.NotFound(tailNumber));
        }

        if (!string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(OperationResult.Invalid("mode must be 'Auto' or 'Manual'."));
        }

        lock (_linksLock) { state.TrackingMode = mode; }
        return Task.FromResult(OperationResult.Ok(new GdtLinkStatus(state.LinkStateName, state.SignalStrengthPercent, state.TrackingMode)));
    }

    private bool TryGetOrCreateLink(string tailNumber, out LinkState state)
    {
        if (!_fleet.ContainsKey(tailNumber))
        {
            state = null!;
            return false;
        }

        lock (_linksLock)
        {
            if (!_links.TryGetValue(tailNumber, out state!))
            {
                state = new LinkState();
                _links[tailNumber] = state;
            }
        }

        return true;
    }
}
