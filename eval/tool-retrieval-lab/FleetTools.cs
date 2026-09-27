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
    public string? SearchZone { get; set; }
    public string? SearchPrompt { get; set; }
    public bool MissionStarted { get; set; }

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
        ["997"] = new UavState { Lat = 31.801447, Lng = 34.643497 },
        ["998"] = new UavState { Lat = 31.798000, Lng = 34.639000 },
        ["999"] = new UavState { Lat = 31.805000, Lng = 34.648000 },
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
        [Description("The tail number of the UAV to query, e.g. '997'. Must be one of the known UAVs.")] string tailNumber)
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
        [Description("The tail number of the UAV to command, e.g. '997'. Must be one of the known UAVs.")] string tailNumber,
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
        [Description("The tail number of the UAV to command, e.g. '997'. Must be one of the known UAVs.")] string tailNumber,
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
        [Description("The tail number of the UAV to command, e.g. '997'. Must be one of the known UAVs.")] string tailNumber,
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
        [Description("The tail number of the UAV to command, e.g. '997'. Must be one of the known UAVs.")] string tailNumber,
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

    // Added for the "return-home-with-summary" scenario (an operator-reported retrieval miss - see
    // Scenarios.cs). Description copied from UavOps.Agent.McpMoav/ToolsConfig.yaml so this tool
    // ranks the way production's does.
    [Description("Command a UAV to return to and land at its launch point (RTL) - this is what phrasing like 'bring it home', 'send it home', 'back to base', and 'return to launch' actually means, even when the operator's exact word is 'home' or 'base' rather than 'launch point'. Use this, not Navigate, for any of that phrasing - Navigate would only fly to the 'home' coordinate as an ordinary waypoint without landing, which is not what a 'bring it home' request means.")]
    public object ReturnToLaunch(
        [Description("The tail number of the UAV to command, e.g. '997'. Must be one of the known UAVs.")] string tailNumber)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(ReturnToLaunch), new { tailNumber }, err);
            return err;
        }
        lock (state) { state.Mode = "ReturningToLaunch"; }
        Log(nameof(ReturnToLaunch), new { tailNumber }, state);
        return state;
    }

    // AOI search-mission tools for the "aoi-search" scenario. Descriptions copied from
    // UavOps.Agent.McpMoav/ToolsConfig.yaml so they rank the way production's do.
    [Description("Prepare one UAV to search a named AOI zone for something: plans a search route covering the zone, uploads it to the UAV, and tells the UAV's onboard agent what to look for. Use for requests like 'enter AOI zone ZoneA and search for a white van' or 'search zone B for a red car'. It does NOT start flying: afterwards, ask the operator whether to start the mission, and only start it with StartMission. 'Enter a zone' here means search it - do not use Navigate for a zone name; Navigate only flies to a known point like 'alpha'.")]
    public object PrepareAoiSearch(
        [Description("The tail number of the ONE UAV to search with, e.g. '997'. Must be one of the known UAVs - never guess one, and never 'ALL' or several UAVs: each search route is for a single UAV.")] string tailNumber,
        [Description("Name of the AOI zone to search, as the operator said it, e.g. 'ZoneA'. Call ListAoiZones if you need the valid names.")] string zoneName,
        [Description("What to look for, in the operator's own words, e.g. 'white van'. This is the object to find, not a location.")] string targetDescription)
    {
        if (!fleet.TryGetValue(tailNumber, out var state))
        {
            var err = new { error = $"Unknown UAV '{tailNumber}'." };
            Log(nameof(PrepareAoiSearch), new { tailNumber, zoneName, targetDescription }, err);
            return err;
        }
        lock (state)
        {
            state.SearchZone = zoneName;
            state.SearchPrompt = targetDescription;
        }
        var result = new { tailNumber, zoneName, searchTarget = targetDescription, waypointsUploaded = 12, started = false, nextStep = "Ask the operator whether to start the mission; start it only with StartMission." };
        Log(nameof(PrepareAoiSearch), new { tailNumber, zoneName, targetDescription }, result);
        return result;
    }

    [Description("Start a UAV flying the search route already uploaded to it (by PrepareAoiSearch or UploadRoute) - what 'start the mission', 'go', or 'begin the search' means after a search was prepared. Not for the training simulator or its lessons, and not for watchdog services.")]
    public object StartMission(
        [Description("The tail number of the UAV whose mission to start, e.g. '997'. Must be one of the known UAVs - never guess one.")] string tailNumber)
    {
        if (!fleet.TryGetValue(tailNumber, out var state) || state.SearchZone is null)
        {
            var err = new { error = $"{tailNumber} has no route uploaded to fly." };
            Log(nameof(StartMission), new { tailNumber }, err);
            return err;
        }
        lock (state)
        {
            state.MissionStarted = true;
            state.Mode = "Searching";
        }
        Log(nameof(StartMission), new { tailNumber }, state);
        return state;
    }

    [Description("List the named AOI (area of interest) zones that can be searched, with each zone's area and centre point.")]
    public object ListAoiZones()
    {
        var result = new[] { new { name = "ZoneA", areaSqKm = 1.175 }, new { name = "ZoneB", areaSqKm = 0.52 } };
        Log(nameof(ListAoiZones), new { }, result);
        return result;
    }

    public AITool[] AsTools() =>
    [
        AIFunctionFactory.Create(ListFleet),
        AIFunctionFactory.Create(GetTelemetry),
        AIFunctionFactory.Create(Navigate),
        AIFunctionFactory.Create(SetSpeed),
        AIFunctionFactory.Create(SetAltitude),
        AIFunctionFactory.Create(PointPayload),
        AIFunctionFactory.Create(ReturnToLaunch),
        AIFunctionFactory.Create(PrepareAoiSearch),
        AIFunctionFactory.Create(StartMission),
        AIFunctionFactory.Create(ListAoiZones),
    ];
}
