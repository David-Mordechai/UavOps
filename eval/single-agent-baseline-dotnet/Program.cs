// Minimal single-agent baseline test, deliberately independent of UavOps.Agent's own multi-agent
// architecture (no BrainAgent/MoavAgent/CreatePlan/leaf-agent split, no delegation, no pronoun
// hand-off between agents, no shared TailNumberResolutionScope) - the .NET-side counterpart to
// eval/single-agent-baseline/single_agent_test.py.
//
// Built to answer a narrower question than that Python script already answered: given the exact
// same flat, single-agent design (one agent, all 6 tools, one system prompt, tool_choice "auto",
// same 3-turn conversation), does going through UavOps.Agent's actual .NET stack - OpenAI ChatClient
// -> AsIChatClient() -> FunctionInvokingChatClient{AllowConcurrentInvocation=true} ->
// ChatClientAgent{ChatOptions{Instructions,Tools,Temperature,AllowMultipleToolCalls=true}}, the
// identical construction path AgentFactory.BuildAgent/Program.cs use for every real agent - reproduce
// the Python script's reliability, or introduce its own failures? If this also scores well, that
// rules out Microsoft.Agents.AI/Microsoft.Extensions.AI as the cause and further isolates the real
// app's multi-hop delegation design as the sole remaining suspect. If this does worse than the
// Python baseline against the same model/backend, that implicates the .NET framework layer itself
// (schema generation, message serialization, tool-call-loop behavior) - something the pure-HTTP
// Python script can't see at all.
//
// Deliberately NOT using RequireToolOnFirstTurnChatClient or any other UavOps.Agent-specific
// wrapper - those are structural choices for the *multi-agent* app, not part of what a flat
// single-agent design would ever need, and forcing tool_choice here would no longer be an
// apples-to-apples comparison with the Python baseline's tool_choice="auto".
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
    : 1;

Console.WriteLine($"Model: {model}   Host: {host}   Temperature: {temperature}");

var systemPrompt = GetSystemPrompt();
var results = new List<bool>();
for (var i = 0; i < n; i++)
{
    Console.WriteLine($"\n\n########## RUN {i} ##########");

    var fleet = Fleet.CreateDefault();
    var tools = new FleetTools(fleet);

    var openAiClient = new ChatClient(model, new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri($"{host}/v1") });
    IChatClient inner = openAiClient.AsIChatClient();
    IChatClient chatClient = new FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true };

    var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
    {
        Name = "SingleFlatAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = systemPrompt,
            Tools = tools.AsTools(),
            Temperature = temperature,
            AllowMultipleToolCalls = true,
        },
    });
    var session = await agent.CreateSessionAsync();

    await RunTurn(agent, session, "hi my name is David and I am today Operator");
    await RunTurn(agent, session, "What uavs do we have?");
    await RunTurn(agent, session,
        "fly all of them to target alpha and set speed to 250 and altitude to 3000 to all of them also point there payloads there");

    var ok = fleet.Values.All(s =>
        s.SpeedKts == 250 && s.AltitudeFt == 3000 && s.Mode == "Transiting" && s.PayloadLockedOn == "alpha");

    Console.WriteLine($"\n{new string('=', 70)}\nFINAL FAKE FLEET STATE (ground truth) - run {i}\n{new string('=', 70)}");
    foreach (var (tail, state) in fleet)
    {
        Console.WriteLine($"  {tail}: {state}");
    }
    Console.WriteLine($"\nRUN {i}: ALL 3 UAVs CORRECTLY UPDATED: {ok}");
    results.Add(ok);
}

Console.WriteLine($"\n\n{new string('=', 70)}\nSUMMARY ({model}): {results.Count(r => r)}/{n} runs fully correct\n{new string('=', 70)}");

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

// Identical wording to single_agent_test.py's SYSTEM_PROMPT, so any behavioral difference is
// attributable to the .NET framework layer, not a reworded prompt.
static string GetSystemPrompt() => """
You are a single UAV fleet operations assistant. You handle everything directly - introducing yourself, answering questions about the fleet, and commanding UAVs - using the tools provided. There is no other agent or system; you must call the real tools yourself to get real information or take real action. Never claim a fact about the fleet or an action was taken unless you actually called the matching tool this turn and are reporting its real result.

Tail numbers:
- Never guess a tail number. If you don't already know the fleet's real tail numbers this turn, call ListFleet first.
- When the operator refers to the whole fleet collectively ('all of them', 'every UAV', 'all three', etc.), call the relevant tool once per real UAV tail number (from ListFleet) - never invent a placeholder like 'ALL', and never skip any UAV.

Multi-part requests:
- Count how many distinct actions the operator asked for, across however many UAVs are involved, and call every matching tool for every UAV - e.g. 'fly all three to alpha, set speed 250 and altitude 3000, and point their payloads there' with 3 known UAVs is 12 tool calls (Navigate + SetSpeed + SetAltitude + PointPayload, once each, for each of the 3 UAVs).
- You may call multiple tools in the same turn.

Summaries:
- After acting, write a short, accurate summary based only on the real tool results you just received - name every UAV you actually acted on, and never claim one was included if you did not call a tool for it.
""";

sealed class UavState
{
    // Auto-properties, not fields - System.Text.Json (used by FunctionInvokingChatClient to
    // serialize tool results back to the model) only serializes public properties by default, not
    // public fields. Public fields here would silently serialize as "{}", starving the model of
    // real telemetry - exactly the kind of .NET-framework-specific footgun this baseline exists to
    // catch, since the pure-HTTP Python script has no equivalent failure mode.
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
    // Same starting positions/speed/altitude/mode as single_agent_test.py's FLEET constant.
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
