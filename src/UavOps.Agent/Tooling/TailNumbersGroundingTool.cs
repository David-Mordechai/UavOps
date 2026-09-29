using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Wraps any tool whose schema declares a <c>tailNumbers</c> list: a tool that acts on a set of
/// UAVs together in ONE call (e.g. a search that splits a zone between them), so unlike
/// <see cref="TailNumberDisambiguationTool"/> it never fans out one call per UAV - that would
/// defeat the point. Which UAVs the operator meant is the model's call (for "all UAVs" it lists
/// the fleet itself; there is no "ALL" sentinel here); this only checks the list before the one
/// call goes out, deterministically:
/// <list type="bullet">
/// <item>"ALL": not executed; the model is told to pass real tail numbers.</item>
/// <item>An empty list (the operator named no UAV): the operator is asked which UAV to use.</item>
/// <item>Tail numbers that aren't in the fleet are dropped (the result says which).</item>
/// <item>One UAV: the same grounding as a single <c>tailNumber</c> - trusted when the operator
/// named it or it's their current UAV (<see cref="OperatorUavContext"/>), otherwise the operator
/// is asked which UAV they mean.</item>
/// <item>Several UAVs: trusted when the operator's message backs a group - it names every one of
/// them, or refers to a group ("all", "both", "them"...). Otherwise the operator is asked to
/// confirm that exact set, so a model turning an unspecified request into "every UAV" can't act
/// on its own judgment.</item>
/// </list>
/// Generic: keyed off the schema property, like <see cref="TailNumberDisambiguationTool"/> and
/// <see cref="LocationCanonicalizationTool"/>; nothing here knows what the tool does.
/// </summary>
public sealed class TailNumbersGroundingTool : AIFunction
{
    public const string PropertyName = "tailNumbers";
    private const string AllSentinel = "ALL";

    private readonly AIFunction _inner;
    private readonly Func<CancellationToken, Task<OperationResult>> _listFleet;
    private readonly OperatorPromptGate _promptGate;
    private readonly TailNumberResolutionScope _scope;
    private readonly FleetGroupMemory _groupMemory;
    private readonly OperatorUavContext _uavContext;
    private readonly string _agentName;
    private readonly string _correlationId;
    private readonly string _operatorText;

    public TailNumbersGroundingTool(AIFunction inner, Func<CancellationToken, Task<OperationResult>> listFleet, OperatorPromptGate promptGate,
        TailNumberResolutionScope scope, FleetGroupMemory groupMemory, OperatorUavContext uavContext, string agentName, string correlationId,
        string operatorText)
    {
        _inner = inner;
        _listFleet = listFleet;
        _promptGate = promptGate;
        _scope = scope;
        _groupMemory = groupMemory;
        _uavContext = uavContext;
        _agentName = agentName;
        _correlationId = correlationId;
        _operatorText = operatorText;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue(PropertyName, out var raw))
            return await _inner.InvokeAsync(arguments, cancellationToken);

        var requested = ReadList(raw);
        if (requested.Any(t => string.Equals(t, AllSentinel, StringComparison.OrdinalIgnoreCase)))
            return "Not executed: 'ALL' is not a tail number. Call ListFleet and pass every UAV's real tail number in tailNumbers.";

        var fleet = await _listFleet(cancellationToken);
        if (!fleet.Success || fleet.Value is not List<UavSummary> known)
        {
            return requested.Count == 0
                ? "Not executed: no UAV was given and the fleet couldn't be listed to ask which one."
                : await InvokeAsync(arguments, requested, "", cancellationToken);   // nothing to check against
        }

        // An empty list means the operator didn't name a UAV: ask them, rather than have the model
        // guess one. Live-measured: told to "call with any UAV you know", it asked "Which UAV?" in
        // plain text instead, with nothing called (AoiSearchMissionLiveTests 6/8).
        if (requested.Count == 0)
        {
            return known.Count == 1
                ? await InvokeAsync(arguments, [known[0].TailNumber], "", cancellationToken)
                : await AskWhichUavAsync(arguments, "", known, cancellationToken);
        }

