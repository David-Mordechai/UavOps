// A small, fixed, hand-written set of varied conversations - deliberately NOT a combinatorial
// generator (see the lab's plan doc for why: simplicity, and "start small" per explicit direction).
// Scenarios cover 3 real domains now (fleet, watchdog, simulator-infra), not just fleet - each
// domain's own tools act as genuine distractors for the others' scenarios.
sealed record ScenarioTurn(string Text, string[] RequiredTools);

sealed record LabState(Dictionary<string, UavState> Fleet, WatchdogState Watchdog, SimulatorState Simulator);

sealed record Scenario(string Name, List<ScenarioTurn> Turns, Func<LabState, bool> Check);

static class Scenarios
{
    private static readonly ScenarioTurn Greeting = new("hi my name is David and I am today Operator", []);
    private static readonly ScenarioTurn ListUavs = new("What uavs do we have?", ["ListFleet"]);

    public static List<Scenario> All() =>
    [
        new Scenario(
            "compound-3uav",
            [
                Greeting,
                ListUavs,
                new ScenarioTurn(
                    "fly all of them to target alpha and set speed to 250 and altitude to 3000 to all of them also point there payloads there",
                    ["Navigate", "SetSpeed", "SetAltitude", "PointPayload"]),
            ],
            state => state.Fleet.Values.All(s => s.SpeedKts == 250 && s.AltitudeFt == 3000 && s.Mode == "Transiting" && s.PayloadLockedOn == "alpha")),

        new Scenario(
            "single-uav",
            [
                Greeting,
                ListUavs,
                new ScenarioTurn(
                    "fly UAV-1 to target alpha and set its speed to 250",
                    ["Navigate", "SetSpeed"]),
            ],
            state =>
                state.Fleet["UAV-1"].SpeedKts == 250 && state.Fleet["UAV-1"].Mode == "Transiting" &&
                state.Fleet["UAV-2"].SpeedKts == 105 && state.Fleet["UAV-2"].Mode == "Orbiting" &&
                state.Fleet["UAV-3"].SpeedKts == 105 && state.Fleet["UAV-3"].Mode == "Orbiting"),

        new Scenario(
            "synonym-phrasing",
            [
                Greeting,
                ListUavs,
                new ScenarioTurn(
                    "send every one of our drones to target alpha and set their speed to 250 and altitude to 3000",
                    ["Navigate", "SetSpeed", "SetAltitude"]),
            ],
            state => state.Fleet.Values.All(s => s.SpeedKts == 250 && s.AltitudeFt == 3000 && s.Mode == "Transiting")),

        new Scenario(
            "pronoun-callback",
            [
                Greeting,
                ListUavs,
                new ScenarioTurn("fly all of them to target alpha", ["Navigate"]),
                new ScenarioTurn("now set their speed to 300 and altitude to 4000", ["SetSpeed", "SetAltitude"]),
            ],
            state => state.Fleet.Values.All(s => s.SpeedKts == 300 && s.AltitudeFt == 4000 && s.Mode == "Transiting")),

        // Cross-domain: correct real tool must be found and called out of the whole catalog
        // (fleet + simulator tools now act as distractors), not just avoided - the direction the
        // 4 scenarios above never actually exercised.
        new Scenario(
            "watchdog-health",
            [
                Greeting,
                new ScenarioTurn("is the watchdog healthy right now?", ["GetServicesHealth"]),
            ],
            state => state.Watchdog.HealthChecked),

        new Scenario(
            "watchdog-restart",
            [
                Greeting,
                new ScenarioTurn("restart the watchdog service", ["RestartService"]),
            ],
            state => state.Watchdog.ServiceState == "Running"),

        new Scenario(
            "simulator-startup",
            [
                Greeting,
                new ScenarioTurn(
                    "start the simulator and tell me what training lessons are available",
                    ["EnsureVmwareHostRunning", "EnsureSimulatorVmRunning", "ListSimulatorLessons"]),
            ],
            state => state.Simulator.HostRunning && state.Simulator.VmRunning && state.Simulator.LessonsListed),
    ];
}
