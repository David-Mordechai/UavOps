// Tool-retrieval-at-scale lab. See the approved plan for the 5-step scale-up this drives:
//   Step 1: --total-tools 30                  (baseline, no retrieval, small noisy tool list)
//   Step 2: --total-tools 30  --retrieval      (turn retrieval on at the same small scale)
//   Step 3: --total-tools 100 --retrieval
//   Step 4: --total-tools 500 --retrieval
//   Step 5: --total-tools 1000 --retrieval
// Zero dependency on UavOps.Agent - standalone, same construction path/package versions as
// eval/single-agent-baseline-dotnet (already proven 8/8 this session).
using System.ClientModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var model = GetOption(args, "--model") ?? "nvidia/Qwen3.6-35B-A3B-NVFP4";
var host = GetOption(args, "--host") ?? "http://10.10.77.57:8000";
var embedHost = GetOption(args, "--embed-host") ?? "http://10.10.77.57:8001";
var embedModel = GetOption(args, "--embed-model") ?? "Qwen/Qwen3-Embedding-8B";
var temperature = float.TryParse(GetOption(args, "--temperature"), out var t) ? t : 0f;
var totalTools = int.TryParse(GetOption(args, "--total-tools"), out var tt) ? tt : 30;
var topK = int.TryParse(GetOption(args, "--top-k"), out var tk) ? tk : 10;
var repeats = int.TryParse(GetOption(args, "--repeats"), out var rp) ? rp : 8;
var seed = int.TryParse(GetOption(args, "--seed"), out var sd) ? sd : 42;
var retrievalEnabled = args.Contains("--retrieval");
var onlyScenario = GetOption(args, "--scenario");
var maxScoreGapFromBest = float.TryParse(GetOption(args, "--max-gap"), out var mg) ? mg : (float?)null;
var disabledNames = (GetOption(args, "--disable") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

var systemPrompt = GetSystemPrompt();
const int RealToolCount = 6 + 4 + 3; // fleet + watchdog + simulator-infra
var distractorCount = Math.Max(0, totalTools - RealToolCount);
var distractors = SyntheticCatalog.Generate(distractorCount, seed);

Console.WriteLine($"Model: {model}   Host: {host}");
Console.WriteLine($"Total tools: {RealToolCount + distractors.Count} ({RealToolCount} real (fleet+watchdog+simulator) + {distractors.Count} distractors)   TopK: {topK}   Retrieval: {retrievalEnabled}   Repeats: {repeats}");

ToolRetrievalIndex? retrievalIndex = null;
if (retrievalEnabled)
{
    Console.WriteLine($"Building retrieval index via embeddings ({embedModel} @ {embedHost}) ...");
    var embeddingClient = new EmbeddingClient(embedModel, new ApiKeyCredential("not-needed"),
        new OpenAIClientOptions { Endpoint = new Uri($"{embedHost}/v1") });
    var embeddingGenerator = embeddingClient.AsIEmbeddingGenerator();
    var templateTools = new FleetTools(Fleet.CreateDefault()).AsTools()
        .Concat(new WatchdogTools(new WatchdogState()).AsTools())
        .Concat(new SimulatorInfraTools(new SimulatorState()).AsTools())
        .Concat(distractors)
        .ToList();
    retrievalIndex = await ToolRetrievalIndex.BuildAsync(templateTools, embeddingGenerator);
    Console.WriteLine($"Index built: {retrievalIndex.Count} tools embedded.");
}

var diagnoseQuery = GetOption(args, "--diagnose");
if (diagnoseQuery is not null && retrievalIndex is not null)
{
    var query = await retrievalIndex.EmbedQueryAsync(diagnoseQuery);
    var ranked = retrievalIndex.RankAll(query);
    Console.WriteLine($"\nFull ranking for query: \"{diagnoseQuery}\"");
    for (var i = 0; i < ranked.Count; i++)
    {
        Console.WriteLine($"  {i + 1,4}. {ranked[i].Name,-30} {ranked[i].Score:F4}");
    }
    return;
}

var scenarios = Scenarios.All().Where(s => onlyScenario is null || s.Name == onlyScenario).ToList();
var scenarioPassCounts = new Dictionary<string, int>();
var recallHits = 0;
var recallTotal = 0;

// Score-distribution measurement for RetrievalOptions.MinConfidenceForAutoExecute (see
// UavOps.Agent's Tooling/RetrievalConfidenceGuardTool.cs) - a true positive is a required tool's
// own score for the turn that required it; a false positive is any other candidate's score for
// that same turn (an offered-but-not-actually-needed tool, exactly the "backfill" shape that
// prompted this measurement). Scores are deterministic per turn text (embeddings aren't sampled),
// so this doesn't need --repeats > 1 to be meaningful - it's collected once per turn regardless of
// how many repeats run, just accumulated across every repeat/scenario that does run.
var truePositiveScores = new List<float>();
var falsePositiveScores = new List<float>();

foreach (var scenario in scenarios)
{
    Console.WriteLine($"\n\n########## SCENARIO: {scenario.Name} ##########");
    var passes = 0;

    for (var r = 0; r < repeats; r++)
    {
        Console.WriteLine($"\n---- repeat {r} ----");
        var fleet = Fleet.CreateDefault();
        var fleetTools = new FleetTools(fleet);
        var watchdogState = new WatchdogState();
        var watchdogTools = new WatchdogTools(watchdogState);
        var simulatorState = new SimulatorState();
        var simulatorTools = new SimulatorInfraTools(simulatorState);
        var nameToTool = fleetTools.AsTools()
            .Concat(watchdogTools.AsTools())
            .Concat(simulatorTools.AsTools())
            .Concat(distractors)
            .ToDictionary(tool => tool.Name);

        var openAiClient = new ChatClient(model, new ApiKeyCredential("not-needed"),
            new OpenAIClientOptions { Endpoint = new Uri($"{host}/v1") });
        IChatClient inner = openAiClient.AsIChatClient();
        IChatClient chatClient = new FunctionInvokingChatClient(inner) { AllowConcurrentInvocation = true };

        var agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "ToolRetrievalLabAgent",
            ChatOptions = new ChatOptions { Instructions = systemPrompt, Tools = [], Temperature = temperature, AllowMultipleToolCalls = true },
        });
        var session = await agent.CreateSessionAsync();

        foreach (var turn in scenario.Turns)
        {
            List<AITool> candidateTools;
            List<string> candidateNames;

            if (retrievalEnabled && retrievalIndex is not null)
            {
                var enabledNames = disabledNames.Count > 0 ? nameToTool.Keys.Where(n => !disabledNames.Contains(n)).ToHashSet() : null;
                var query = await retrievalIndex.EmbedQueryAsync(turn.Text);
                var (_, ranked) = retrievalIndex.RankCandidates(query, topK, enabledNames, maxScoreGapFromBest);
                candidateNames = ranked.Select(x => x.Name).ToList();
                candidateTools = candidateNames.Select(n => nameToTool[n]).ToList();

                foreach (var (name, score) in ranked)
                {
                    if (turn.RequiredTools.Contains(name)) truePositiveScores.Add(score);
                    else falsePositiveScores.Add(score);
                }
            }
            else
            {
                candidateNames = nameToTool.Keys.ToList();
                candidateTools = nameToTool.Values.ToList();
            }

            if (turn.RequiredTools.Length > 0)
            {
                var missing = turn.RequiredTools.Except(candidateNames).ToList();
                recallTotal++;
                if (missing.Count == 0) recallHits++;
                Console.WriteLine($"\n[retrieval] required={string.Join(",", turn.RequiredTools)} candidates={candidateTools.Count} missing={(missing.Count == 0 ? "none" : string.Join(",", missing))}");
            }

            Console.WriteLine($"\n{new string('=', 70)}\nOperator: {turn.Text}\n{new string('=', 70)}");
            var runOptions = new ChatClientAgentRunOptions(new ChatOptions
            {
                Instructions = systemPrompt, Tools = candidateTools, Temperature = temperature, AllowMultipleToolCalls = true,
            });
            var response = await agent.RunAsync(turn.Text, session, runOptions);
            Console.WriteLine($"\nAssistant (final): {response.Text}");
        }

        var labState = new LabState(fleet, watchdogState, simulatorState);
        var ok = scenario.Check(labState);
        Console.WriteLine($"\nFINAL STATE: {string.Join("  ", fleet.Select(kv => $"{kv.Key}: {kv.Value}"))}  Watchdog: {watchdogState}  Simulator: {simulatorState}");
        Console.WriteLine($"REPEAT {r}: PASS = {ok}");
        if (ok) passes++;
    }

    scenarioPassCounts[scenario.Name] = passes;
    Console.WriteLine($"\nSCENARIO '{scenario.Name}': {passes}/{repeats} passed");
}

