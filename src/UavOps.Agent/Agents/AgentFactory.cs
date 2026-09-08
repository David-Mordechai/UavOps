using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds BrainAgent — the single flat agent covering every real operation across every domain
/// directly, no agent-to-agent delegation anywhere: Moav (live UAV fleet), watchdog, and simulator
/// tools (VM readiness, lesson listing, and starting a lesson run) are all discovered from
/// connected MCP servers (<see cref="McpTools"/>, each domain its own separate process - see
/// "Split BrainAgent's 3 domains into separate MCP servers" in <c>CLAUDE.md</c>) - every domain
/// operation across all three domains lives entirely in its own MCP server process; nothing
/// domain-specific is built in-process anymore. Replaces what used to be a recursive multi-agent
/// tree (BrainAgent → MoavAgent/SimulatorAgent/MaintenanceAgent → per-domain leaf specialists)
/// after this session's investigation established that hop structure — not the model, not the .NET
/// Agent Framework — as the actual source of fabricated success claims; a standalone lab then
/// validated a flat design scales correctly to far more tools than this app has today, given real
/// semantic-embedding retrieval narrowing what's offered each turn (see
/// <see cref="ToolRetrievalIndex"/>).
///
/// Tools are rebuilt fresh every turn (not cached) because every tool instance closes over that
/// turn's correlationId, so logs/traces are attributable end-to-end — only the underlying
/// <see cref="IChatClient"/> is cached across turns. BrainAgent itself is a single, cached,
/// memory-carrying instance (see <see cref="GetOrCreatePersistentBrainAgentAsync"/>) together with
/// a single reused <see cref="AgentSession"/> — required by the Agent Framework's own session-reuse
/// contract (a session is tied to the agent instance/configuration that created it). Its *tools*
/// are still rebuilt fresh every turn via <see cref="BuildToolsForTurn"/> and supplied per call
/// through <c>ChatClientAgentRunOptions</c>, so correlationId-scoped tracing is unaffected.
/// </summary>
public sealed class AgentFactory(
    Func<string, string?, IChatClient> chatClientFactory,
    string defaultModel,
    AgentConfig config,
    RetrievalOptions retrievalOptions,
    MemoryOptions memoryOptions,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate)
{
    public const string RootAgentName = "BrainAgent";
    private const string StartupTemplateCorrelationId = "startup-template";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    /// <summary>Set once at startup, right after construction — see <c>Program.cs</c>. Not a
    /// constructor parameter: building the real index requires calling
    /// <see cref="BuildTemplateTools"/> on this very instance first, which would otherwise be a
    /// circular construction-order dependency.</summary>
    public ToolRetrievalIndex RetrievalIndex { get; set; } = null!;

    /// <summary>Every tool discovered once at startup across all connected MCP servers (see
    /// <c>Agents/BrainAgent.yaml</c>'s <c>mcpServers:</c> section - today the Moav domain's
    /// <c>UavOps.Agent.McpMoav</c>, the watchdog domain's <c>UavOps.Agent.McpWatchdog</c>, and the
    /// simulator-infra domain's <c>UavOps.Agent.McpSimulator</c>) — set post-construction, same
    /// pattern and reasoning
    /// as <see cref="RetrievalIndex"/> (listing a server's tools is itself an async stdio round
    /// trip). Merged into one flat list rather than kept per-server: which safety wrapping a tool
    /// needs (<see cref="BuildAllTools"/>'s tail-number/location checks) is decided from the
    /// tool's own JSON schema, not from which server it came from, so nothing downstream needs to
    /// know the split. Each raw <c>McpClientTool</c> is stateless and safely reusable across every
    /// turn - only the <see cref="Tooling.McpBackedTool"/>/disambiguation wrapping built around it
    /// in <see cref="BuildAllTools"/> needs to be fresh per turn, for correlationId-scoped tracing;
    /// the underlying tool list itself is never re-fetched per turn, so adding another MCP server
    /// never adds a second network/IPC round trip to a turn's latency.</summary>
    public IReadOnlyList<AIFunction> McpTools { get; set; } = [];

    /// <summary>The same tools as <see cref="McpTools"/>, kept grouped by which server they came
    /// from - set post-construction alongside <see cref="McpTools"/> (see <c>Program.cs</c>'s MCP
    /// connection loop, which already has each server's own name and tool list in scope before
    /// flattening into <see cref="McpTools"/>). Exists solely for <c>AgentGraphProjector</c> to
    /// render an honest per-server graph; tool-building/retrieval never needs the split, since
    /// which safety wrapping a tool needs is decided from its own JSON schema, not which server it
    /// came from.</summary>
    public IReadOnlyList<(string ServerName, IReadOnlyList<AIFunction> Tools)> McpServerToolGroups { get; set; } = [];

    // Guards first-time creation of the persistent BrainAgent + its session (see
    // GetOrCreatePersistentBrainAgentAsync) against concurrent turns racing to create it — SignalR
    // allows overlapping SendMessage calls (MaximumParallelInvocationsPerClient = 10).
    private readonly SemaphoreSlim _brainAgentLock = new(1, 1);
    private AIAgent? _brainAgent;
    private AgentSession? _brainAgentSession;
    private InMemoryChatHistoryProvider? _brainAgentHistoryProvider;

    /// <summary>Returns the single, long-lived BrainAgent instance, its reused conversation
    /// session, and the history provider backing that session (so a caller can snapshot/restore
    /// its message list — see <see cref="MainAgentOrchestrator"/>'s verified-retry logic), creating
    /// all three on first use. The instance carries no tools of its own — this turn's tools are
    /// built separately via <see cref="BuildToolsForTurn"/> and supplied by the caller through
    /// per-call run options, so nothing here needs to change per turn.</summary>
    public async Task<(AIAgent Agent, AgentSession Session, InMemoryChatHistoryProvider HistoryProvider)> GetOrCreatePersistentBrainAgentAsync(CancellationToken cancellationToken)
    {
        if (_brainAgent is not null && _brainAgentSession is not null && _brainAgentHistoryProvider is not null)
        {
            return (_brainAgent, _brainAgentSession, _brainAgentHistoryProvider);
        }

        await _brainAgentLock.WaitAsync(cancellationToken);
        try
        {
            if (_brainAgent is null || _brainAgentSession is null || _brainAgentHistoryProvider is null)
            {
#pragma warning disable MEAI001 // IChatReducer/InMemoryChatHistoryProviderOptions.ChatReducer are experimental Microsoft.Extensions.AI APIs — acceptable here.
                var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    // NOT Microsoft.Extensions.AI's own MessageCountingChatReducer — see
                    // ToolCallAwareChatReducer's own doc comment for the live-reproduced,
                    // source-verified fabrication bug that type causes once this session runs long
                    // enough to trigger even one reduction pass.
                    ChatReducer = new ToolCallAwareChatReducer(memoryOptions.MaxHistoryMessages)
                });
#pragma warning restore MEAI001
                var agent = BuildAgent(tools: [], historyProvider);
                _brainAgent = agent;
                _brainAgentSession = await agent.CreateSessionAsync(cancellationToken);
                _brainAgentHistoryProvider = historyProvider;
            }

            return (_brainAgent, _brainAgentSession, _brainAgentHistoryProvider);
        }
        finally
        {
            _brainAgentLock.Release();
        }
    }

    /// <summary>Builds a template tool list once at startup — real per-tool wrapping (so names/
    /// descriptions match exactly what a real turn would build) under a placeholder correlationId
    /// that's never actually invoked, purely to give <see cref="ToolRetrievalIndex.BuildAsync"/>
    /// stable (Name, Description) pairs to embed.</summary>
    public List<AITool> BuildTemplateTools() => BuildAllTools(StartupTemplateCorrelationId, operatorText: "");

    /// <summary>Builds this turn's real, correlationId-scoped tool list, then narrows it to the
    /// top-K most relevant via <see cref="ToolRetrievalIndex.RankCandidates"/> (ranked against the
    /// operator's own turn text). No "safe no-op" tool appended - <c>tool_choice</c> is never
    /// forced (see <see cref="BuildAgent"/>), so the model can always answer in plain text when no
    /// real operation applies; nothing needs to be picked among just to satisfy a forced choice.</summary>
    public async Task<List<AITool>> BuildToolsForTurn(string correlationId, string operatorText, CancellationToken cancellationToken)
    {
        var allTools = BuildAllTools(correlationId, operatorText);
        var nameToTool = allTools.ToDictionary(t => ((AIFunction)t).Name, StringComparer.Ordinal);

        var query = await RetrievalIndex.EmbedQueryAsync(operatorText, cancellationToken);
        var candidateNames = RetrievalIndex.RankCandidates(query, retrievalOptions.TopK);

        return candidateNames.Select(n => nameToTool[n]).ToList();
    }

    /// <summary>Builds one agent standalone, with no tools at all — used for a background job's
    /// proactive summary (see <c>SimulatorLessonJobProcessor</c>), where the same persona/
    /// instructions/model/temperature as the real agent gives a consistent voice, but attaching no
    /// tools means it structurally cannot re-trigger anything even if it misreads the prompt.</summary>
    public AIAgent BuildPersonaOnlyAgent() => BuildAgent(tools: []);

    /// <summary>Builds every real operation - every domain's MCP-discovered tools plus the one
    /// remaining in-process operator-prompt tool - as a correlationId-scoped tool, with the same
    /// per-tool safety wrapping this app has always applied (location/tail-number grounding,
    /// confirmation-gating) — no agent-to-agent delegation, no recursion: this is the entire
    /// tool-building surface now.</summary>
    private List<AITool> BuildAllTools(string correlationId, string operatorText)
    {
        var tools = new List<AITool>();
        var tailNumberScope = new TailNumberResolutionScope();

        foreach (var mcpTool in McpTools)
        {
            AITool wrappedTool = new McpBackedTool(mcpTool, RequiresConfirmation(mcpTool), toolLogger, confirmationGate, RootAgentName, correlationId);

            // Canonicalize "location" (Navigate, PointPayload) before the tailNumber guard, if
            // any, so every re-invocation it does (e.g. the multi-UAV fan-out) also gets the
            // canonical value - see LocationCanonicalizationTool's own doc comment.
            if (SchemaHasProperty(mcpTool, "location"))
            {
                wrappedTool = new LocationCanonicalizationTool((AIFunction)wrappedTool);
            }

            // Only Moav operations (this server) ever take a "tailNumber" the model could guess
            // instead of asking - see TailNumberDisambiguationTool's own doc comment.
            if (SchemaHasProperty(mcpTool, "tailNumber"))
            {
                wrappedTool = new TailNumberDisambiguationTool((AIFunction)wrappedTool, ListRealMoavFleetAsync, operatorPromptGate, tailNumberScope, RootAgentName, correlationId, operatorText);
            }

            tools.Add(wrappedTool);
        }

        return tools;
    }

    /// <summary>Whether a tool call needs operator approval before it runs - read directly off the
    /// MCP server's own protocol-native annotations (<c>Tool.Annotations.ReadOnlyHint</c>/
    /// <c>DestructiveHint</c>, set server-side via <c>[McpServerTool(ReadOnly = ...,
    /// Destructive = ...)]</c>), never a host-side name list. <c>ReadOnlyHint</c> and
    /// <c>DestructiveHint</c> are independent flags in the MCP spec - setting one does NOT imply
    /// the other - so a tool needs confirmation only if it's both NOT marked read-only AND either
    /// explicitly or (by the spec's own "unspecified means true") implicitly destructive. Every
    /// tool across all 3 servers has been given an explicit annotation (see each project's own tool
    /// file) specifically so the "unspecified" conservative default doesn't silently make
    /// everything require confirmation; only the one tool nobody explicitly marked safe keeps it.
    /// Live-verified via the real Agent Graph UI: without the ReadOnlyHint check, every read-only
    /// query tool (ListFleet, GetTelemetry, GetLinkStatus, GetMissionStatus, GetServicesHealth,
    /// ListConfigurations, ListConfiguredServices, ListSimulatorLessons) silently required
    /// confirmation - never asserted as a wrong RESULT by the live test suite (whose own ground-
    /// truth checks bypass McpBackedTool entirely), but real, and very likely the true cause of an
    /// earlier unexplained live-suite slowdown (every affected call silently eating a confirmation
    /// timeout) that had been chased as GPU/network load instead. A non-MCP <see cref="AIFunction"/>
    /// (none exist today, but the cast guards the case) is treated the same conservative way.
    /// Public so <see cref="Options.AgentGraphProjector"/> can show the same real answer in its
    /// tooltips instead of keeping its own separate copy of this logic - the bug above happened
    /// specifically because two copies existed and only one got fixed.</summary>
    public static bool RequiresConfirmation(AIFunction tool)
    {
        var annotations = (tool as McpClientTool)?.ProtocolTool.Annotations;
        if (annotations?.ReadOnlyHint == true)
        {
            return false;
        }

        return annotations?.DestructiveHint ?? true;
    }

    /// <summary>Whether an MCP tool's own JSON schema declares a property with this name - used to
    /// decide which safety wrapping (location canonicalization, tail-number disambiguation) a
    /// Moav tool needs.</summary>
    private static bool SchemaHasProperty(AIFunction tool, string propertyName) =>
        tool.JsonSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(propertyName, out _);

    /// <summary>Looks up the real fleet by calling the Moav MCP server's own fleet-listing tool
    /// directly - the same internal, untraced lookup <c>TailNumberDisambiguationTool</c> always
    /// did against the in-process fleet service before the MCP split, just reached over MCP now.
    /// Deliberately bypasses <see cref="McpBackedTool"/>/<see cref="ToolInvocationLogger"/> (this
    /// is BrainAgent's own internal disambiguation bookkeeping, not a model-initiated tool call to
    /// trace) - exactly as before. Which tool name to call is read from
    /// <see cref="McpServerConfig.FleetListingTool"/> (set on the <c>moav</c> server's own entry in
    /// <c>BrainAgent.yaml</c>) rather than a hardcoded string - the Moav domain's own tool naming
    /// is that domain's business, not something the host should silently assume.</summary>
    private async Task<OperationResult> ListRealMoavFleetAsync(CancellationToken cancellationToken)
    {
        var fleetListingToolName = config.McpServers.Select(s => s.FleetListingTool).FirstOrDefault(name => name is not null);
        if (fleetListingToolName is null)
        {
            return OperationResult.Fail(OperationError.NoClientConnected, "No MCP server declares a 'fleetListingTool' in BrainAgent.yaml.");
        }

        var listFleetTool = McpTools.FirstOrDefault(t => t.Name == fleetListingToolName);
        if (listFleetTool is null)
        {
            return OperationResult.Fail(OperationError.NoClientConnected, $"Configured fleetListingTool '{fleetListingToolName}' is not a real connected MCP tool.");
        }

        var raw = await listFleetTool.InvokeAsync(new AIFunctionArguments(), cancellationToken);
        var json = raw?.ToString();
        if (string.IsNullOrWhiteSpace(json) || json.StartsWith("Error:", StringComparison.Ordinal))
        {
            return OperationResult.Fail(OperationError.ClientReportedError, json);
        }

        var tails = JsonSerializer.Deserialize<List<UavSummary>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return tails is null ? OperationResult.Fail(OperationError.ClientReportedError, "Could not parse the fleet list.") : OperationResult.Ok(tails);
    }

    private AIAgent BuildAgent(List<AITool> tools, ChatHistoryProvider? chatHistoryProvider = null)
    {
        var chatClient = GetChatClient();

        // tool_choice deliberately left at its default ("auto") - never forced. See
        // MainAgentOrchestrator's own doc comment for the evidence behind this: forcing was carried
        // over defensively from the old multi-agent architecture without re-testing whether it was
        // still needed here, and it was not - it actively broke a real scenario (a bare greeting
        // deterministically failing to produce any tool call when forced) that auto-mode never did.

        var options = new ChatClientAgentOptions
        {
            Name = RootAgentName,
            UseProvidedChatClientAsIs = true, // our client is already wrapped with concurrent function invocation below — don't let ChatClientAgent re-wrap it
            // Only the persistent root agent (see GetOrCreatePersistentBrainAgentAsync) gets one of
            // these — the template-only build (BuildTemplateTools' underlying construction never
            // reaches here) and BuildPersonaOnlyAgent leave this null.
            ChatHistoryProvider = chatHistoryProvider,
            ChatOptions = new ChatOptions
            {
                Instructions = config.Instructions,
                Tools = tools,
                Temperature = config.Temperature,
                // Without this, a small model deciding it needs two tools (e.g. SetSpeed + SetAltitude)
                // may spread them across separate turns instead of one batched turn, which defeats
                // AllowConcurrentInvocation below (only tool calls within the *same* turn run concurrently).
                AllowMultipleToolCalls = true
            }
        };

        return new ChatClientAgent(chatClient, options);
    }

    private IChatClient GetChatClient()
    {
        var model = config.Model ?? defaultModel;
        // Provider has to be part of the cache key, not just the model name — an Ollama model and
        // an OpenRouter model could otherwise collide on an identical name string.
        var cacheKey = $"{config.Provider ?? "Ollama"}::{model}";
        return _chatClients.GetOrAdd(cacheKey, _ => chatClientFactory(model, config.Provider));
    }
}
