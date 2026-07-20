using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Agents;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Hubs;

/// <summary>
/// The one realtime surface for the chat SPA: chat turns, agent traces (tool name/args/result/
/// duration for every call), and the confirmation round-trip when ExecutionMode=Confirm.
/// </summary>
public sealed class ChatHub(MainAgentOrchestrator orchestrator, ConfirmationGate confirmationGate, ILogger<ChatHub> logger) : Hub
{
    public async Task SendMessage(string user, string text, string correlationId)
    {
        await Clients.All.SendAsync("ReceiveChatMessage", user, text, 0d, correlationId);

        try
        {
            var (response, duration) = await orchestrator.HandleAsync(text, correlationId, Context.ConnectionAborted);
            await Clients.All.SendAsync("ReceiveChatMessage", "MainAgent", response, duration, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "correlationId={CorrelationId} chat turn failed", correlationId);
            await Clients.All.SendAsync("ReceiveChatMessage", "MainAgent", $"Something went wrong: {ex.Message}", 0d, correlationId);
        }
    }

    public void SendConfirmationResponse(string confirmationId, bool approved) =>
        confirmationGate.Resolve(confirmationId, approved);
}
