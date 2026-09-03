using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Wraps a MoavAgent -> specialist <see cref="DelegateAgentTool"/> (FlightControlAgent,
/// PayloadControlAgent, GdtControlAgent, MissionAgent) for two related problems, both about the
/// tail number a delegated instruction does or doesn't carry - neither solved by guessing at
/// wording or assuming a naming format (no regex, no fixed pattern; a real fleet's tail numbers
/// could be "UAV-1", "UAV 999", or anything else, and this system is domain-agnostic about that
/// shape by design - see <see cref="IOperationService"/>'s own doc comment). Both use
/// <see cref="IOperationService.ListFleet"/> as the one source of truth for what a "real" tail
/// number even is.
///
/// 1. MoavAgent inventing a tail number itself when delegating - observed directly, repeatedly:
///    when a delegate's response comes back malformed or unhelpful, MoavAgent sometimes "fixes" it
///    by guessing a tail number and re-delegating with it explicitly named, which bypasses the
///    leaf-level <see cref="TailNumberDisambiguationTool"/> guard entirely (that guard correctly
///    trusts any tail number literally present in the instruction it was given - exactly what lets
///    an invented one slip through undetected). Checked before delegating: if the instruction names
///    a real, known tail number that never appears anywhere in the text MoavAgent was actually given
///    this turn, the delegation is blocked - a real value MoavAgent introduced beyond that can only
///    mean MoavAgent invented it, regardless of what it looks like. "The text MoavAgent was actually
///    given" is <see cref="DelegatedInstructionContext.Current"/> - BrainAgent now carries real
///    multi-turn memory and may legitimately resolve a reference to something established earlier
///    in the conversation ("fly it to alpha" -> "fly UAV-1 to alpha") before ever delegating to
///    MoavAgent, so grounding against only the operator's raw *this-turn* message
///    (<c>_rootOperatorText</c>) would falsely flag a tail number BrainAgent had every
///    right to include. Falls back to the root operator text only when nothing was delegated in
///    between (mirrors <see cref="TailNumberDisambiguationTool"/>'s own fallback).
///
/// 2. A specialist answering an ambiguous instruction with plain text instead of calling a tool at
///    all - observed directly: FlightControlAgent sometimes responds "I need to know which UAV..."
///    without ever calling SetSpeed/etc., so TailNumberDisambiguationTool (which only ever sees
///    real tool calls) never gets a chance to turn that into the operator's usual structured
///    Yes/No or "which UAV" prompt - the operator just gets an ordinary chat message with no
///    buttons. Forcing tool use via the chat completion API (ChatToolMode.RequireAny) was tried and
///    ruled out - it's silently ignored by Ollama and, worse, appears to hang the production
///    OpenAI-compatible backend entirely when set. So this is handled after the fact instead: if
///    delegating produced zero tool calls for that specialist (tracked via
///    <see cref="ToolInvocationLogger.GetAgentInvocationCount"/>, taken immediately before and
///    after the delegation so concurrent/earlier calls to the same specialist elsewhere in the turn
///    don't skew the count) and the instruction never named a real, known tail number, the
///    specialist genuinely had nothing to go on - so a real structured prompt is forced via
///    <see cref="OperatorPromptGate"/> (deduplicated per turn through the shared
///    <see cref="TailNumberResolutionScope"/>, same as the leaf-level guard) and the same
///    instruction is retried once with the operator's real answer appended.
/// </summary>
public sealed class TailNumberProvenanceGuardTool : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IOperationService _operationService;
    private readonly OperatorPromptGate _promptGate;
    private readonly TailNumberResolutionScope _scope;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly string _delegateName;
    private readonly string _correlationId;
    private readonly string _rootOperatorText;

    public TailNumberProvenanceGuardTool(AIFunction inner, IOperationService operationService, OperatorPromptGate promptGate,
        TailNumberResolutionScope scope, ToolInvocationLogger toolLogger, string delegateName, string correlationId, string rootOperatorText)
    {
        _inner = inner;
        _operationService = operationService;
        _promptGate = promptGate;
        _scope = scope;
        _toolLogger = toolLogger;
        _delegateName = delegateName;
        _correlationId = correlationId;
        _rootOperatorText = rootOperatorText;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        arguments.TryGetValue("instruction", out var raw);
        var instruction = raw?.ToString() ?? "";

        var fleet = await _operationService.ListFleet(cancellationToken);
        var tails = fleet.Success && fleet.Value is List<UavSummary> t ? t : null;
        var groundingText = DelegatedInstructionContext.Current ?? _rootOperatorText;

        if (tails is not null)
        {
            var invented = tails
                .Select(x => x.TailNumber)
                .Where(tail =>
                    instruction.Contains(tail, StringComparison.OrdinalIgnoreCase) &&
                    !groundingText.Contains(tail, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (invented.Count > 0)
            {
                return $"Not delegated: this instruction names {string.Join(", ", invented)}, which the operator never actually said. " +
                       "Never guess a tail number yourself - if a delegate's earlier response was unclear or failed, relay that plainly " +
                       "to the operator instead of resending with a tail number you invented.";
            }
        }

        var countBefore = _toolLogger.GetAgentInvocationCount(_correlationId, _delegateName);
        var result = await _inner.InvokeAsync(arguments, cancellationToken);
        var stayedSilent = _toolLogger.GetAgentInvocationCount(_correlationId, _delegateName) == countBefore;

        var namesNoRealTail = tails is { Count: > 1 } &&
            !tails.Any(t => instruction.Contains(t.TailNumber, StringComparison.OrdinalIgnoreCase));

        if (!stayedSilent || !namesNoRealTail)
        {
            return result;
        }

        // The specialist called no tool at all on a genuinely ambiguous instruction (no real tail
        // number named anywhere in it) - force the same structured ask the leaf-level guard would
        // have used had it actually been given a chance to guess, instead of relaying whatever
        // plain text the specialist said.
        var chosen = await _scope.GetOrAskAsync(() => _promptGate.RequestChoiceAsync(
            _correlationId,
            _delegateName,
            "Which UAV do you mean?",
            tails!.Select(x => x.TailNumber).ToList(),
            cancellationToken));

        if (chosen is null)
        {
            return "Not delegated: operator did not specify which UAV (or did not respond in time).";
        }

        var retryArgs = new AIFunctionArguments(arguments) { ["instruction"] = $"{instruction} for {chosen}" };
        return await _inner.InvokeAsync(retryArgs, cancellationToken);
    }
}
