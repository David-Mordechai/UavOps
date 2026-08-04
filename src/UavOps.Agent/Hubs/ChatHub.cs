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
            await Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", response, duration, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "correlationId={CorrelationId} chat turn failed", correlationId);
            await Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", $"Something went wrong: {ex.Message}", 0d, correlationId);
        }
    }
}
