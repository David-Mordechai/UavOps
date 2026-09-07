// The 6 real fleet tools + fake fleet state - copied from eval/single-agent-baseline-dotnet's
// already-proven (8/8) FleetTools/UavState/Fleet, unchanged in behavior, so this lab's baseline
// step (no retrieval, small tool count) starts from a known-good foundation rather than a new
// untested implementation.
using System.ComponentModel;
using Microsoft.Extensions.AI;

sealed class UavState
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int SpeedKts { get; set; } = 105;
    public int AltitudeFt { get; set; } = 4000;
    public string Mode { get; set; } = "Orbiting";
    public string? PayloadLockedOn { get; set; }

    public override string ToString()
    {
        var payload = PayloadLockedOn is null ? "None" : $"'{PayloadLockedOn}'";
        return $"{{'lat': {Lat}, 'lng': {Lng}, 'speedKts': {SpeedKts}, 'altitudeFt': {AltitudeFt}, 'mode': '{Mode}', 'payloadLockedOn': {payload}}}";
    }
}

static class Fleet
{
    public static Dictionary<string, UavState> CreateDefault() => new()
    {
        ["UAV-1"] = new UavState { Lat = 31.801447, Lng = 34.643497 },
        ["UAV-2"] = new UavState { Lat = 31.798000, Lng = 34.639000 },
        ["UAV-3"] = new UavState { Lat = 31.805000, Lng = 34.648000 },
    };
}

sealed class FleetTools(Dictionary<string, UavState> fleet)
{
    private static readonly Dictionary<string, (double Lat, double Lng)> KnownPoints = new()
    {
        ["alpha"] = (31.81, 34.66),
        ["bravo"] = (31.79, 34.62),
        ["home"] = (31.80, 34.64),
    };

    private static (double Lat, double Lng)? ResolvePoint(string name)
    {
        var key = name.ToLowerInvariant().Replace("target ", "").Trim();
        return KnownPoints.TryGetValue(key, out var point) ? point : null;
    }

    private static void Log(string name, object args, object result) =>
        Console.WriteLine($"  {name}({Describe(args)}) -> {result}");

    private static string Describe(object args) =>
        "{" + string.Join(", ", args.GetType().GetProperties().Select(p => $"'{p.Name}': {FormatValue(p.GetValue(args))}")) + "}";

    private static string FormatValue(object? value) => value switch
    {
        null => "None",
        string s => $"'{s}'",
        _ => value.ToString()!,
    };

    [Description("List all known UAVs by tail number with a brief status summary for each.")]
    public object ListFleet()
    {
        var result = fleet.Select(kv => new { tailNumber = kv.Key, mode = kv.Value.Mode, lat = kv.Value.Lat, lng = kv.Value.Lng }).ToList();
        Log(nameof(ListFleet), new { }, "[" + string.Join(", ", result.Select(r => $"{{'tailNumber': '{r.tailNumber}', 'mode': '{r.mode}', 'lat': {r.lat}, 'lng': {r.lng}}}")) + "]");
        return result;
    }

    [Description("Get a UAV's current position, speed, altitude, and mode.")]
    public object GetTelemetry(
        [Description("The tail number of the UAV to query, e.g. 'UAV-1'. Must be one of the known UAVs.")] string tailNumber)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(GetTelemetry), new { tailNumber }, err);
            return err;
        }
        Log(nameof(GetTelemetry), new { tailNumber }, state);
        return state;
    }

    [Description("Send a UAV to a named location.")]
    public object Navigate(
        [Description("The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs.")] string tailNumber,
        [Description("Name of a known point, e.g. 'home', 'alpha', 'bravo'.")] string location)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(Navigate), new { tailNumber, location }, err);
            return err;
        }
        var point = ResolvePoint(location);
        if (point is null)
        {
            var err = new { error = $"Unknown location '{location}'." };
            Log(nameof(Navigate), new { tailNumber, location }, err);
            return err;
        }
        lock (state)
        {
            state.Lat = point.Value.Lat;
            state.Lng = point.Value.Lng;
            state.Mode = "Transiting";
        }
        Log(nameof(Navigate), new { tailNumber, location }, state);
        return state;
    }

    [Description("Change a UAV's target cruise speed.")]
    public object SetSpeed(
        [Description("The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs.")] string tailNumber,
        [Description("Target speed in knots.")] int speedKts)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(SetSpeed), new { tailNumber, speedKts }, err);
            return err;
        }
        lock (state) { state.SpeedKts = speedKts; }
        Log(nameof(SetSpeed), new { tailNumber, speedKts }, state);
        return state;
    }

    [Description("Change a UAV's target altitude.")]
    public object SetAltitude(
        [Description("The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs.")] string tailNumber,
        [Description("Target altitude in feet.")] int altitudeFt)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(SetAltitude), new { tailNumber, altitudeFt }, err);
            return err;
        }
        lock (state) { state.AltitudeFt = altitudeFt; }
        Log(nameof(SetAltitude), new { tailNumber, altitudeFt }, state);
        return state;
    }

    [Description("Point a UAV's sensor/gimbal at a named location.")]
    public object PointPayload(
        [Description("The tail number of the UAV to command, e.g. 'UAV-1'. Must be one of the known UAVs.")] string tailNumber,
        [Description("Name of a known point to look at, e.g. 'home', 'alpha', 'bravo'.")] string location)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(PointPayload), new { tailNumber, location }, err);
            return err;
        }
        if (ResolvePoint(location) is null)
        {
            var err = new { error = $"Unknown location '{location}'." };
            Log(nameof(PointPayload), new { tailNumber, location }, err);
            return err;
        }
        lock (state) { state.PayloadLockedOn = location; }
        Log(nameof(PointPayload), new { tailNumber, location }, state);
        return state;
    }

    public AITool[] AsTools() =>
    [
        AIFunctionFactory.Create(ListFleet),
        AIFunctionFactory.Create(GetTelemetry),
        AIFunctionFactory.Create(Navigate),
        AIFunctionFactory.Create(SetSpeed),
        AIFunctionFactory.Create(SetAltitude),
        AIFunctionFactory.Create(PointPayload),
    ];
}