        var valid = new List<string>();
        var unknown = new List<string>();
        foreach (var tail in requested)
        {
            var match = known.FirstOrDefault(k => string.Equals(k.TailNumber, tail, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                unknown.Add(tail);
            else
                valid.Add(match.TailNumber);
        }
        var droppedNote = unknown.Count == 0
            ? ""
            : $"Note - {string.Join(", ", unknown)} {(unknown.Count == 1 ? "is not a known UAV and was" : "are not known UAVs and were")} left out. ";

        if (valid.Count == 0)
        {
            if (known.Count <= 1)
                return await InvokeAsync(arguments, requested, "", cancellationToken);   // nothing to disambiguate
            return droppedNote + await AskWhichUavAsync(arguments, requested[0], known, cancellationToken);
        }

        if (valid.Count == 1)
        {
            var tail = valid[0];
            if (known.Count <= 1 || _uavContext.Grounds(tail, _operatorText))
                return await InvokeAsync(arguments, valid, droppedNote, cancellationToken);
            if (Named(tail))
            {
                RememberIfOnlyUavNamed(tail, known);
                return await InvokeAsync(arguments, valid, droppedNote, cancellationToken);
            }
            return droppedNote + await AskWhichUavAsync(arguments, tail, known, cancellationToken);
        }

        // Several UAVs. Backed by the operator's own words, or confirmed by them.
        if (!valid.All(Named) && !OperatorUavContext.RefersToGroup(_operatorText))
        {
            var key = "confirm:" + string.Join(",", valid.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
            var answer = await _scope.GetOrAskAsync(key, () => _promptGate.RequestChoiceAsync(
                _correlationId, _agentName, $"Use UAVs {JoinAnd(valid)} for this?", ["Yes", "No"], cancellationToken));
            if (!string.Equals(answer, "Yes", StringComparison.OrdinalIgnoreCase))
                return $"Not executed: the operator did not confirm using UAVs {JoinAnd(valid)} (or did not respond in time). " +
                       "Ask them which UAVs they mean.";
        }

        _uavContext.Clear();
        _groupMemory.RecordFullGroup(valid);
        return await InvokeAsync(arguments, valid, droppedNote, cancellationToken);
    }

    private async Task<string> AskWhichUavAsync(AIFunctionArguments arguments, string guessed, List<UavSummary> known, CancellationToken cancellationToken)
    {
        // Keyed like TailNumberDisambiguationTool's own ask, so a single-UAV tool guessing the same
        // UAV in the same turn shares one prompt.
        var chosen = await _scope.GetOrAskAsync(guessed, () => _promptGate.RequestChoiceAsync(
            _correlationId, _agentName, "Which UAV do you mean?", known.Select(k => k.TailNumber).ToList(), cancellationToken));
        if (chosen is null)
            return "Not executed: operator did not specify which UAV (or did not respond in time).";

        _uavContext.Set(chosen);
        var note = $"IMPORTANT - this action actually executed against {chosen}, and ONLY {chosen} - your summary must name {chosen} " +
                   "and no other UAV number, including whatever you originally specified in this call's own arguments (that value was " +
                   "never used, it was replaced by the operator's real answer). ";
        return await InvokeAsync(arguments, [chosen], note, cancellationToken);
    }

    private async Task<string> InvokeAsync(AIFunctionArguments arguments, IReadOnlyList<string> tailNumbers, string note, CancellationToken cancellationToken)
    {
        var resolved = new AIFunctionArguments(arguments) { [PropertyName] = tailNumbers.ToArray() };
        return note + (await _inner.InvokeAsync(resolved, cancellationToken))?.ToString();
    }

    /// <summary>The operator's own message names this tail number as a whole word.</summary>
    private bool Named(string tailNumber) => Regex.IsMatch(_operatorText, $@"\b{Regex.Escape(tailNumber)}\b");

    /// <summary>The operator named this UAV and no other known one: it's now the one they're working
    /// with (<see cref="OperatorUavContext"/>).</summary>
    private void RememberIfOnlyUavNamed(string tailNumber, List<UavSummary> known)
    {
        var named = known.Select(k => k.TailNumber).Where(Named).ToList();
        if (named.Count == 1 && string.Equals(named[0], tailNumber, StringComparison.OrdinalIgnoreCase))
            _uavContext.Set(named[0]);
    }

    /// <summary>A JSON array as the schema asks; a bare or comma-separated string is taken too, since
    /// a small model sometimes sends a string where the schema asks for an array.</summary>
    internal static List<string> ReadList(object? raw)
    {
        IEnumerable<string?> items = raw switch
        {
            null => [],
            string s => s.Split(','),
            JsonElement { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().Select(e =>
                e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString()),
            JsonElement { ValueKind: JsonValueKind.String } text => (text.GetString() ?? "").Split(','),
            JsonElement other => [other.ToString()],
            IEnumerable<string> list => list,
            _ => [raw.ToString()]
        };
        return items
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string JoinAnd(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
}
