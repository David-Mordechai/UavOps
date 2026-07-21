namespace UavOps.MockApi;

/// <summary>
/// Stand-in fleet registry — maps a tail number to that vehicle's in-memory state. A real UAV
/// control application would own vehicle discovery/registration; this mock just seeds a fixed
/// fleet so multi-UAV tool calls have something real to address.
/// </summary>
public sealed class UavRegistry
{
    private readonly Dictionary<string, UavState> _vehicles;

    public UavRegistry()
    {
        _vehicles = new Dictionary<string, UavState>(StringComparer.OrdinalIgnoreCase)
        {
            ["UAV-1"] = new UavState(31.801447, 34.643497),
            ["UAV-2"] = new UavState(31.798000, 34.639000),
            ["UAV-3"] = new UavState(31.805000, 34.648000),
        };
    }

    public bool TryGet(string tailNumber, out UavState state) =>
        _vehicles.TryGetValue(tailNumber, out state!);

    public IReadOnlyDictionary<string, UavState> All => _vehicles;
}
