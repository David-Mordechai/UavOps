using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// The training simulator's full domain surface, model-facing tools only - VM readiness, lesson
/// discovery, starting a lesson run, and (see <see cref="AskOperatorWhichLesson"/>) resolving which
/// lesson the operator meant. Everything in this domain, including the background job queue/
/// processor and the operator lesson-choice prompt that both used to be host-side, lives entirely
/// in this process now - the host only supplies the one genuinely host-only capability (the actual
/// in-chat round-trip, via <c>ChatHub.RelayAskOperatorChoice</c>) when this domain's own
/// deterministic auto-resolve can't settle the question by itself. No <c>[McpServerTool]</c>/
/// <c>[Description]</c> attributes here anymore - every method's name/description/parameter
/// descriptions/annotations come from this project's own <c>ToolsConfig.yaml</c>, built at startup
/// by <see cref="McpToolsBuilder"/> (<c>Program.cs</c>) - editing a description or adding a
/// parameter description needs only a YAML edit and a process restart, never a rebuild. Method
/// names still have to match that YAML's <c>operation:</c> entries exactly (case-sensitive) -
/// <see cref="McpToolsBuilder.Build"/> fails fast at startup if either side has an entry the other
/// doesn't.
/// </summary>
public static class SimulatorTools
{
    private static readonly JsonSerializerOptions ResultSerializeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string ToResultText(OperationResult result) =>
        result.Success ? JsonSerializer.Serialize(result.Value, ResultSerializeOptions) : $"Error: {result.ErrorMessage}";

    public static async Task<string> EnsureVmwareHostRunning(ISimulatorInfraService simulator, CancellationToken cancellationToken) =>
        ToResultText(await simulator.EnsureVmwareHostRunning(cancellationToken));

    public static async Task<string> EnsureSimulatorVmRunning(ISimulatorInfraService simulator, CancellationToken cancellationToken) =>
        ToResultText(await simulator.EnsureSimulatorVmRunning(cancellationToken));

    public static async Task<string> ListSimulatorLessons(ISimulatorInfraService simulator, CancellationToken cancellationToken) =>
        ToResultText(await simulator.ListSimulatorLessons(cancellationToken));

    public static Task<string> RunSimulatorLesson(ISimulatorLessonJobQueue queue, string lessonName)
    {
        // Deliberately thin: the actual run (which can take minutes) and the "what happened"
        // evaluation both happen in the background via SimulatorLessonJobProcessor, which reports
        // to UavOps.Agent for a proactive plain-language summary once it's done - never blocking
        // this call, and never handing the model a multi-KB docker dump to parse.
        queue.Enqueue(new SimulatorLessonJob(lessonName, DateTimeOffset.UtcNow));
        return Task.FromResult(JsonSerializer.Serialize(new { status = "queued", lessonName }, ResultSerializeOptions));
    }

    public static async Task<string> AskOperatorWhichLesson(HubConnection hubConnection, List<string> lessons, string operatorMessage, CancellationToken cancellationToken)
    {
        if (lessons.Count == 0)
        {
            return "No choices were offered — cannot ask the operator anything.";
        }

        var autoMatch = LessonChoiceResolver.TryAutoResolve(lessons, operatorMessage);
        if (autoMatch is not null)
        {
            return autoMatch;
        }

        var question = "Which training lesson do you want to run?";
        string? choice;
        try
        {
            choice = await hubConnection.InvokeAsync<string?>("RelayAskOperatorChoice", question, lessons, cancellationToken);
        }
        catch (Exception ex)
        {
            // The host connection (see HubConnectionStarter) retries in the background rather than
            // blocking this server's own startup - if it genuinely isn't connected yet, or the call
            // otherwise fails, report that plainly instead of letting a raw connection exception
            // surface as this tool's result.
            return $"Could not reach the operator - not connected to UavOps.Agent's chat hub yet: {ex.Message}";
        }

        return choice ?? "Not answered: operator did not choose (or did not respond) within the time limit.";
    }
}
