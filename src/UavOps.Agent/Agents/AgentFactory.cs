using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Agents.MoavAgent;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds the agent graph fresh for each chat turn. Rebuilding is necessary (not just
/// cheap-and-easy) because every tool instance closes over the turn's correlationId, so logs
/// and traces for that turn are attributable end-to-end. Chat clients (one per distinct
/// provider+model pair in use — see <see cref="AgentConfig.Provider"/>) are cached across turns.
///
/// The root <c>BrainAgent</c> exists in two forms. A cached, memory-carrying one (see
/// <see cref="GetOrCreatePersistentBrainAgentAsync"/>), together with a single reused
/// <see cref="AgentSession"/> — required by the Agent Framework's own session-reuse contract (a
/// session is tied to the agent instance/configuration that created it). Its *tools* are still
/// rebuilt fresh every turn exactly as before, via <see cref="BuildRootToolsForTurn"/>, and
/// supplied per call through <c>ChatClientAgentRunOptions</c> — so correlationId-scoped tracing is
/// unaffected. And a brand new, memory-less one built fresh per call (see
/// <see cref="BuildStatelessRootAgentForTurn"/>) — <see cref="MainAgentOrchestrator"/> always tries
/// this one *first* for every turn, and only falls back to the memory-carrying one when it produces
/// no tool call at all, precisely so a small local model's tendency to pattern-complete a fresh
/// command against its own prior "success" turns can never fabricate an unexecuted action — see
/// <see cref="MainAgentOrchestrator"/>'s own doc comment for the incident this fixes. Child agents
/// reached via delegation are unaffected either way: still built fresh per turn, still carry no
/// memory of their own, still receive only the synthesized delegation instruction.
///
/// Delegate selection for each agent is either explicit (<see cref="AgentConfig.Children"/>, when
/// present) or embedding retrieval (<see cref="AgentRetrievalIndex"/>, the fallback for any agent
/// that doesn't declare <c>children:</c>) — see <see cref="BuildAgentRecursive"/>.
/// </summary>
public sealed class AgentFactory(
    Func<string, string?, IChatClient> chatClientFactory,
    string defaultModel,
    Dictionary<string, AgentConfig> agents,
    OperationCatalog catalog,
    IOperationService operationService,
    OperationCatalog simulatorCatalog,
    ISimulatorService simulatorService,
    OperationCatalog watchdogCatalog,
    IWatchdogService watchdogService,
    OperationCatalog watchdogConfigCatalog,
    IWatchdogConfigService watchdogConfigService,
    AgentRetrievalIndex retrievalIndex,
    RetrievalOptions retrievalOptions,
    MemoryOptions memoryOptions,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate,
    ILogger<AgentFactory> logger)
{
    private const string RootAgentName = "BrainAgent";
    private const string MoavAgentName = "MoavAgent";
    private const string OperatorPromptKind = "OperatorPrompt";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    // Guards first-time creation of the persistent BrainAgent + its session (see
    // GetOrCreatePersistentBrainAgentAsync) against concurrent turns racing to create it — SignalR
    // allows overlapping SendMessage calls (MaximumParallelInvocationsPerClient = 10).
    private readonly SemaphoreSlim _brainAgentLock = new(1, 1);
    private AIAgent? _brainAgent;
    private AgentSession? _brainAgentSession;

    /// <summary>Returns the single, long-lived BrainAgent instance and its reused conversation
    /// session, creating both on first use. The instance carries no tools of its own — this
    /// turn's tools are built separately via <see cref="BuildRootToolsForTurn"/> and supplied by
    /// the caller through per-call run options, so nothing here needs to change per turn.</summary>
    public async Task<(AIAgent Agent, AgentSession Session)> GetOrCreatePersistentBrainAgentAsync(CancellationToken cancellationToken)
    {
        if (_brainAgent is not null && _brainAgentSession is not null)
        {
            return (_brainAgent, _brainAgentSession);
        }

        await _brainAgentLock.WaitAsync(cancellationToken);
        try
        {
            if (_brainAgent is null || _brainAgentSession is null)
            {
#pragma warning disable MEAI001 // MessageCountingChatReducer is an experimental Microsoft.Extensions.AI API — acceptable here, it's just a message-count bound with no external side effects.
                var historyProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new MessageCountingChatReducer(memoryOptions.MaxHistoryMessages)
                });
#pragma warning restore MEAI001
                var agent = BuildAgent(RootAgentName, agents[RootAgentName], tools: [], historyProvider);
                _brainAgent = agent;
                _brainAgentSession = await agent.CreateSessionAsync(cancellationToken);
            }

            return (_brainAgent, _brainAgentSession);
        }
        finally
        {
            _brainAgentLock.Release();
        }
    }

    /// <summary>Builds just this turn's tool list for the root agent (BrainAgent) — the same
    /// correlationId-scoped tree construction <see cref="BuildAgentRecursive"/> already does for
    /// every agent, without also wrapping the result in a new root <see cref="ChatClientAgent"/>
    /// (the persistent one from <see cref="GetOrCreatePersistentBrainAgentAsync"/> is reused
    /// instead).</summary>
    public async Task<List<AITool>> BuildRootToolsForTurn(string correlationId, string operatorText, CancellationToken cancellationToken)
    {
        var query = await retrievalIndex.EmbedQueryAsync(operatorText, cancellationToken);
        return BuildAgentTools(RootAgentName, agents[RootAgentName], correlationId, query, operatorText,
            visited: ImmutableHashSet<string>.Empty, depth: retrievalOptions.MaxDelegationDepth, new TailNumberResolutionScope());
    }

    /// <summary>Builds a brand new, memory-less root <see cref="AIAgent"/> for this one turn only -
    /// the same shape the root agent had before the persistent-session feature existed.
    /// <see cref="MainAgentOrchestrator"/> always tries this first, precisely because it carries no
    /// history to pattern-complete a fresh command against - see its own doc comment for the full
    /// reasoning.</summary>
    public async Task<AIAgent> BuildStatelessRootAgentForTurn(string correlationId, string operatorText, CancellationToken cancellationToken)
    {
        var query = await retrievalIndex.EmbedQueryAsync(operatorText, cancellationToken);
        return BuildAgentRecursive(RootAgentName, agents[RootAgentName], correlationId, query, operatorText,
            visited: ImmutableHashSet<string>.Empty, depth: retrievalOptions.MaxDelegationDepth, new TailNumberResolutionScope());
    }

    /// <summary>Builds one agent standalone, with no tools at all — used for a background job's
    /// proactive summary (see <c>SimulatorLessonJobProcessor</c>), where the same persona/
    /// instructions/model/temperature as the real agent gives a consistent voice, but attaching no
    /// tools means it structurally cannot re-trigger anything even if it misreads the prompt.</summary>
    public AIAgent BuildPersonaOnlyAgent(string agentName) => BuildAgent(agentName, agents[agentName], tools: []);

    private AIAgent BuildAgentRecursive(string name, AgentConfig config, string correlationId,
        Embedding<float> query, string operatorText, ImmutableHashSet<string> visited, int depth, TailNumberResolutionScope tailNumberScope)
    {
        var tools = BuildAgentTools(name, config, correlationId, query, operatorText, visited, depth, tailNumberScope);
        return BuildAgent(name, config, tools);
    }

    private List<AITool> BuildAgentTools(string name, AgentConfig config, string correlationId,
        Embedding<float> query, string operatorText, ImmutableHashSet<string> visited, int depth, TailNumberResolutionScope tailNumberScope)
    {
        visited = visited.Add(name);
        var tools = new List<AITool>();

        // One scope per operator turn (correlationId), shared across every specialist agent built
        // for that turn - not one per agent. A delegate call always targets exactly one UAV (or
        // now, all of them), so once one tailNumber-taking tool call this turn asks/confirms and
        // gets an answer, EVERY sibling call across the WHOLE turn - including a different
        // specialist entirely, e.g. PayloadControlAgent's PointPayload right after
        // FlightControlAgent's SetSpeed - reuses that same answer instead of asking again. Created
        // once in BuildRootToolsForTurn/BuildStatelessRootAgentForTurn and threaded down through
        // every recursive call, rather than fresh per agent here - see TailNumberResolutionScope's
        // own doc comment. Observed directly: creating a fresh scope per agent asked the operator
        // the same "apply to all 3 known UAVs?" confirmation twice in one turn, once per specialist.

        foreach (var toolConfig in config.Tools)
        {
            if (toolConfig.Kind == OperatorPromptKind)
            {
                tools.Add(new AskOperatorChoiceTool(toolConfig, operatorPromptGate, toolLogger, name, correlationId, operatorText));
                continue;
            }

            if (catalog.TryResolve(toolConfig.Operation, out var descriptor) && descriptor is not null)
            {
                AITool operationTool = new OperationTool(descriptor, toolConfig, operationService, toolLogger, confirmationGate, name, correlationId);

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
                    operationTool = new TailNumberDisambiguationTool((AIFunction)operationTool, operationService, operatorPromptGate, tailNumberScope, name, correlationId, operatorText);
                }

                tools.Add(operationTool);
            }
            else if (simulatorCatalog.TryResolve(toolConfig.Operation, out var simDescriptor) && simDescriptor is not null)
            {
                tools.Add(new OperationTool(simDescriptor, toolConfig, simulatorService, toolLogger, confirmationGate, name, correlationId));
            }
            else if (watchdogCatalog.TryResolve(toolConfig.Operation, out var watchdogDescriptor) && watchdogDescriptor is not null)
            {
                tools.Add(new OperationTool(watchdogDescriptor, toolConfig, watchdogService, toolLogger, confirmationGate, name, correlationId));
            }
            else if (watchdogConfigCatalog.TryResolve(toolConfig.Operation, out var watchdogConfigDescriptor) && watchdogConfigDescriptor is not null)
            {
                tools.Add(new OperationTool(watchdogConfigDescriptor, toolConfig, watchdogConfigService, toolLogger, confirmationGate, name, correlationId));
            }
            else
            {
                // Should never happen — AgentConfigValidator checks this at startup.
                throw new InvalidOperationException($"Agent '{name}' references unknown operation '{toolConfig.Operation}'.");
            }
        }

        // Explicit Children (when declared) is an author-guaranteed structural edge — e.g. the
        // live-vs-simulator split — so it's always honored regardless of `depth`, unlike the
        // bounded similarity search below. `visited` still guards against an accidental YAML cycle.
        IReadOnlyList<string> candidateNames = config.Children is { } children
            ? children.Where(c => !visited.Contains(c)).ToList()
            : depth > 0
                ? retrievalIndex.RankCandidates(query, visited, retrievalOptions.MaxDelegatesPerAgent)
                : [];

        if (candidateNames.Count > 0)
        {
            logger.LogInformation("correlationId={CorrelationId} agent={Agent} delegates={Delegates}",
                correlationId, name, candidateNames);
        }

        foreach (var candidateName in candidateNames)
        {
            var childConfig = agents[candidateName]; // existence validated at startup
            var subAgent = BuildAgentRecursive(candidateName, childConfig, correlationId, query, operatorText, visited, depth - 1, tailNumberScope);
            var description = childConfig.Description!; // validated non-blank at startup
            AITool delegateTool = new DelegateAgentTool(candidateName, description, subAgent, toolLogger, name, correlationId);

            // Only MoavAgent's own delegates (its four fleet specialists) ever receive a free-text
            // instruction that might contain a tail number MoavAgent invented itself - see
            // TailNumberProvenanceGuardTool's own doc comment for why and how.
            if (name == MoavAgentName)
            {
                delegateTool = new TailNumberProvenanceGuardTool((AIFunction)delegateTool, operationService, operatorPromptGate,
                    tailNumberScope, toolLogger, candidateName, correlationId, operatorText);
            }

            tools.Add(delegateTool);
        }

        return tools;
    }

    private AIAgent BuildAgent(string name, AgentConfig config, List<AITool> tools, ChatHistoryProvider? chatHistoryProvider = null)
    {
        var chatClient = GetChatClient(config.Model, config.Provider);

        var options = new ChatClientAgentOptions
        {
            Name = name,
            Description = config.Description,
            UseProvidedChatClientAsIs = true, // our client is already wrapped with concurrent function invocation below — don't let ChatClientAgent re-wrap it
            // Only the persistent root agent (see GetOrCreatePersistentBrainAgentAsync) gets one of
            // these — every other agent built here is a single-turn, throwaway object, so leaving
            // this null keeps their (already stateless) behavior unchanged.
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

    private IChatClient GetChatClient(string? modelOverride, string? provider)
    {
        var model = modelOverride ?? defaultModel;
        // Provider has to be part of the cache key, not just the model name — an Ollama model and
        // an OpenRouter model could otherwise collide on an identical name string.
        var cacheKey = $"{provider ?? "Ollama"}::{model}";
        return _chatClients.GetOrAdd(cacheKey, _ => chatClientFactory(model, provider));
    }
}
