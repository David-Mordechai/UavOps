using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Agents.MoavAgent.Simulation;

namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Wraps a <see cref="Tooling.OperationTool"/> for any operation with a "location" argument
/// (Navigate, PointPayload) to deterministically canonicalize it - via
/// <see cref="KnownPoints.Canonicalize"/> - before it ever reaches the operation itself, the
/// tool-call trace/log, or a result field an operation echoes it back through (e.g.
/// PointPayload's PayloadLockedOn). Observed directly: the model faithfully repeats back whichever
/// shape of a location name it was given - an operator who said "target alpha" got "target alpha"
/// in the final summary, one who said bare "alpha" got "alpha" back - it doesn't normalize on its
/// own. Rewriting the argument here, before anything downstream reads or logs it, means the operator
/// always sees the canonical bare name regardless of which shape they (or the model) used, without
/// depending on the model to get that consistent. This is not interpreting free-form operator intent
/// (the class of fix this codebase's operator has repeatedly and explicitly rejected) - "target" is
/// a single, fixed, known filler word specific to this module's own small, static reference
/// vocabulary (see <see cref="KnownPoints"/>'s own doc comment), not a guess about anything the
/// operator meant.
/// </summary>
public sealed class LocationCanonicalizationTool : AIFunction
{
    private readonly AIFunction _inner;

    public LocationCanonicalizationTool(AIFunction inner) => _inner = inner;

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("location", out var raw))
        {
            return await _inner.InvokeAsync(arguments, cancellationToken);
        }

        var location = ExtractString(raw);
        var canonical = KnownPoints.Canonicalize(location);

        if (string.Equals(canonical, location, StringComparison.Ordinal))
        {
            return await _inner.InvokeAsync(arguments, cancellationToken);
        }

        var rewritten = new AIFunctionArguments(arguments) { ["location"] = canonical };
        var result = await _inner.InvokeAsync(rewritten, cancellationToken);

        // Rewriting the argument alone isn't enough - the model still writes its own free-text
        // summary from what it recalls saying, not from the tool result, so without an explicit
        // correction here it keeps calling it "target alpha" in prose even though the call itself
        // now correctly used "alpha" (observed directly). Same forceful-note grounding pattern
        // TailNumberDisambiguationTool's BuildConfirmedTargetNote already established for the same
        // class of problem with tail numbers.
        return $"IMPORTANT - this location's real name is '{canonical}', not '{location}' - your summary must call it " +
               $"'{canonical}', never '{location}'. " + result;
    }

    private static string ExtractString(object? raw) => raw switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
        _ => raw.ToString() ?? ""
    };
}
