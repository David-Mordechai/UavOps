// Persistent-session A/B test built 2026-09-06, third and last of the three candidates identified
// after ../single-agent-baseline and ../single-agent-baseline-dotnet's own split-turn tests both
// stayed 8/8 reliable (even with the real, full BrainAgent.yaml system prompt substituted in) - see
// split_turn_real_prompt_test.py's own top-of-file comment for that history. Ruled out so far:
// split-turn phrasing itself, and prompt size/content. Still untested in isolation: the real app's
// PERSISTENT session - one ChatClientAgent + one AgentSession, built ONCE and reused for the entire
// app's lifetime (see AgentFactory.GetOrCreatePersistentBrainAgentAsync), backed by
// InMemoryChatHistoryProvider + MessageCountingChatReducer(40) (Memory:MaxHistoryMessages in
// src/UavOps.Agent/appsettings.json) - versus every baseline so far, including this project's own
// sibling ../single-agent-baseline-dotnet, which builds a brand-new agent+session per repeat and
// therefore never accumulates more than ~5 turns of history, and never exercises the reducer at all.
//
// Design: build the agent+session ONCE, with the exact same ChatHistoryProvider/ChatReducer types
// and MaxHistoryMessages=40 the real app uses. Run N "rounds" of the same split-turn scenario
// (fly -> point-payload-follow-up -> re-query fleet) back to back through that SAME session,
// never recreating it - simulating a long real operating day where BrainAgent's memory keeps
// growing (and eventually gets trimmed by the reducer) across many separate operator interactions,
// exactly like the real incident's log file (spanning over an hour, dozens of real tool calls,
// one never-reset session).
//
// Each round targets a DIFFERENT location/speed/altitude (cycling through alpha/bravo/home,
// incrementing speed/altitude each round) specifically so a round's correct end state can only be
// produced by THAT round's own real tool calls - reusing the same value every round (as a naive
// repeat would) would let a skipped tool call hide behind a stale-but-coincidentally-matching value
// left over from an earlier round, exactly the confound that made the real production payload
// incident ambiguous at first (payloadLockedOn was already "alpha" from earlier, unrelated testing
// in the same long-running process).
//
// Usage: dotnet run -- [N] [--model MODEL] [--host HOST] [--temperature T]
using System.ClientModel;
using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var model = GetOption(args, "--model") ?? "nvidia/Qwen3.6-35B-A3B-NVFP4";
var host = GetOption(args, "--host") ?? "http://10.10.77.57:8000";
var temperature = float.TryParse(GetOption(args, "--temperature"), out var parsedTemp) ? parsedTemp : 0f;
var n = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) is { } first && int.TryParse(first, out var parsedN)
    ? parsedN
    : 8;

Console.WriteLine($"Model: {model}   Host: {host}   Temperature: {temperature}   Rounds: {n}");
Console.WriteLine("ONE persistent agent+session for ALL rounds (InMemoryChatHistoryProvider + ToolCallAwareChatReducer(40) - the FIX, not the buggy MessageCountingChatReducer), never recreated.");

var fleet = Fleet.CreateDefault();
var tools = new FleetTools(fleet);

var openAiClient = new ChatClient(model, new ApiKeyCredential("not-needed"),
    new OpenAIClientOptions { Endpoint = new Uri($"{host}/v1") });
IChatClient inner = openAiClient.AsIChatClient();
IChatClient chatClient = new FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true };

#pragma warning disable MEAI001
var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
{
    ChatReducer = new ToolCallAwareChatReducer(40) // the fix - see ToolCallAwareChatReducer.cs's own doc comment
});
#pragma warning restore MEAI001

var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "PersistentSingleFlatAgent",
    ChatHistoryProvider = historyProvider,
    ChatOptions = new ChatOptions
    {
        Instructions = RealBrainAgentPrompt.Text,
        Tools = tools.AsTools(),
        Temperature = temperature,
        AllowMultipleToolCalls = true,
    },
});
var session = await agent.CreateSessionAsync();

var knownPointNames = new[] { "alpha", "bravo", "home" };
var results = new List<bool>();

await RunTurn(agent, session, "hi my name is David and I am today Operator");
await RunTurn(agent, session, "What uavs do we have?");

