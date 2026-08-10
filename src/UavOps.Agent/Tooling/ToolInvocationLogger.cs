using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingProactiveMessages = new();

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

    /// <summary>Buffers a piece of text that must reliably reach the operator regardless of
    /// whether the model itself chooses to relay it in its own final reply — see
    /// <c>OperationTool</c>'s "yaml" result-field convention. Queued per-turn rather than sent
    /// immediately so <c>ChatHub</c> can fold it into that same turn's single final
    /// <c>ReceiveChatMessage</c> (<see cref="TakeProactiveMessages"/>) instead of it landing as a
    /// separate bubble that could arrive out of order relative to the model's own answer. A
    /// <see cref="ConcurrentQueue{T}"/> per correlationId because concurrent tool invocation
    /// (<c>AllowConcurrentInvocation</c>) means more than one tool call in the same turn could
    /// buffer a message at once.</summary>
    public void BufferProactiveMessage(string correlationId, string text)
    {
        _pendingProactiveMessages.GetOrAdd(correlationId, static _ => new ConcurrentQueue<string>()).Enqueue(text);
    }

    /// <summary>Removes and returns any messages buffered for this turn via
    /// <see cref="BufferProactiveMessage"/>, in the order they were recorded — called once by
    /// <c>ChatHub</c> right before sending the turn's final answer (on both the success and
    /// error paths, since a tool call earlier in the turn may have genuinely succeeded even if a
    /// later step in the same turn failed).</summary>
    public IReadOnlyList<string> TakeProactiveMessages(string correlationId) =>
        _pendingProactiveMessages.TryRemove(correlationId, out var queue) ? queue.ToArray() : [];
}
