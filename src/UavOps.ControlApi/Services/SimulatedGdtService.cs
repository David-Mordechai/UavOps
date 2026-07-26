using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// In-memory, simulated <see cref="IGdtService"/>. Explicitly named "Simulated" — placeholder
/// mock tools pending the real GDT tool definitions. Valid tail numbers are sourced from
/// <see cref="IUavFleetService"/> so the fleet is the single source of truth for which UAVs exist.
/// </summary>
public sealed class SimulatedGdtService(IUavFleetService fleetService) : IGdtService
{
    private sealed class LinkState
    {
        public string LinkStateName = "Connected";
        public int SignalStrengthPercent = 92;
        public string TrackingMode = "Auto";
    }

    private readonly Dictionary<string, LinkState> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private bool TryGetOrCreate(string tailNumber, out LinkState state)
    {
        if (!fleetService.ListFleet().Any(v => string.Equals(v.TailNumber, tailNumber, StringComparison.OrdinalIgnoreCase)))
        {
            state = null!;
            return false;
        }

        lock (_lock)
        {
            if (!_links.TryGetValue(tailNumber, out state!))
            {
                state = new LinkState();
                _links[tailNumber] = state;
            }
        }

        return true;
    }

    public (bool Found, GdtLinkStatus? Status) GetLinkStatus(string tailNumber)
    {
        if (!TryGetOrCreate(tailNumber, out var state))
        {
            return (false, null);
        }

        return (true, new GdtLinkStatus(state.LinkStateName, state.SignalStrengthPercent, state.TrackingMode));
    }

    public (bool Found, GdtLinkStatus? Status, string? Error) SetTrackingMode(string tailNumber, string mode)
    {
        if (!TryGetOrCreate(tailNumber, out var state))
        {
            return (false, null, null);
        }

        if (!string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "Manual", StringComparison.OrdinalIgnoreCase))
        {
            return (true, null, "mode must be 'Auto' or 'Manual'.");
        }

        lock (_lock) { state.TrackingMode = mode; }
        return (true, new GdtLinkStatus(state.LinkStateName, state.SignalStrengthPercent, state.TrackingMode), null);
    }
}
