using System.Text.Json;
using System.Text.RegularExpressions;
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
///
/// <see cref="_groupMemory"/> closes a THIRD, distinct real bug from the two above (structural
/// multi-guess trust, and ALL/subset resolution): a small local model reliably calling this tool
/// for N-1 of N previously-addressed UAVs and simply never generating the Nth call - honestly
/// reporting the resulting partial state afterward, never fabricating, but incomplete all the same.
/// Live-reproduced repeatedly (see McpMoav/ToolsConfig.yaml's own "Tail numbers" section for the
/// prompt-wording attempts this survived) with no wording that reliably closed it - this is a
/// deterministic completion instead: every time a genuine multi-target group resolves (fan-out,
/// explicit subset, or several distinct real guesses in one turn), <see cref="FleetGroupMemory"/>
/// remembers it, so a LATER action naming only part of that same group - the same turn or a
/// separate one - gets the missing member(s) invoked too, with a clear note so the model's own
/// summary doesn't quietly omit them. Two independent signals gate this, both domain-agnostic (no
/// tool name, parameter name, or domain vocabulary - <see cref="PluralPronounPattern"/> is plain
/// English grammar, applicable to any domain sharing this same tailNumber-shaped convention): (1)
/// two or more distinct real guesses for the same tool this turn - already unambiguous multi-target
/// evidence on its own, per <see cref="TailNumberResolutionScope.RegisterRealGuessAndGetSiblingsAsync"/>
/// - or (2) exactly one real guess plus a bare plural pronoun in the operator's OWN current-turn
/// text, when that guess is part of a remembered group with more members than were named. Neither
/// signal fires the ask flow's genuine single-UAV protection: a lone guess with no plural pronoun,
/// or with one but no matching remembered group, still falls through to asking, exactly as before.
/// </summary>
public sealed class TailNumberDisambiguationTool : AIFunction
{
    private const string AllSentinel = "ALL";