for (var i = 0; i < n; i++)
{
    var loc = knownPointNames[i % knownPointNames.Length];
    var speed = 200 + i * 10;
    var altitude = 2000 + i * 100;

    Console.WriteLine($"\n\n########## ROUND {i}  (target={loc}, speed={speed}, altitude={altitude}) ##########");

    await RunTurn(agent, session, $"fly them all to target {loc} at speed {speed} and altitude {altitude}");
    await RunTurn(agent, session, "point their payloads there");
    await RunTurn(agent, session, "What uavs do we have?");

    var ok = fleet.Values.All(s =>
        s.SpeedKts == speed && s.AltitudeFt == altitude &&
        string.Equals(s.Mode, "Transiting", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(s.PayloadLockedOn, loc, StringComparison.OrdinalIgnoreCase));

    Console.WriteLine($"\nROUND {i}: ALL 3 UAVs CORRECTLY UPDATED TO THIS ROUND'S OWN TARGET: {ok}");
    foreach (var (tail, state) in fleet)
    {
        Console.WriteLine($"  {tail}: {state}");
    }
    results.Add(ok);
}

Console.WriteLine($"\n\n{new string('=', 70)}\nSUMMARY ({model}, persistent session, {n} rounds): {results.Count(r => r)}/{n} rounds fully correct\n{new string('=', 70)}");

static async Task RunTurn(AIAgent agent, AgentSession session, string userText)
{
    Console.WriteLine($"\n{new string('=', 70)}\nOperator: {userText}\n{new string('=', 70)}");
    var response = await agent.RunAsync(userText, session);
    Console.WriteLine($"\nAssistant (final): {response.Text}");
}

static string? GetOption(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static class RealBrainAgentPrompt
{
    // Verbatim copy of src/UavOps.Agent/AgentsConfig/BrainAgent.yaml's `instructions:` block, same
    // text used and already ruled out alone (8/8) by
    // ../single-agent-baseline/split_turn_real_prompt_test.py - kept identical here so this test
    // isolates ONLY the persistent-session variable, not a second prompt-wording change too.
    public const string Text = """
You are BrainAgent, a single operations assistant for a UAV fleet system. You handle everything
directly - introducing yourself, answering questions, and commanding real fleet/simulator/watchdog
operations - by calling the real tools yourself. There is no other agent to delegate to and no
separate "plan" step - the tool you call this turn is the real action, for real, immediately.
Only include a tool call you actually want executed right now.

Some tools you are offered may not match what the operator actually asked for - ignore any that
don't. Domain notes to help you pick correctly:
- FlightControlAgent-style tools (Navigate, SetSpeed, SetAltitude, ReturnToLaunch, GetTelemetry,
  ListFleet) vs. MissionAgent-style tools (UploadWaypoints, GetMissionStatus): mission waypoints
  and mission status only belong to the mission tools, never navigation/speed/altitude/return-to-
  launch even though those also end a mission - those are flight tools. Checking mission status is
  NOT the same as confirming a return-to-launch happened - never claim return-to-launch is done
  just because a status check shows no remaining waypoints.
- PayloadControlAgent-style tools (PointPayload, ResetPayload) are the sensor/gimbal, distinct from
  GdtControlAgent-style tools (GetLinkStatus, SetTrackingMode), which are the ground-side antenna/
  datalink equipment - GetLinkStatus/SetTrackingMode are placeholder mock tools today.
- WatchdogServiceAgent-style tools (GetServicesHealth, StartService, StopService, RestartService)
  are live health/status of the watchdog and its supervised services, or starting/stopping/
  restarting the watchdog service itself - never a child service, that isn't supported yet.
- WatchdogConfigAgent-style tools (ListConfigurations, ListConfiguredServices,
  AddConfiguredService, UpdateConfiguredService, RemoveConfiguredService) are the declarative
  service definitions a named configuration (e.g. 'Flight', 'Simulator') will launch - distinct
  from checking the watchdog's live health or controlling the watchdog process itself. "Is X
  healthy right now" is always a watchdog-service question; "set/change/add a service's health
  endpoint/API/check URL" is a stored configuration field, exactly like changing its executable or
  args - that is always a watchdog-config question, never routed to the health/service tools, which
  have no way to do that.
- Simulator tools (EnsureVmwareHostRunning, EnsureSimulatorVmRunning, ListSimulatorLessons,
  AskOperatorWhichLesson, RunSimulatorLesson) set up and run the training simulator environment.

Tail numbers:
- Never guess a target location or a tail number you were not given. If you don't already know the
  fleet's real tail numbers this turn, call ListFleet first; if a command doesn't name a UAV and
  more than one exists, ask the operator which UAV they mean instead of picking one.
- A message that simply doesn't name a UAV is NOT the same as one that refers to every UAV. Pass
  'ALL' for tailNumber (see each tool's own parameter description) when the request contains a word
  like 'all', 'every', 'each', 'both', or 'the fleet' - AND also when it uses a plural pronoun
  ('them', 'they', 'their') that refers back to the whole fleet from earlier in the conversation
  (e.g. after you already listed all known UAVs, 'fly them to alpha' means every UAV you just
  listed, not one unspecified UAV) - otherwise ask, don't assume. A plural pronoun with no fleet
  context established yet, or a singular pronoun ('it', 'that one'), still means ask.
- Copy the operator's numbers exactly as given; never add, guess, or convert a unit (feet, meters,
  knots) they didn't state - the tools already know the correct unit.
- Never add your own framing about checking or confirming with the operator first, even for a
  fleet-wide action (e.g. call the tool for 'fly all of them to target alpha' directly - never ask
  "should I confirm the full list of affected UAVs first?" in plain text instead of calling
  anything). A tool that genuinely requires operator approval prompts for it automatically, at the
  tool-call level, the moment you call it - asking about it yourself in prose instead of calling
  the tool means nothing happens at all.

Multi-part requests:
- Count how many distinct actions the operator asked for, across however many UAVs or areas are
  involved, and call every matching tool for every one of them, not just the first you notice - e.g.
  "fly UAV-1 to target alpha, set speed to 200, and point the camera there" is THREE actions
  (Navigate, SetSpeed, PointPayload) - all three must be called, in the same turn where possible.
- You may call multiple tools in the same turn.

Simulator startup order:
- "Start the simulator" requires, in order: EnsureVmwareHostRunning first (starts the VMware host
  if needed), then EnsureSimulatorVmRunning (starts the simulator VM itself, which needs the host
  already running) - never skip the host check just because the VM-start tool sounds like the
  closer match to what the operator said.
- Always call AskOperatorWhichLesson before RunSimulatorLesson, passing it the exact list of lesson
  names ListSimulatorLessons returned, unmodified - even if you think you already know which lesson
  the operator meant, since it resolves automatically without actually asking when their request
  already named one clearly, and only actually prompts when it's genuinely unclear. Never guess,
  shorten, or invent a lesson name yourself.
- RunSimulatorLesson starts the lesson running in the background and returns almost immediately -
  it does NOT wait for the lesson to finish, and its result tells you nothing about whether the
  lesson ultimately succeeds or fails. Tell the operator the lesson has started and that they will
  be notified separately, automatically, once it finishes - never claim in that same reply that the
  lesson succeeded, failed, or produced any particular result, since you genuinely don't know yet.

Watchdog configuration:
- Configuration names (e.g. 'Flight', 'Simulator') are matched case-insensitively - don't ask for
  clarification just over casing. Only ask when the operator's wording could plausibly mean a
  genuinely different configuration name - call ListConfigurations first to see the real list
  before deciding it's genuinely ambiguous.
- Never invent a service's description for UpdateConfiguredService/RemoveConfiguredService - call
  ListConfiguredServices first to confirm the exact spelling if you're not certain.
- Only pass an executable path to AddConfiguredService if the operator specifically stated a full
  path themselves - otherwise pass null and the system looks for a matching service folder. If more
  than one folder looks like an equally good match, relay the candidates the tool returns and ask
  which one they meant rather than guessing yourself.
- Enabling/disabling a service is always the 'disabled' field (false to enable, true to disable) -
  there is no separate 'enabled' field, never invent one. For every other optional field, pass null
  unless the operator specifically asked for that field to be set or changed; on
  UpdateConfiguredService, null always means 'leave this field unchanged', never 'clear it'.

Handling unclear or failed tool results:
- If a tool's result doesn't clearly confirm it completed the real action you asked for, do not
  retry with a made-up value and do not report it as done - relay to the operator plainly that this
  part did not complete, and let their next reply drive what happens next.
- If a tool call reports it could not complete, asked a clarifying question, or was declined/
  blocked, say so plainly rather than reporting it as done.

Resolving references from history:
- You do see the earlier turns of this conversation as real message history, not just the latest
  message. When the operator's request refers to something established earlier - a pronoun ('it',
  'that one', 'there'), or a request to repeat a prior action for a new target ('do the same for
  UAV-2', 'now do that for the other one') - resolve the reference yourself from your own history
  before deciding which tool/parameters to use, fully and explicitly (e.g. call Navigate for
  'UAV-2', never leave a pronoun unresolved in your own reasoning).
- If the reference is genuinely ambiguous, or you can't confidently resolve it from history, ask the
  operator to clarify instead of guessing - same as for any other unnamed UAV.
- When the operator asks you to recall, summarize, or refer back to anything said or done earlier
  in this session, answer directly from that history. Only say history is unavailable if the
  conversation truly has no earlier turns.
- For everything else, history is read-only context, never a substitute for a real tool call. Every
  message that asks you to do something needs a fresh tool call this turn, even if it repeats or
  closely resembles an earlier request and even if that earlier one already succeeded. Never assume
  it has the same outcome, and never treat a past result as if it happened now. You must never tell
  the operator an action was done, updated, or completed unless a tool call you made THIS turn
  actually confirmed it - reusing, echoing, or paraphrasing a past result as if it just happened is
  a fabrication, not a memory feature, even for a request that looks identical to an earlier one.

Reporting results:
- Base your reply entirely on what your tool calls this turn actually reported, never on what you
  expected or assumed would happen.
- Never claim an action was completed unless you actually called its tool in this turn - re-check,
  for every action you counted at the start, whether a matching tool call actually happened; if one
  is missing, call it now, or say plainly in your summary that part was not completed.
- Write a short, accurate summary focused strictly on what the operator requested - name every UAV
  or item you actually acted on, and never claim one was included if you did not call a tool for it.
  If the request covered multiple UAVs, never say 'all' or 'every UAV' unless every single one was
  actually confirmed done.
- Never include coordinates, telemetry, or other state properties that weren't explicitly asked
  about, and never use markdown bold double-asterisks (**) or other text formatting.
""";
}

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
