using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Operations;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds the agent graph fresh for each chat turn. Rebuilding is necessary (not just
/// cheap-and-easy) because every tool instance closes over the turn's correlationId, so logs
/// and traces for that turn are attributable end-to-end. Chat clients (one per distinct model
/// in use) are cached across turns.
/// </summary>
public sealed class AgentFactory(
    Func<string, IChatClient> chatClientFactory,
    string defaultModel,
    Dictionary<string, AgentConfig> agents,
    OperationCatalog catalog,
    IOperationService operationService,
    AgentRetrievalIndex retrievalIndex,
    RetrievalOptions retrievalOptions,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate,
    ILogger<AgentFactory> logger)
{
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    public async Task<AIAgent> BuildMainAgentForTurn(string correlationId, string operatorText, CancellationToken cancellationToken)
    {
        var query = await retrievalIndex.EmbedQueryAsync(operatorText, cancellationToken);
        return BuildAgentRecursive("MainAgent", agents["MainAgent"], correlationId, query,
            visited: ImmutableHashSet<string>.Empty, depth: retrievalOptions.MaxDelegationDepth);
    }

    private AIAgent BuildAgentRecursive(string name, AgentConfig config, string correlationId,
        Embedding<float> query, ImmutableHashSet<string> visited, int depth)
    {
        visited = visited.Add(name);
        var tools = new List<AITool>();

        foreach (var toolConfig in config.Tools)
        {
            if (!catalog.TryResolve(toolConfig.Operation, out var descriptor) || descriptor is null)
            {
                // Should never happen — AgentConfigValidator checks this at startup.
                throw new InvalidOperationException($"Agent '{name}' references unknown operation '{toolConfig.Operation}'.");
            }

            tools.Add(new OperationTool(descriptor, toolConfig, operationService, toolLogger, confirmationGate, name, correlationId));
        }

        if (depth > 0)
        {
            var candidateNames = retrievalIndex.RankCandidates(query, visited, retrievalOptions.MaxDelegatesPerAgent);
            logger.LogInformation("correlationId={CorrelationId} agent={Agent} delegates={Delegates}",
                correlationId, name, candidateNames);
            foreach (var candidateName in candidateNames)
            {
                var subAgent = BuildAgentRecursive(candidateName, agents[candidateName], correlationId, query, visited, depth - 1);
                var description = agents[candidateName].Description!; // validated non-blank at startup
                tools.Add(new DelegateAgentTool(candidateName, description, subAgent, toolLogger, correlationId));
            }
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