Console.WriteLine($"\n\n{new string('=', 70)}\nSUMMARY ({model}, {RealToolCount + distractors.Count} tools, retrieval={retrievalEnabled}, topK={topK})\n{new string('=', 70)}");
foreach (var (name, passes) in scenarioPassCounts)
{
    Console.WriteLine($"  {name}: {passes}/{repeats}");
}
if (recallTotal > 0)
{
    Console.WriteLine($"  RECALL (required tools present in candidate set): {recallHits}/{recallTotal}");
}
if (truePositiveScores.Count > 0 || falsePositiveScores.Count > 0)
{
    Console.WriteLine($"\nSCORE DISTRIBUTION (for RetrievalOptions.MinConfidenceForAutoExecute):");
    if (truePositiveScores.Count > 0)
    {
        Console.WriteLine($"  True positives  (required tool's own score, n={truePositiveScores.Count}): " +
            $"min={truePositiveScores.Min():F4} p5={Percentile(truePositiveScores, 5):F4} mean={truePositiveScores.Average():F4} max={truePositiveScores.Max():F4}");
    }
    if (falsePositiveScores.Count > 0)
    {
        Console.WriteLine($"  Other candidates (not required, n={falsePositiveScores.Count}): " +
            $"min={falsePositiveScores.Min():F4} mean={falsePositiveScores.Average():F4} p95={Percentile(falsePositiveScores, 95):F4} max={falsePositiveScores.Max():F4}");
    }
}

