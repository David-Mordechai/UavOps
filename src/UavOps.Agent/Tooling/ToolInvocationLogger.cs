using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Hubs;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Wraps every tool execution (agent-to-sub-agent delegation and agent-to-UAV-API calls alike)
/// with one structured log line and one realtime trace event, both carrying the same
/// correlationId so a chat turn's full reasoning path is reconstructable end to end.
/// </summary>
public sealed class ToolInvocationLogger(ILogger<ToolInvocationLogger> logger, IHubContext<ChatHub> hub)
{
    public async Task<TResult> LogAsync<TResult>(
        string correlationId,
        string agentName,
        string toolName,
        object? arguments,
        Func<Task<TResult>> action,
        Func<TResult, string> summarize)
    {
        var argsJson = JsonSerializer.Serialize(arguments);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await action();
            sw.Stop();
            var summary = summarize(result);

            logger.LogInformation(
                "[Tool] correlationId={CorrelationId} agent={Agent} tool={Tool} args={Args} result={Result} durationMs={DurationMs}",
                correlationId, agentName, toolName, argsJson, summary, sw.ElapsedMilliseconds);

            await hub.Clients.All.SendAsync("ReceiveAgentTrace", correlationId, agentName, toolName, argsJson, summary, sw.Elapsed.TotalSeconds);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();

            logger.LogError(ex,
                "[Tool] correlationId={CorrelationId} agent={Agent} tool={Tool} args={Args} FAILED durationMs={DurationMs}",
                correlationId, agentName, toolName, argsJson, sw.ElapsedMilliseconds);

            await hub.Clients.All.SendAsync("ReceiveAgentTrace", correlationId, agentName, toolName, argsJson, $"ERROR: {ex.Message}", sw.Elapsed.TotalSeconds);
            throw;
        }
    }
}
