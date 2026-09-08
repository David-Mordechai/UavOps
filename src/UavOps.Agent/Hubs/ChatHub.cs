using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Agents;
using UavOps.Agent.Contracts;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Hubs;

/// <summary>
/// The one realtime surface this app has - deliberately a single Hub class serving two very
/// different kinds of client:
/// <list type="bullet">
/// <item>The chat SPA (mapped at <c>/chatHub</c>): chat turns, agent traces, and any in-band
/// operator round-trip (a yes/no confirmation or an open-ended choice prompt), all as plain chat
/// messages via the untyped, dynamic <c>SendAsync("EventName", ...)</c> calls already established
/// here and in <see cref="ConfirmationGate"/>/<see cref="OperatorPromptGate"/>/
/// <see cref="ToolInvocationLogger"/> - all four, this Hub included, go through an injected
/// <c>IHubContext&lt;ChatHub&gt;</c> (the untyped flavor ASP.NET Core registers for any Hub
/// regardless of whether it also derives from <see cref="Hub{T}"/>) rather than this Hub's own
/// inherited <c>Clients</c> property, which - confirmed live, not just by inspection - throws
/// <see cref="InvalidCastException"/> if cast to the untyped <see cref="IHubCallerClients"/>: once
/// a Hub derives from <see cref="Hub{T}"/>, its inherited <c>Clients</c> is backed by a
/// <c>TypedHubClients&lt;T&gt;</c> wrapper that does NOT also implement the untyped interface,
/// unlike the separate, independently-registered <c>IHubContext&lt;ChatHub&gt;</c> DI already hands
/// to everything else that talks to this Hub from outside it.</item>
/// <item>The real (or mock) Moav-commanding client (mapped at <c>/uavCommandHub</c>, unchanged -
/// see <see cref="IOperationClientProxy"/>'s own doc comment for why its methods must stay
/// byte-for-byte stable): the strongly-typed <see cref="Hub{T}"/> half, handled by
/// <see cref="OnConnectedAsync"/>/<see cref="OnDisconnectedAsync"/>/<see cref="SubmitCommandResult"/>
/// plus the dozen <c>Relay*</c> methods below.</item>
/// </list>
/// Both roles were previously two separate Hub classes (this one, plus a since-deleted
/// <c>OperationHub</c>) mapped to their own endpoints. Merged into one because nothing about them
/// actually needs to be separate Hub *classes* - only separate *endpoints*, which both still are
/// (unchanged) - and because <c>UavOps.Agent.McpMoav</c> (see <see cref="RelayNavigate"/> and
/// friends) needed a single, obvious place to relay Moav operations through as a SignalR client
/// of its own, alongside the real Moav client it's relaying to. <c>UavOps.Agent.McpSimulator</c>
/// uses this same "connect back for the one host-only capability" pattern via
/// <see cref="PushLessonOutcome"/> below, for a different capability entirely (BrainAgent's own
/// persona/model, not a physical hardware connection).
/// </summary>
public sealed class ChatHub(
    MainAgentOrchestrator orchestrator,
    ConfirmationGate confirmationGate,
    OperatorPromptGate operatorPromptGate,
    ToolInvocationLogger toolLogger,
    IRemoteOperationBroker broker,
    IHubContext<ChatHub> hubContext,
    AgentFactory agentFactory,
    ILogger<ChatHub> logger) : Hub<IOperationClientProxy>
{
    public async Task SendMessage(string user, string text, string correlationId)
    {
        await hubContext.Clients.All.SendAsync("ReceiveChatMessage", user, text, 0d, correlationId);

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
            await hubContext.Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", CombineWithProactiveMessages(correlationId, response), duration, correlationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "correlationId={CorrelationId} chat turn failed", correlationId);
            var errorText = CombineWithProactiveMessages(correlationId, $"Something went wrong: {ex.Message}");
            await hubContext.Clients.All.SendAsync("ReceiveChatMessage", "BrainAgent", errorText, 0d, correlationId);
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

    // ----- Moav-commanding client half (formerly OperationHub) -----

    /// <summary>The two things this merged Hub must get right: <see cref="broker"/> must only ever
    /// track a connection that both (a) actually came in on the Moav-commanding endpoint - both
    /// endpoints route through this same Hub class now, so without checking the path, an ordinary
    /// browser chat tab connecting (or refreshing) at <c>/chatHub</c> would call
    /// <see cref="OnConnectedAsync"/> too and - via
    /// <see cref="IRemoteOperationBroker.RegisterConnection"/>'s last-writer-wins semantics, needed
    /// for the real "a new Moav client replaced an old one" case - silently hijack the broker's
    /// tracked connection away from the real Moav-hardware client, breaking every real Moav
    /// operation until that browser tab disconnected (live-reproduced during this change's own
    /// verification before this check was added); and (b) is genuinely the real (or mock) Moav
    /// hardware, not <c>UavOps.Agent.McpMoav</c>'s own relay client connecting to this same
    /// endpoint under its own <c>OperationBackend: SignalR</c> - excluded via its
    /// <c>?client=relay</c> query marker, since without it a McpMoav reconnect (its own
    /// <c>WithAutomaticReconnect</c>, e.g. after a transient network blip) would otherwise register
    /// itself as the tracked target and silently steal Moav commands away from the real hardware
    /// client, which is never itself a source of Relay* calls.</summary>
    private bool IsMoavCommandEndpoint
    {
        get
        {
            var httpContext = Context.GetHttpContext();
            return httpContext?.Request.Path == "/uavCommandHub" && httpContext.Request.Query["client"] != "relay";
        }
    }

    public override Task OnConnectedAsync()
    {
        if (IsMoavCommandEndpoint)
        {
            broker.RegisterConnection(Context.ConnectionId);
        }

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (IsMoavCommandEndpoint)
        {
            broker.UnregisterConnection(Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Called by the connected real Moav client once it has handled an operation —
    /// resolves the broker's pending call for that <paramref name="correlationId"/>.</summary>
    public Task SubmitCommandResult(string correlationId, bool success, string? errorMessage, string? resultJson) =>
        broker.Complete(correlationId, success, errorMessage, resultJson);

    // ----- UavOps.Agent.McpMoav relay half -----
    //
    // Called by McpMoav's own SignalR client (see MoavRelayService there) when its MoavTools run
    // under OperationBackend: SignalR - the exact same real Moav-client relay RemoteOperationService
    // used to perform in-process before the Moav domain moved to MCP, just invoked from the other
    // side now. Each method is the same one-liner RemoteOperationService always had (call
    // IRemoteOperationBroker.SendAsync with the matching typed IOperationClientProxy call), only the
    // final camelCase-JSON-or-"Error: ..." formatting moved here from the (now deleted)
    // Tooling/OperationTool.cs-equivalent, since a plain string is the simplest thing to hand back
    // over a SignalR client call with zero JSON-casing ambiguity (System.Text.Json only preserves a
    // CLR type's declared property casing through this Hub's own outgoing serialization when the
    // value is boxed as `object` - a plain, already-formatted string sidesteps that entirely).
    //
    // Deliberately no CancellationToken parameter - SignalR does NOT treat a trailing
    // CancellationToken specially for a client-invoked hub method the way it does for streaming
    // methods; live-reproduced: the client got "invocation provided 2 arguments but the target
    // expected 3" the moment one was added, because SignalR counted it as a real argument the
    // caller must supply. CancellationToken.None is passed to the broker instead - its own
    // RemoteOperationOptions.TimeoutSeconds already bounds how long a call can wait.

    private static readonly JsonSerializerOptions ResultSerializeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string ToResultText(OperationResult result) =>
        result.Success ? JsonSerializer.Serialize(result.Value, ResultSerializeOptions) : $"Error: {result.ErrorMessage}";

    public async Task<string> RelayListFleet() =>
        ToResultText(await broker.SendAsync<List<UavSummary>>((proxy, correlationId) => proxy.ListFleet(correlationId), CancellationToken.None));

    public async Task<string> RelayGetTelemetry(string tailNumber) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.GetTelemetry(correlationId, tailNumber), CancellationToken.None));

    public async Task<string> RelayNavigate(string tailNumber, string location) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.Navigate(correlationId, tailNumber, location), CancellationToken.None));

    public async Task<string> RelaySetSpeed(string tailNumber, int speedKts) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.SetSpeed(correlationId, tailNumber, speedKts), CancellationToken.None));

    public async Task<string> RelaySetAltitude(string tailNumber, int altitudeFt) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.SetAltitude(correlationId, tailNumber, altitudeFt), CancellationToken.None));

    public async Task<string> RelayReturnToLaunch(string tailNumber) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.ReturnToLaunch(correlationId, tailNumber), CancellationToken.None));

    public async Task<string> RelayPointPayload(string tailNumber, string location) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.PointPayload(correlationId, tailNumber, location), CancellationToken.None));

    public async Task<string> RelayResetPayload(string tailNumber) =>
        ToResultText(await broker.SendAsync<TelemetrySnapshot>((proxy, correlationId) => proxy.ResetPayload(correlationId, tailNumber), CancellationToken.None));

    public async Task<string> RelayUploadWaypoints(string tailNumber, List<Waypoint> waypoints) =>
        ToResultText(await broker.SendAsync<int>((proxy, correlationId) => proxy.UploadWaypoints(correlationId, tailNumber, waypoints), CancellationToken.None));

    public async Task<string> RelayGetMissionStatus(string tailNumber) =>
        ToResultText(await broker.SendAsync<MissionStatus>((proxy, correlationId) => proxy.GetMissionStatus(correlationId, tailNumber), CancellationToken.None));

    public async Task<string> RelayGetLinkStatus(string tailNumber) =>
        ToResultText(await broker.SendAsync<GdtLinkStatus>((proxy, correlationId) => proxy.GetLinkStatus(correlationId, tailNumber), CancellationToken.None));

    public async Task<string> RelaySetTrackingMode(string tailNumber, string mode) =>
        ToResultText(await broker.SendAsync<GdtLinkStatus>((proxy, correlationId) => proxy.SetTrackingMode(correlationId, tailNumber, mode), CancellationToken.None));

    // ----- UavOps.Agent.McpSimulator lesson-outcome half -----

    /// <summary>
    /// Called by McpSimulator's own SignalR client (<c>HubLessonOutcomeNotifier</c>) once its
    /// background <c>SimulatorLessonJobProcessor</c> finishes running a lesson - the "how to say
    /// it" step (building the "simple terms" sentence in BrainAgent's own voice) that only this
    /// process can do, since it alone holds the persistent model session. Builds a tools-stripped
    /// instance of BrainAgent (<see cref="AgentFactory.BuildPersonaOnlyAgent"/> — same persona/
    /// voice as the real agent, but structurally unable to call any tool again) and runs it once
    /// with a synthetic instruction summarizing the concise outcome McpSimulator already decided,
    /// then pushes the result as an ordinary <c>ReceiveChatMessage</c> under a fresh correlationId
    /// — the chat UI already renders any such message as a new bubble the first time it sees that
    /// correlationId, so this appears as a new, unprompted message in the thread with no frontend
    /// changes needed.
    /// </summary>
    public async Task PushLessonOutcome(string lessonName, string outcome, string? detail, double durationSeconds)
    {
        var parsedOutcome = Enum.TryParse<LessonOutcome>(outcome, out var o) ? o : LessonOutcome.Failed;
        var instruction = BuildSummaryInstruction(lessonName, parsedOutcome, detail, TimeSpan.FromSeconds(durationSeconds));
        var summaryAgent = agentFactory.BuildPersonaOnlyAgent();
        var response = await summaryAgent.RunAsync(instruction);

        var proactiveCorrelationId = Guid.NewGuid().ToString("N")[..8];
        await hubContext.Clients.All.SendAsync("ReceiveChatMessage", AgentFactory.RootAgentName, response.Text, durationSeconds, proactiveCorrelationId);
    }

    private static string BuildSummaryInstruction(string lessonName, LessonOutcome outcome, string? detail, TimeSpan duration)
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

    // ----- Generic operator-prompt relay half -----

    /// <summary>
    /// Called by any MCP server's own SignalR client (today only
    /// <c>UavOps.Agent.McpSimulator</c>'s <c>AskOperatorWhichLesson</c> tool, when its own
    /// deterministic auto-resolve can't settle on exactly one choice) to run the in-chat
    /// "ask an open question, read back the answer" round-trip - a thin wrapper over the existing
    /// <see cref="OperatorPromptGate.RequestChoiceAsync"/>, the same host-only chat-hub
    /// infrastructure <see cref="PushLessonOutcome"/> above already reuses for a different
    /// capability. Deliberately domain-agnostic (no lesson/simulator knowledge here at all) so any
    /// future MCP server needing an open-ended operator prompt can call this same method - the
    /// deciding of *what* to ask, and *whether* asking is even needed, stays entirely with the
    /// calling domain.
    /// </summary>
    public Task<string?> RelayAskOperatorChoice(string question, List<string> choices)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        return operatorPromptGate.RequestChoiceAsync(correlationId, AgentFactory.RootAgentName, question, choices, CancellationToken.None);
    }
}
