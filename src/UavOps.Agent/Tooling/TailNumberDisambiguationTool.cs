using System.Text.Json;
using Microsoft.Extensions.AI;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Wraps any Moav-operation <see cref="AIFunction"/> (an in-process <c>Tooling.OperationTool</c>
/// historically; a remote Moav-server <c>McpClientTool</c> since the MCP domain split - this class
/// only ever calls <c>_inner.InvokeAsync</c> generically, so which kind never matters here) whose
/// model-supplied "tailNumber" argument might be a guess rather than something the operator
/// actually said. Lives alongside <c>OperationTool</c>/<c>OperationCatalog</c> here in
/// <c>Tooling/</c> rather than a Moav-specific folder - despite depending on
/// <see cref="UavSummary"/> and a fleet-listing delegate - since it's really generic MCP-tool
/// wrapping infrastructure that happens to be shaped for Moav-like tools (decided per-turn from a
/// tool's own JSON schema, not from a hardcoded domain check - see <c>AgentFactory.BuildAllTools</c>),
/// not Moav domain logic itself; the actual Moav domain logic all lives in
/// <c>UavOps.Agent.McpMoav</c> now. Takes a plain <c>listFleet</c> delegate rather than a whole
/// Moav-service interface - this is the only fleet data it ever needs, and since the MCP split,
/// "list the real fleet" means calling the Moav MCP server's own fleet-listing tool (configured via
/// <c>McpServerConfig.FleetListingTool</c>, not a hardcoded name), not an in-process interface
/// method - a delegate lets <see cref="AgentFactory"/> supply whichever is actually live without
/// this class needing to know the difference.
///
/// BrainAgent's own instructions already say not to guess a tail number and to ask the operator
/// instead when more than one UAV exists - but a small local model doesn't reliably follow that
/// (observed directly: it calls ListFleet, sees 3 known tail numbers, and picks one anyway in the
/// large majority of trials). This makes the check deterministic instead of trusting the model's
/// judgment, the same reasoning <c>ChatConfirmationParser</c>/<see cref="OperatorPromptGate"/>
/// already establish for this class of problem: if the tail number the model chose doesn't
/// literally appear anywhere in the operator's own turn text, and the fleet actually has more than
/// one known UAV, block the guess and ask for real via <see cref="OperatorPromptGate"/> instead of
/// executing it.
///
/// A model-supplied tailNumber of the literal value <see cref="AllSentinel"/> (case-insensitive) is
/// a distinct signal from a normal guess: it means the model itself recognized, from the
/// instruction text it was actually given, that the request refers to every UAV collectively - see
/// each tailNumber parameter's own tool description. This is deliberately model-side natural-language
/// judgment on a bounded, already-delegated instruction, not free-text keyword scanning in C# on
/// the operator's raw message (the class of fix this codebase's own operator explicitly rejected).
///
/// That judgment alone is NOT trusted to execute directly, though - measured directly across two
/// different model configurations, it's unreliable in both directions (a weaker model sometimes
/// missed genuine "all" requests; a supposedly stronger one instead started defaulting *every*
/// unspecified single-UAV request to "all", the opposite failure). So a recognized "ALL" always
/// routes through <see cref="ResolveAllUavsRequestAsync"/> first - a deterministic yes/no
/// confirmation of the actual known fleet before anything executes broadly, falling back to a real
/// per-UAV choice if declined. Only once resolved (confirmed "ALL", or a specific chosen UAV) does
/// <see cref="InvokeForAllUavsAsync"/> actually invoke the inner tool - once per real tail number,
/// aggregating each result (trivially collapsing to that one UAV, no fan-out, when only one is
/// known). If the model doesn't recognize "all" at all and instead emits an ungrounded guess, the
/// separate ask-path below fires the same way, also offering <see cref="AllSentinel"/> as a choice.
///
/// Both ALL-fan-out call sites route through <see cref="TailNumberResolutionScope.GetOrFanOutAsync"/>,
/// not directly to <see cref="InvokeForAllUavsAsync"/> - see that method's own doc comment for the
/// live-reproduced duplicate-call bug this closes (the model issuing the same tool multiple times
/// in one completion, each independently re-fanning-out across the whole fleet).
/// </summary>
public sealed class TailNumberDisambiguationTool : AIFunction
{
    private const string AllSentinel = "ALL";

    private readonly AIFunction _inner;
    private readonly Func<CancellationToken, Task<OperationResult>> _listFleet;
    private readonly OperatorPromptGate _promptGate;
    private readonly TailNumberResolutionScope _scope;
    private readonly string _agentName;
    private readonly string _correlationId;
    private readonly string _operatorText;

    public TailNumberDisambiguationTool(AIFunction inner, Func<CancellationToken, Task<OperationResult>> listFleet, OperatorPromptGate promptGate,
        TailNumberResolutionScope scope, string agentName, string correlationId, string operatorText)
    {
        _inner = inner;
        _listFleet = listFleet;
        _promptGate = promptGate;
        _scope = scope;
        _agentName = agentName;
        _correlationId = correlationId;
        _operatorText = operatorText;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override JsonElement JsonSchema => _inner.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (!arguments.TryGetValue("tailNumber", out var raw))
        {
            return await _inner.InvokeAsync(arguments, cancellationToken);
        }

        var guessed = ExtractString(raw);

        // Checked first, ahead of the trust-check below: this is the model's own explicit,
        // structured decision that every UAV is meant - but that judgment has proven unreliable
        // in practice (observed directly: a model upgrade made it consistently default an
        // unspecified single-UAV request to "every UAV" instead of asking, the opposite of the
        // improvement intended). A broad, multi-UAV action never executes on that judgment alone -
        // see ResolveAllUavsRequestAsync's own doc comment for the deterministic confirm-or-ask
        // safety net this routes through instead.
        if (string.Equals(guessed, AllSentinel, StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await _scope.GetOrAskAsync(() => ResolveAllUavsRequestAsync(cancellationToken));

            if (resolved is null)
            {
                return "Not executed: operator did not confirm applying this to every UAV, or did not specify which one (or did not respond in time).";
            }

            if (string.Equals(resolved, AllSentinel, StringComparison.OrdinalIgnoreCase))
            {
                return await _scope.GetOrFanOutAsync(Name, () => InvokeForAllUavsAsync(arguments, cancellationToken, knownTails: null));
            }

            var confirmedSingle = new AIFunctionArguments(arguments) { ["tailNumber"] = resolved };
            var confirmedSingleResult = await _inner.InvokeAsync(confirmedSingle, cancellationToken);
            return BuildConfirmedTargetNote(resolved) + confirmedSingleResult;
        }

        // Trust it without asking whenever the operator's own turn text already named this tail
        // number - same substring-match reasoning AskOperatorChoiceTool.TryAutoResolve already
        // uses to skip a redundant prompt when the answer was already given.
        if (string.IsNullOrEmpty(guessed) || _operatorText.Contains(guessed, StringComparison.OrdinalIgnoreCase))
        {
            return await _inner.InvokeAsync(arguments, cancellationToken);
        }

        var fleet = await _listFleet(cancellationToken);
        if (fleet.Success && fleet.Value is List<UavSummary> { Count: > 1 } tails)
        {
            // Deduplicated via _scope: only the first tailNumber-taking call in this turn actually
            // opens an OperatorPromptGate round-trip - any sibling call (SetAltitude right after
            // SetSpeed, say) awaits that same in-flight/already-resolved answer instead of asking
            // again, since one delegate call always targets exactly one UAV (or, now, all of them).
            var choices = tails.Select(t => t.TailNumber).Append(AllSentinel).ToList();
            var chosen = await _scope.GetOrAskAsync(() => _promptGate.RequestChoiceAsync(
                _correlationId,
                _agentName,
                "Which UAV do you mean?",
                choices,
                cancellationToken));

            if (chosen is null)
            {
                return "Not executed: operator did not specify which UAV (or did not respond in time).";
            }

            if (string.Equals(chosen, AllSentinel, StringComparison.OrdinalIgnoreCase))
            {
                return await _scope.GetOrFanOutAsync(Name, () => InvokeForAllUavsAsync(arguments, cancellationToken, knownTails: tails));
            }

            var resolved = new AIFunctionArguments(arguments) { ["tailNumber"] = chosen };
            var result = await _inner.InvokeAsync(resolved, cancellationToken);

            // The model's own record of this call still shows whatever tail number it originally
            // guessed (arguments, above) - the substitution to the operator's real answer happened
            // entirely behind it. Without this, its own later summary sentence has no way to know
            // the corrected tail number and just repeats its stale original guess instead - a real
            // production case: it silently substituted UAV-1 -> UAV-2 (correctly, per the operator's
            // answer) yet still told the operator "UAV-1" was updated. Stating the confirmed target
            // explicitly in the result text - not asking the model to infer or remember it - is what
            // makes the summary reliably correct.
            return BuildConfirmedTargetNote(chosen) + result;
        }

        // Fleet lookup failed, or there's only one (or zero) known UAV - nothing to disambiguate,
        // so fall through to the model's own value rather than block on a guard that can't help.
        return await _inner.InvokeAsync(arguments, cancellationToken);
    }

    /// <summary>Resolves a model-emitted <see cref="AllSentinel"/> to the real fleet. Fleet count
    /// 0/lookup failure: nothing to resolve. Otherwise, every known UAV is the target - no
    /// operator confirmation prompt before fanning out.
    ///
    /// TEMPORARY, FOR TESTING: this used to ask a real yes/no "Apply this to all N known UAVs?"
    /// confirmation before committing to every UAV (the same fixed-vocabulary pattern
    /// <see cref="OperatorPromptGate"/>/<c>ConfirmationGate</c> use for consequential actions,
    /// applied to the *scope* of the action rather than one specific operation) - removed at the
    /// operator's explicit request to match the flat single-agent lab's own behavior
    /// (<c>eval/tool-retrieval-lab/</c>, which never gated a fleet-wide fan-out) while testing this
    /// architecture. Revisit before any real production use against a live fleet.</summary>
    private async Task<string?> ResolveAllUavsRequestAsync(CancellationToken cancellationToken)
    {
        var fleet = await _listFleet(cancellationToken);
        if (!fleet.Success || fleet.Value is not List<UavSummary> tails || tails.Count == 0)
        {
            return null;
        }

        return AllSentinel;
    }

    /// <summary>Resolves the "every UAV" case - reached either because the model itself emitted
    /// <see cref="AllSentinel"/> directly (<paramref name="knownTails"/> is null, so the fleet is
    /// looked up here), or because the operator selected "ALL" from a disambiguation prompt that
    /// already looked it up (<paramref name="knownTails"/> passed through, no second lookup).
    /// Fleet count 0 or lookup failure: nothing to act on. Fleet count 1: that one UAV *is* "all of
    /// them" - resolve directly to its real tail number, never send the literal string "ALL"
    /// through to the operation itself. Otherwise, invokes the inner tool once per known UAV,
    /// sequentially - not concurrently, so a requiresConfirmation operation (e.g. ReturnToLaunch)
    /// asks once per UAV in a predictable order through ConfirmationGate's single turnstile, rather
    /// than racing several confirmation prompts at once - aggregating each result so the calling
    /// agent's own summary has explicit per-UAV ground truth, same reasoning as the single-UAV
    /// ground-truth note <see cref="BuildConfirmedTargetNote"/> builds for the single-UAV case.</summary>
    private async Task<string> InvokeForAllUavsAsync(AIFunctionArguments arguments, CancellationToken cancellationToken, List<UavSummary>? knownTails)
    {
        List<UavSummary>? tails = knownTails;
        if (tails is null)
        {
            var fleet = await _listFleet(cancellationToken);
            tails = fleet.Success && fleet.Value is List<UavSummary> t ? t : null;
        }

        if (tails is null || tails.Count == 0)
        {
            return "Not executed: could not resolve the known fleet to apply this to every UAV.";
        }

        if (tails.Count == 1)
        {
            var single = new AIFunctionArguments(arguments) { ["tailNumber"] = tails[0].TailNumber };
            return (await _inner.InvokeAsync(single, cancellationToken))?.ToString() ?? "";
        }

        var results = new List<string>();
        foreach (var tail in tails)
        {
            var perUav = new AIFunctionArguments(arguments) { ["tailNumber"] = tail.TailNumber };
            var result = await _inner.InvokeAsync(perUav, cancellationToken);
            results.Add($"{tail.TailNumber}: {result}");
        }

        return $"IMPORTANT - this action actually executed against all {tails.Count} known UAVs listed below, and ONLY those - " +
               "your summary must name exactly these, not any other UAV number (including whatever single UAV, if any, you originally " +
               "specified in this call's own arguments - that value was never used). " + string.Join("; ", results);
    }

    /// <summary>Builds the ground-truth note prefixed to a single-UAV result once disambiguation
    /// substitutes a real value for whatever the model originally guessed - deliberately forceful
    /// and explicit (not just "(Confirmed target: X)") because a softer note still let some models
    /// hedge and mention their own original argument alongside the real one in their summary.</summary>
    private static string BuildConfirmedTargetNote(string resolvedTailNumber) =>
        $"IMPORTANT - this action actually executed against {resolvedTailNumber}, and ONLY {resolvedTailNumber} - " +
        $"your summary must name {resolvedTailNumber} and no other UAV number, including whatever you originally specified " +
        "in this call's own arguments (that value was never used, it was replaced by the operator's real answer). ";

    private static string ExtractString(object? raw) => raw switch
    {
        null => "",
        string s => s,
        JsonElement { ValueKind: JsonValueKind.String } je => je.GetString() ?? "",
        _ => raw.ToString() ?? ""
    };
}
