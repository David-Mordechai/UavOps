using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Real <see cref="ILessonOutcomeNotifier"/> — calls <c>UavOps.Agent.Hubs.ChatHub.PushLessonOutcome</c>
/// over this process's own SignalR client connection to the host's <c>/chatHub</c> (the same
/// "connect back into the host for the one capability only it has" pattern
/// <c>UavOps.Agent.McpMoav.MoavRelayService</c> already established for the Moav domain - here
/// the host-only capability is BrainAgent's own persistent model session, not a physical hardware
/// connection).
/// </summary>
public sealed class HubLessonOutcomeNotifier(HubConnection hubConnection) : ILessonOutcomeNotifier
{
    public Task PushAsync(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration, CancellationToken cancellationToken) =>
        hubConnection.InvokeAsync("PushLessonOutcome", lessonName, outcome.ToString(), detail, duration.TotalSeconds, cancellationToken);
}