static float Percentile(List<float> values, double percentile)
{
    var sorted = values.OrderBy(v => v).ToList();
    var index = (int)Math.Clamp(Math.Round(percentile / 100.0 * (sorted.Count - 1)), 0, sorted.Count - 1);
    return sorted[index];
}

static string? GetOption(string[] args, string name)
{
    var idx = Array.IndexOf(args, name);
    return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
}

static string GetSystemPrompt() => """
You are a single operations assistant covering three real areas - the UAV fleet, the watchdog service that supervises other services, and the training simulator's infrastructure. You handle everything directly - introducing yourself, answering questions, and taking action - using the tools provided. There is no other agent or system; you must call the real tools yourself to get real information or take real action. Never claim a fact or an action was taken unless you actually called the matching tool this turn and are reporting its real result.

Tail numbers:
- Never guess a tail number. If you don't already know the fleet's real tail numbers this turn, call ListFleet first.
- When the operator refers to the whole fleet collectively ('all of them', 'every UAV', 'all three', 'our drones', etc.), call the relevant tool once per real UAV tail number (from ListFleet) - never invent a placeholder like 'ALL', and never skip any UAV.

Simulator startup:
- "Start the simulator" requires two separate prerequisite calls, both needed, in order: first EnsureVmwareHostRunning (starts the underlying VMware host if needed), then EnsureSimulatorVmRunning (starts the actual simulator VM, which needs the host already running) - never skip the host check just because the VM-start tool sounds like the closer match to what the operator said.

Multi-part requests:
- Count how many distinct actions the operator asked for, across however many UAVs are involved, and call every matching tool for every UAV.
- You may call multiple tools in the same turn.
- Some of the tools you are given may not be relevant to the current request - ignore any tool that doesn't match what the operator actually asked for. Pick the tool whose name and description best match what the operator asked, regardless of which area it belongs to.

Summaries:
- After acting, write a short, accurate summary based only on the real tool results you just received - name every UAV you actually acted on, and never claim one was included if you did not call a tool for it.
""";
