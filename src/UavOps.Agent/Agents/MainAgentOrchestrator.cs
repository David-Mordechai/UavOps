using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace UavOps.Agent.Agents;

/// <summary>Entry point the chat hub calls for one operator turn.</summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory)
{
    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        // BrainAgent itself is long-lived (carries the reused session/history across turns); only
        // its tools are rebuilt every turn, for correlationId-scoped tracing — see AgentFactory.
        var (brainAgent, session) = await agentFactory.GetOrCreatePersistentBrainAgentAsync(cancellationToken);
        var tools = await agentFactory.BuildRootToolsForTurn(correlationId, text, cancellationToken);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { Tools = tools, AllowMultipleToolCalls = true });

        var response = await brainAgent.RunAsync(text, session, runOptions, cancellationToken: cancellationToken);
        sw.Stop();
        return (response.Text, sw.Elapsed.TotalSeconds);
    }
}
