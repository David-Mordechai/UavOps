using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Agents;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Hubs;

/// <summary>
/// The one realtime surface for the chat SPA: chat turns, agent traces (tool name/args/result/
/// duration for every call), and any in-band operator round-trip — a yes/no confirmation
/// (ExecutionMode=Confirm) or an open-ended choice prompt (e.g. "which lesson?") — which both
/// happen entirely as chat messages, so every incoming message is first offered to
/// <see cref="ConfirmationGate.TryHandleChatReplyAsync"/> and then
/// <see cref="OperatorPromptGate.TryHandleChatReplyAsync"/> before being treated as a new command.
/// </summary>
public sealed class ChatHub(
    MainAgentOrchestrator orchestrator,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate,
    ToolInvocationLogger toolLogger,
    ILogger<ChatHub> logger) : Hub
{
    public async Task SendMessage(string user, string text, string correlationId)
    {
        await Clients.All.SendAsync("ReceiveChatMessage", user, text, 0d, correlationId);

        if (await confirmationGate.TryHandleChatReplyAsync(text, Context.ConnectionAborted))
        {
            return;
        }

        if (await operatorPromptGate.TryHandleChatReplyAsync(text, Context.ConnectionAborted))
        {
            return;
        }

        try
        {
            var (response, duration) = await orchestrator.HandleAsync(text, correlationId, Context.ConnectionAborted);
            await Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", CombineWithProactiveMessages(correlationId, response), duration, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "correlationId={CorrelationId} chat turn failed", correlationId);
            var errorText = CombineWithProactiveMessages(correlationId, $"Something went wrong: {ex.Message}");
            await Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", errorText, 0d, correlationId);
        }
    }

    /// <summary>Folds any messages an <see cref="Tooling.OperationTool"/> buffered mid-turn (e.g. a
    /// config's "yaml" snippet) ahead of the model's own final answer, so the operator sees one
    /// bubble per turn instead of a separate proactive one — see
    /// <see cref="ToolInvocationLogger.BufferProactiveMessage"/>. Applied on both the success and
    /// error paths: a tool call earlier in the turn may have genuinely succeeded even if a later
    /// step in the same turn failed.</summary>
    private string CombineWithProactiveMessages(string correlationId, string response)
    {
        var proactive = toolLogger.TakeProactiveMessages(correlationId);
        return proactive.Count == 0 ? response : string.Join("\n\n", proactive) + "\n\n" + response;
    }
}
