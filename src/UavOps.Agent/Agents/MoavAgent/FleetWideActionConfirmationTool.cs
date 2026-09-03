using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent.Operations;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Wraps BrainAgent's own delegation to MoavAgent specifically, to catch a whole-fleet action that
/// bypasses the existing "ALL" sentinel confirmation. BrainAgent now carries real multi-turn memory
/// (see <c>MainAgentOrchestrator</c>'s doc comment) and may legitimately resolve a collective
/// reference like "all of them" into an explicit list of every known tail number before delegating -
/// but doing so makes the resulting instruction indistinguishable, to MoavAgent, from an operator who
/// typed every tail number out by hand, which MoavAgent's own rules correctly treat as "no ambiguity,
/// just fan out" - skipping the "Apply this to all N known UAVs?" confirmation
/// (<see cref="TailNumberDisambiguationTool.ResolveAllUavsRequestAsync"/> normally provides for
/// exactly this) entirely. Observed directly: a "fly all of them..." request executed against all 3
/// UAVs with zero operator sign-off, because BrainAgent had written out "UAV-1, UAV-2, UAV-3"
/// explicitly rather than passing "all of them" through.
///
/// Deterministic, not a rewording of the "don't do this" instruction alone - <c>BrainAgent.yaml</c>
/// also says not to expand a collective reference into an explicit list, but this session has
/// repeatedly learned not to trust prompt wording alone for anything safety-relevant. If the
/// delegated instruction names every currently-known real tail number (via
/// <see cref="IOperationService.ListFleet"/>, the same source of truth every other tail-number guard
/// in this codebase uses) but the operator's own raw message this turn does not also name them all,
/// something upstream expanded a collective reference - force the same confirmation an explicit
/// "ALL" recognition already requires. If the operator's own words already named every tail number
/// themselves, no confirmation is added here - that's the normal "operator named 2+ specific UAVs"
/// path MoavAgent already handles correctly, and always has.
///
/// Deliberately does NOT also check for an invented tail number the way
/// <see cref="TailNumberProvenanceGuardTool"/> does for MoavAgent's own delegation to its
/// specialists - that check's premise (a memory-less delegate should never introduce a real value it
/// wasn't given) doesn't hold here, since BrainAgent legitimately resolves references from its own
/// conversation history and is expected to hand MoavAgent a real, resolved tail number that the raw
/// operator text for this turn alone would never contain.
/// </summary>
public sealed class FleetWideActionConfirmationTool : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IOperationService _operationService;
    private readonly OperatorPromptGate _promptGate;
    private readonly TailNumberResolutionScope _scope;
    private readonly string _delegateName;
    private readonly string _correlationId;
    private readonly string _rootOperatorText;

    public FleetWideActionConfirmationTool(AIFunction inner, IOperationService operationService, OperatorPromptGate promptGate,
        TailNumberResolutionScope scope, string delegateName, string correlationId, string rootOperatorText)
    {
        _inner = inner;
        _operationService = operationService;
        _promptGate = promptGate;
        _scope = scope;
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
        if (fleet.Success && fleet.Value is List<UavSummary> { Count: > 1 } tails)
        {
            var namesEveryTail = tails.All(t => instruction.Contains(t.TailNumber, StringComparison.OrdinalIgnoreCase));
            var operatorNamedThemAllThemselves = tails.All(t => _rootOperatorText.Contains(t.TailNumber, StringComparison.OrdinalIgnoreCase));

            if (namesEveryTail && !operatorNamedThemAllThemselves)
            {
                var tailList = string.Join(", ", tails.Select(t => t.TailNumber));
                var confirmed = await _scope.GetOrAskAsync(() => _promptGate.RequestChoiceAsync(
                    _correlationId,
                    _delegateName,
                    $"Apply this to all {tails.Count} known UAVs ({tailList})?",
                    ["Yes", "No"],
                    cancellationToken));

                if (!string.Equals(confirmed, "Yes", StringComparison.OrdinalIgnoreCase))
                {
                    return "Not delegated: operator did not confirm applying this to every known UAV (or did not respond in time).";
                }
            }
        }

        return await _inner.InvokeAsync(arguments, cancellationToken);
    }
}
