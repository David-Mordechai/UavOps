using System.Diagnostics;

namespace UavOps.Agent.Agents;

/// <summary>Entry point the chat hub calls for one operator turn.</summary>
public sealed class MainAgentOrchestrator(AgentFactory agentFactory)
{
    public async Task<(string Response, double DurationSeconds)> HandleAsync(string text, string correlationId, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var mainAgent = agentFactory.BuildMainAgentForTurn(correlationId);
        var response = await mainAgent.RunAsync(text, cancellationToken: cancellationToken);
        sw.Stop();
        return (response.Text, sw.Elapsed.TotalSeconds);
    }
}
