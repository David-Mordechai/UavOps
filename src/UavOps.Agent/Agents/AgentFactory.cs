using System.Collections.Concurrent;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Builds the agent graph fresh for each chat turn. Rebuilding is necessary (not just
/// cheap-and-easy) because every tool instance closes over the turn's correlationId, so logs
/// and traces for that turn are attributable end to end. Chat clients (one per distinct Ollama
/// model in use) are cached across turns.
/// </summary>
public sealed class AgentFactory(
    OllamaOptions ollamaOptions,
    Dictionary<string, AgentConfig> agents,
    OpenApiToolCatalog toolCatalog,
    UavApiToolInvoker apiInvoker,
    ToolInvocationLogger toolLogger,
    ConfirmationGate confirmationGate)
{
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    public AIAgent BuildMainAgentForTurn(string correlationId)
    {
        var mainConfig = agents["MainAgent"];

        var delegateTools = new List<AITool>();
        foreach (var delegateName in mainConfig.Delegates)
        {
            var subAgent = BuildDomainAgent(delegateName, agents[delegateName], correlationId);
            var description = agents[delegateName].Description ?? $"Delegate to the {delegateName}.";
            delegateTools.Add(new DelegateAgentTool(delegateName, description, subAgent, toolLogger, correlationId));
        }

        return BuildAgent("MainAgent", mainConfig, delegateTools);
    }

    private AIAgent BuildDomainAgent(string name, AgentConfig config, string correlationId)
    {
        var tools = new List<AITool>();
        foreach (var toolConfig in config.Tools)
        {
            if (!toolCatalog.TryResolve(toolConfig.OperationId, out var descriptor) || descriptor is null)
            {
                // Should never happen — AgentConfigValidator checks this at startup.
                throw new InvalidOperationException($"Agent '{name}' references unknown OpenAPI operationId '{toolConfig.OperationId}'.");
            }

            tools.Add(new UavApiOperationTool(descriptor, toolConfig, apiInvoker, toolLogger, confirmationGate, name, correlationId));
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
        var model = modelOverride ?? ollamaOptions.DefaultModel;
        return _chatClients.GetOrAdd(model, m =>
        {
            var ollama = new OllamaApiClient(new Uri(ollamaOptions.Endpoint), m);
            // When a model response contains multiple tool calls in one turn (e.g. MainAgent
            // delegating to FlightControlAgent and PayloadControlAgent at once, or FlightControlAgent
            // calling SetSpeed and SetAltitude at once), run them concurrently instead of the
            // default sequential-one-at-a-time invocation.
            return new FunctionInvokingChatClient(ollama) { AllowConcurrentInvocation = true };
        });
    }
}
