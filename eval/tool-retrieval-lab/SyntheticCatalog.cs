// Deterministic synthetic distractor-tool generator. Produces plausible-sounding fake tools
// across a few fake domains, some deliberately near-duplicate to the 6 real fleet tools (to
// create genuine "which one did they mean" difficulty for retrieval ranking), so the lab can be
// scaled from ~20 to ~1000 total tools without the real UAV domain needing to actually have that
// many operations. Never mutates real state if called - see DistractorTools.Invoke.
using System.ComponentModel;
using Microsoft.Extensions.AI;

static class SyntheticCatalog
{
    private static readonly string[] Nouns =
    [
        "Payload", "Sensor", "Gimbal", "Beacon", "Relay", "Actuator", "Manifest", "Waypoint",
        "Geofence", "Transponder", "Battery", "Antenna", "Camera", "Winch", "Ballast", "Rotor",
    ];

    private static readonly string[] Verbs =
    [
        "Set", "Get", "Start", "Stop", "Reset", "Adjust", "Query", "Enable", "Disable", "Calibrate",
    ];

    // Deliberately near-duplicate to the real 6 fleet tools' names/descriptions - the genuine
    // "distractor" difficulty a retrieval ranker needs to survive, per the "chance-corrected"
    // paper's easy/medium/hard framing (these are the "hard" tier).
    private static readonly (string Name, string Description)[] HardDistractors =
    [
        ("SetGimbalSpeed", "Change the gimbal's rotation speed."),
        ("SetSensorAltitude", "Change a sensor mount's target altitude offset."),
        ("NavigateRelay", "Send a relay station to a named location."),
        ("PointBeacon", "Point a beacon's directional antenna at a named location."),
        ("GetManifestTelemetry", "Get a cargo manifest's current status and location."),
        ("ListWaypoints", "List all known named waypoints with a brief status summary for each."),
    ];

    // Real tool names/descriptions copied verbatim from this repo's own
    // AgentsConfig/MaintenanceAgent/WatchdogConfigAgent.yaml - a third genuinely real domain
    // (service-definition config) as distractor noise, distinct from the WatchdogTools/
    // SimulatorInfraTools domains below which get REAL implementations + their own scenarios
    // instead of staying generic no-ops (see this project's own history - these used to all be
    // generic no-ops here, which meant they were never actually exercised, only ever avoided).
    private static readonly (string Name, string Description)[] RealDomainDistractors =
    [
        ("RunSimulatorLesson", "Starts the operator's chosen training lesson script running in the background on this machine and returns immediately with status 'queued'."),
        ("ListConfigurations", "Lists the names of every watchdog configuration that exists, e.g. 'Flight', 'Simulator'."),
        ("ListConfiguredServices", "Lists every service defined in one named watchdog configuration, with its executable path and other settings."),
        ("AddConfiguredService", "Adds a new service to a named watchdog configuration. Requires operator confirmation before it runs."),
        ("UpdateConfiguredService", "Changes one or more fields of an existing service in a named watchdog configuration. Requires operator confirmation before it runs."),
        ("RemoveConfiguredService", "Removes an existing service from a named watchdog configuration. Requires operator confirmation before it runs."),
    ];

    /// <summary>Generates exactly <paramref name="count"/> distractor tools (all no-op, logged if
    /// called), deterministic for a given seed - same seed + count always produces the same set,
    /// so a regression run is reproducible.</summary>
    public static List<AITool> Generate(int count, int seed = 42)
    {
        var rng = new Random(seed);
        var tools = new List<AITool>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, description) in HardDistractors.Concat(RealDomainDistractors))
        {
            if (tools.Count >= count) return tools;
            usedNames.Add(name);
            tools.Add(BuildDistractor(name, description));
        }

        // BUG FIXED: Nouns x Verbs only has 160 unique combinations - at large `count` (e.g. 994
        // needed for a 1000-tool catalog) a bare random-retry-on-collision loop can never terminate
        // once that small pool is exhausted (confirmed live: spun at ~100% CPU for 27+ minutes with
        // zero forward progress before being killed). A numeric suffix guarantees a free name is
        // always found in bounded steps, so this now always terminates in exactly `count` outer
        // iterations regardless of how large `count` is.
        while (tools.Count < count)
        {
            var noun = Nouns[rng.Next(Nouns.Length)];
            var verb = Verbs[rng.Next(Verbs.Length)];
            var baseName = $"{verb}{noun}";
            var name = baseName;
            for (var suffix = 2; !usedNames.Add(name); suffix++)
            {
                name = $"{baseName}{suffix}";
            }
            var description = $"{verb} the {noun.ToLowerInvariant()}'s current configuration or status.";
            tools.Add(BuildDistractor(name, description));
        }

        return tools;
    }

    private static AITool BuildDistractor(string name, string description)
    {
        // A closure (not a shared static method) so each distractor's own log line names itself -
        // otherwise every distractor call would print identically and be impossible to tell apart.
        object Invoke(
            [Description("Target identifier, e.g. a tail number or device name.")] string target,
            [Description("A value or setting to apply.")] string value)
        {
            Console.WriteLine($"  [DISTRACTOR CALLED] {name}({target}, {value})");
            return new { status = "ok" };
        }
        return AIFunctionFactory.Create((Func<string, string, object>)Invoke, name, description);
    }
}
