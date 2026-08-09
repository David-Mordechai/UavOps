using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Agents.MaintenanceAgent;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Agents.SimulatorAgent;
using UavOps.Agent.Contracts;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds the agent graph fresh for each chat turn. Rebuilding is necessary (not just
/// cheap-and-easy) because every tool instance closes over the turn's correlationId, so logs
/// and traces for that turn are attributable end-to-end. Chat clients (one per distinct model
/// in use) are cached across turns.
///
/// Delegate selection for each agent is either explicit (<see cref="AgentConfig.Children"/>, when
/// present) or embedding retrieval (<see cref="AgentRetrievalIndex"/>, the fallback for any agent
/// that doesn't declare <c>children:</c>) — see <see cref="BuildAgentRecursive"/>.
/// </summary>
public sealed class AgentFactory(
    Func<string, IChatClient> chatClientFactory,
    string defaultModel,
    Dictionary<string, AgentConfig> agents,
    OperationCatalog catalog,
    IOperationService operationService,
    OperationCatalog simulatorCatalog,
    ISimulatorService simulatorService,
    OperationCatalog watchdogCatalog,
    IWatchdogService watchdogService,
    AgentRetrievalIndex retrievalIndex,
    RetrievalOptions retrievalOptions,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate,
    ILogger<AgentFactory> logger)
{
    private const string RootAgentName = "BrainAgent";
    private const string OperatorPromptKind = "OperatorPrompt";

    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    public async Task<AIAgent> BuildMainAgentForTurn(string correlationId, string operatorText, CancellationToken cancellationToken)
    {
        var query = await retrievalIndex.EmbedQueryAsync(operatorText, cancellationToken);
        return BuildAgentRecursive(RootAgentName, agents[RootAgentName], correlationId, query, operatorText,
            visited: ImmutableHashSet<string>.Empty, depth: retrievalOptions.MaxDelegationDepth);
    }

    /// <summary>Builds one agent standalone, with no tools at all — used for a background job's
    /// proactive summary (see <c>SimulatorLessonJobProcessor</c>), where the same persona/
    /// instructions/model/temperature as the real agent gives a consistent voice, but attaching no
    /// tools means it structurally cannot re-trigger anything even if it misreads the prompt.</summary>
    public AIAgent BuildPersonaOnlyAgent(string agentName) => BuildAgent(agentName, agents[agentName], tools: []);

    private AIAgent BuildAgentRecursive(string name, AgentConfig config, string correlationId,
        Embedding<float> query, string operatorText, ImmutableHashSet<string> visited, int depth)
    {
        visited = visited.Add(name);
        var tools = new List<AITool>();

        foreach (var toolConfig in config.Tools)
        {
            if (toolConfig.Kind == OperatorPromptKind)
            {
                tools.Add(new AskOperatorChoiceTool(toolConfig, operatorPromptGate, toolLogger, name, correlationId, operatorText));
                continue;
            }

            if (catalog.TryResolve(toolConfig.Operation, out var descriptor) && descriptor is not null)
            {
                tools.Add(new OperationTool(descriptor, toolConfig, operationService, toolLogger, confirmationGate, name, correlationId));
            }
            else if (simulatorCatalog.TryResolve(toolConfig.Operation, out var simDescriptor) && simDescriptor is not null)
            {
                tools.Add(new OperationTool(simDescriptor, toolConfig, simulatorService, toolLogger, confirmationGate, name, correlationId));
            }
            else if (watchdogCatalog.TryResolve(toolConfig.Operation, out var watchdogDescriptor) && watchdogDescriptor is not null)
            {
                tools.Add(new OperationTool(watchdogDescriptor, toolConfig, watchdogService, toolLogger, confirmationGate, name, correlationId));
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
            var subAgent = BuildAgentRecursive(candidateName, childConfig, correlationId, query, operatorText, visited, depth - 1);
            var description = childConfig.Description!; // validated non-blank at startup
            tools.Add(new DelegateAgentTool(candidateName, description, subAgent, toolLogger, name, correlationId));
        }

        return BuildAgent(name, config, tools);
    }

    private AIAgent BuildAgent(string name, AgentConfig config, List<AITool> tools)
    {
        var chatClient = GetChatClient(config.Model);

        var options = new ChatClientAgentOptions
        {
            Name = name,
            Description = config.Description,
            UseProvidedChatClientAsIs = true, // our client is already wrapped with concurrent function invocation below — don't let ChatClientAgent re-wrap it
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

    private IChatClient GetChatClient(string? modelOverride)
    {
        var model = modelOverride ?? defaultModel;
        return _chatClients.GetOrAdd(model, chatClientFactory);
    }
}