    private static readonly Regex PluralPronounPattern = new(@"\b(them|their|theirs|they)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AIFunction _inner;
    private readonly Func<CancellationToken, Task<OperationResult>> _listFleet;
    private readonly OperatorPromptGate _promptGate;
    private readonly TailNumberResolutionScope _scope;
    private readonly FleetGroupMemory _groupMemory;
    private readonly string _agentName;
    private readonly string _correlationId;
    private readonly string _operatorText;

    public TailNumberDisambiguationTool(AIFunction inner, Func<CancellationToken, Task<OperationResult>> listFleet, OperatorPromptGate promptGate,
        TailNumberResolutionScope scope, FleetGroupMemory groupMemory, string agentName, string correlationId, string operatorText)
    {
        _inner = inner;
        _listFleet = listFleet;
        _promptGate = promptGate;
        _scope = scope;
        _groupMemory = groupMemory;
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
            var resolved = await _scope.GetOrAskAsync(AllSentinel, () => ResolveAllUavsRequestAsync(cancellationToken));

            if (resolved is null)
            {
                return "Not executed: operator did not confirm applying this to every UAV, or did not specify which one (or did not respond in time).";
            }

            if (string.Equals(resolved, AllSentinel, StringComparison.OrdinalIgnoreCase))
            {
                return await InvokeForAllUavsAndRecordGroupAsync(arguments, cancellationToken, knownTails: null);
            }

            var confirmedSingle = new AIFunctionArguments(arguments) { ["tailNumber"] = resolved };
            var confirmedSingleResult = await _inner.InvokeAsync(confirmedSingle, cancellationToken);
            return BuildConfirmedTargetNote(resolved) + confirmedSingleResult;
        }

        // A genuinely different, specific SUBSET of the fleet - not "ALL", not one UAV - e.g. "the
        // rest", "the other two", "998 and 999" once 997 was already handled separately. This
        // is expressed as a single comma-separated list of real tail numbers computed by the model
        // itself (which has the full conversation history to work out which ones those are), rather
        // than one ambiguous single-UAV guess per tool call. Live-reproduced why this matters: asked
        // to "bring the rest UAVs home" with two UAVs remaining, the model correctly resolved and
        // executed against the first only, and simply never attempted the second, in 6 of 8 trials -
        // it was reliable at completing ONE ambiguous call but not at reliably issuing a SECOND one
        // to finish a multi-target request. A single call naming the whole subset removes that
        // reliance on the model correctly counting and looping back. Unlike InvokeForAllUavsAsync,
        // this never touches a UAV outside the exact set named - re-applying some operations (e.g.
        // UploadWaypoints, SetTrackingMode) to a UAV the operator meant to exclude would be a real,
        // unwanted behavior change, not a harmless no-op the way redundantly re-confirming an
        // already-returning UAV's ReturnToLaunch is.
        if (guessed.Contains(','))
        {
            var candidates = guessed.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var subsetFleetLookup = await _listFleet(cancellationToken);
            if (subsetFleetLookup.Success && subsetFleetLookup.Value is List<UavSummary> subsetTails)
            {
                var validTails = new List<string>();
                foreach (var candidate in candidates)
                {
                    var match = subsetTails.FirstOrDefault(t => string.Equals(t.TailNumber, candidate, StringComparison.OrdinalIgnoreCase));
                    if (match is not null && !validTails.Contains(match.TailNumber, StringComparer.OrdinalIgnoreCase))
                    {
                        validTails.Add(match.TailNumber);
                    }
                }

                if (validTails.Count > 0)
                {
                    var subsetKey = string.Join(",", validTails.OrderBy(t => t, StringComparer.OrdinalIgnoreCase));
                    var subsetResult = await _scope.GetOrFanOutAsync($"{Name}:{subsetKey}", () => InvokeForExplicitSubsetAsync(arguments, cancellationToken, validTails));
                    _groupMemory.RecordFullGroup(validTails);
                    return subsetResult;
                }
            }
            // No known tail number could be matched out of the list - fall through to the normal
            // single-value handling below, the same safe failure mode as any other unrecognized guess.
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
            // Structural multi-target detection plus cross-turn group completion, ahead of the ask
            // flow below - see this class's own doc comment for both signals. Only reachable when
            // the guess is itself a REAL, fleet-verified tail number (never for a hallucinated one,
            // which still must go through the ask flow below).
            if (tails.Any(t => string.Equals(t.TailNumber, guessed, StringComparison.OrdinalIgnoreCase)))
            {
                var siblingGuesses = await _scope.RegisterRealGuessAndGetSiblingsAsync(Name, guessed, cancellationToken);
                var lastGroup = _groupMemory.GetLastFullGroup();
                var missingFromGroup = lastGroup is { Count: > 1 } && lastGroup.IsSupersetOf(siblingGuesses)
                    ? lastGroup.Where(t => !siblingGuesses.Contains(t)).ToList()
                    : [];

                // Signal 1: two or more distinct real guesses for this tool this turn is already
                // unambiguous multi-target proof on its own, regardless of wording or group memory.
                // Signal 2: exactly one real guess, but the operator's OWN current-turn text uses a
                // bare plural pronoun AND that guess is part of a remembered group with more members
                // than were named - a lone guess with neither signal still falls through to asking,
                // preserving the original genuine single-UAV protection unchanged.
                var structuralMultiGuess = siblingGuesses.Count >= 2;
                var groupCompletionSignal = siblingGuesses.Count == 1 && missingFromGroup.Count > 0 && PluralPronounPattern.IsMatch(_operatorText);

                if (structuralMultiGuess || groupCompletionSignal)
                {
                    if (missingFromGroup.Count > 0)
                    {
                        var completionNote = await _scope.GetOrFanOutAsync($"{Name}:autocomplete",
                            () => CompleteMissingMembersAsync(arguments, missingFromGroup, cancellationToken));
                        _groupMemory.RecordFullGroup(siblingGuesses.Concat(missingFromGroup).ToList());
                        return completionNote + await _inner.InvokeAsync(arguments, cancellationToken);
                    }

                    _groupMemory.RecordFullGroup(siblingGuesses);
                    return await _inner.InvokeAsync(arguments, cancellationToken);
                }
            }

            // Deduplicated via _scope, keyed by the model's own guessed value: only the first
            // tailNumber-taking call in this turn that guessed THIS SAME value actually opens an
            // OperatorPromptGate round-trip - a sibling call that guessed the same thing (SetAltitude
            // right after SetSpeed, both defaulting to the same ungrounded guess) awaits that same
            // in-flight/already-resolved answer instead of asking again. A sibling call that guessed
            // a DIFFERENT value (e.g. a separate ReturnToLaunch call the model intended for a
            // different UAV) gets its own independent prompt instead of silently inheriting this
            // one's answer - see TailNumberResolutionScope's own doc comment for the live bug this
            // fixes.
            var choices = tails.Select(t => t.TailNumber).Append(AllSentinel).ToList();
            var chosen = await _scope.GetOrAskAsync(guessed, () => _promptGate.RequestChoiceAsync(
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
                return await InvokeForAllUavsAndRecordGroupAsync(arguments, cancellationToken, knownTails: tails);
            }

            var resolved = new AIFunctionArguments(arguments) { ["tailNumber"] = chosen };
            var result = await _inner.InvokeAsync(resolved, cancellationToken);

            // The model's own record of this call still shows whatever tail number it originally
            // guessed (arguments, above) - the substitution to the operator's real answer happened
            // entirely behind it. Without this, its own later summary sentence has no way to know
            // the corrected tail number and just repeats its stale original guess instead - a real
            // production case: it silently substituted 997 -> 998 (correctly, per the operator's
            // answer) yet still told the operator "997" was updated. Stating the confirmed target
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

    /// <summary>Thin wrapper around <see cref="InvokeForAllUavsAsync"/> (still deduplicated via
    /// <see cref="TailNumberResolutionScope.GetOrFanOutAsync"/>, unchanged) that also records the
    /// resolved fleet in <see cref="_groupMemory"/>, so a LATER partial reference to "them" can
    /// complete against it - see this class's own doc comment.</summary>
    private async Task<string> InvokeForAllUavsAndRecordGroupAsync(AIFunctionArguments arguments, CancellationToken cancellationToken, List<UavSummary>? knownTails)
    {
        var result = await _scope.GetOrFanOutAsync(Name, () => InvokeForAllUavsAsync(arguments, cancellationToken, knownTails));

        List<UavSummary>? fleetForMemory = knownTails;
        if (fleetForMemory is null)
        {
            var fleet = await _listFleet(cancellationToken);
            fleetForMemory = fleet.Success && fleet.Value is List<UavSummary> t ? t : null;
        }

        if (fleetForMemory is not null)
        {
            _groupMemory.RecordFullGroup(fleetForMemory.Select(u => u.TailNumber).ToList());
        }
        return result;
    }

    /// <summary>Invokes this tool for every tail number in <paramref name="missingTailNumbers"/> -
    /// the gap between what the model actually called this turn and <see cref="FleetGroupMemory"/>'s
    /// remembered full group - and returns a ground-truth note the same forceful shape as
    /// <see cref="BuildConfirmedTargetNote"/>/<see cref="InvokeForAllUavsAsync"/> use, so the model's
    /// own summary includes UAVs it never explicitly called this turn but that genuinely were acted
    /// on for real.</summary>
    private async Task<string> CompleteMissingMembersAsync(AIFunctionArguments arguments, IReadOnlyList<string> missingTailNumbers, CancellationToken cancellationToken)
    {
        var results = new List<string>();
        foreach (var tail in missingTailNumbers)
        {
            var perUav = new AIFunctionArguments(arguments) { ["tailNumber"] = tail };
            var result = await _inner.InvokeAsync(perUav, cancellationToken);
            results.Add($"{tail}: {result}");
        }

        return "IMPORTANT - you only explicitly named some of the UAVs from the same group you already addressed together earlier, " +
               $"but this action was ALSO applied for real to the remaining {missingTailNumbers.Count} UAV(s) from that same group, listed " +
               "below - your summary must include these too, not just the one(s) you explicitly named. " + string.Join("; ", results) + " ";
    }

    /// <summary>Resolves a model-computed, explicit comma-separated SUBSET of the fleet (e.g. "the
    /// rest", "the other two") to real per-UAV invocations - see the call site's own comment for why
    /// this exists and how it differs from <see cref="InvokeForAllUavsAsync"/>. Sequential, not
    /// concurrent, for the same reason InvokeForAllUavsAsync is: a requiresConfirmation operation
    /// needs to ask once per UAV in a predictable order through ConfirmationGate's single
    /// turnstile.</summary>
    private async Task<string> InvokeForExplicitSubsetAsync(AIFunctionArguments arguments, CancellationToken cancellationToken, IReadOnlyList<string> tailNumbers)
    {
        var results = new List<string>();
        foreach (var tail in tailNumbers)
        {
            var perUav = new AIFunctionArguments(arguments) { ["tailNumber"] = tail };
            var result = await _inner.InvokeAsync(perUav, cancellationToken);
            results.Add($"{tail}: {result}");
        }

        return $"IMPORTANT - this action actually executed against exactly these {tailNumbers.Count} UAVs listed below, and ONLY those - " +
               "your summary must name exactly these, not any other UAV number. " + string.Join("; ", results);
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
