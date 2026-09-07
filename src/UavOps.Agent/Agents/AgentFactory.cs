using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Agents.MoavAgent;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds BrainAgent — the single flat agent covering every real operation across all 4 catalogs
/// (fleet, simulator, watchdog, watchdog-config) directly, no agent-to-agent delegation anywhere.
/// Replaces what used to be a recursive multi-agent tree (BrainAgent → MoavAgent/SimulatorAgent/
/// MaintenanceAgent → per-domain leaf specialists) after this session's investigation established
/// that hop structure — not the model, not the .NET Agent Framework — as the actual source of
/// fabricated success claims; a standalone lab then validated a flat design scales correctly to far
/// more tools than this app has today, given real semantic-embedding retrieval narrowing what's
/// offered each turn (see <see cref="ToolRetrievalIndex"/>).
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
    OperationCatalog catalog,
    IOperationService operationService,
    OperationCatalog simulatorCatalog,
    ISimulatorService simulatorService,
    OperationCatalog watchdogCatalog,
    IWatchdogService watchdogService,
    OperationCatalog watchdogConfigCatalog,
    IWatchdogConfigService watchdogConfigService,
    RetrievalOptions retrievalOptions,
    MemoryOptions memoryOptions,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate)
{
    public const string RootAgentName = "BrainAgent";
    private const string OperatorPromptKind = "OperatorPrompt";
    private const string StartupTemplateCorrelationId = "startup-template";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    /// <summary>Set once at startup, right after construction — see <c>Program.cs</c>. Not a
    /// constructor parameter: building the real index requires calling
    /// <see cref="BuildTemplateTools"/> on this very instance first, which would otherwise be a
    /// circular construction-order dependency.</summary>
    public ToolRetrievalIndex RetrievalIndex { get; set; } = null!;

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

    /// <summary>Builds every real operation across all 4 catalogs as a correlationId-scoped tool,
    /// with the same per-tool safety wrapping this app has always applied (location/tail-number
    /// grounding, confirmation-gating) — no agent-to-agent delegation, no recursion: this is the
    /// entire tool-building surface now.</summary>
    private List<AITool> BuildAllTools(string correlationId, string operatorText)
    {
        var tools = new List<AITool>();
        var tailNumberScope = new TailNumberResolutionScope();

        foreach (var toolConfig in config.Tools)
        {
            if (toolConfig.Kind == OperatorPromptKind)
            {
                tools.Add(new AskOperatorChoiceTool(toolConfig, operatorPromptGate, toolLogger, RootAgentName, correlationId, operatorText));
                continue;
            }

            if (catalog.TryResolve(toolConfig.Operation, out var descriptor) && descriptor is not null)
            {
                AITool operationTool = new OperationTool(descriptor, toolConfig, operationService, toolLogger, confirmationGate, RootAgentName, correlationId);

                // Canonicalize "location" (Navigate, PointPayload) before the tailNumber guard,
                // if any, so every re-invocation it does (e.g. the multi-UAV fan-out) also gets the
                // canonical value - see LocationCanonicalizationTool's own doc comment.
                if (descriptor.Parameters.Any(p => p.Name == "location") && !toolConfig.FixedParameters.ContainsKey("location"))
                {
                    operationTool = new LocationCanonicalizationTool((AIFunction)operationTool);
                }

                // Only fleet operations (this catalog) ever take a "tailNumber" the model could
                // guess instead of asking - see TailNumberDisambiguationTool's own doc comment.
                if (descriptor.Parameters.Any(p => p.Name == "tailNumber") && !toolConfig.FixedParameters.ContainsKey("tailNumber"))
                {
                    operationTool = new TailNumberDisambiguationTool((AIFunction)operationTool, operationService, operatorPromptGate, tailNumberScope, RootAgentName, correlationId, operatorText);
                }

                tools.Add(operationTool);
            }
            else if (simulatorCatalog.TryResolve(toolConfig.Operation, out var simDescriptor) && simDescriptor is not null)
            {
                tools.Add(new OperationTool(simDescriptor, toolConfig, simulatorService, toolLogger, confirmationGate, RootAgentName, correlationId));
            }
            else if (watchdogCatalog.TryResolve(toolConfig.Operation, out var watchdogDescriptor) && watchdogDescriptor is not null)
            {
                tools.Add(new OperationTool(watchdogDescriptor, toolConfig, watchdogService, toolLogger, confirmationGate, RootAgentName, correlationId));
            }
            else if (watchdogConfigCatalog.TryResolve(toolConfig.Operation, out var watchdogConfigDescriptor) && watchdogConfigDescriptor is not null)
            {
                tools.Add(new OperationTool(watchdogConfigDescriptor, toolConfig, watchdogConfigService, toolLogger, confirmationGate, RootAgentName, correlationId));
            }
            else
            {
                // Should never happen — AgentConfigValidator checks this at startup.
                throw new InvalidOperationException($"BrainAgent references unknown operation '{toolConfig.Operation}'.");
            }
        }

        return tools;
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
