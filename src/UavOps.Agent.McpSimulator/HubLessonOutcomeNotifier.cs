using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// Real <see cref="ILessonOutcomeNotifier"/>. Decides what the operator should be told about a
/// finished lesson (<see cref="BuildSummaryInstruction"/>) and hands it to the host's generic
/// <see cref="HostHubContract.Methods.PostPhrasedOperatorMessage"/> over this process's own SignalR
/// connection to <c>/chatHub</c>: the host only phrases it in BrainAgent's voice (its persona and
/// model live there) and shows it, knowing nothing about lessons.
/// </summary>
public sealed class HubLessonOutcomeNotifier(HubConnection hubConnection) : ILessonOutcomeNotifier
{
    public Task PushAsync(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration, CancellationToken cancellationToken) =>
        hubConnection.InvokeAsync(HostHubContract.Methods.PostPhrasedOperatorMessage,
            BuildSummaryInstruction(lessonName, outcome, detail, duration), duration.TotalSeconds, cancellationToken);

    public static string BuildSummaryInstruction(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration)
    {
        var outcomeText = outcome switch
        {
            LessonOutcome.Succeeded => "Succeeded, no problems detected.",
            LessonOutcome.SucceededWithWarnings => $"Succeeded, but with a warning: {detail}",
            LessonOutcome.Failed => $"Failed: {detail}",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
        };

        return $"You just finished running lesson '{lessonName}' in the background (it took {duration.TotalSeconds:0.#}s). " +
               $"Outcome: {outcomeText} Tell the operator this outcome in one or two short, plain sentences. " +
               "Do not invent anything beyond what's given here, and do not mention tool or operation names.";
    }
}
