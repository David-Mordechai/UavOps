namespace UavOps.Agent.Tooling;

/// <summary>
/// Shares in-flight (or already-answered) "which UAV do you mean?" resolutions, AND one
/// in-flight (or already-executed) "every known UAV" fan-out per tool name, across every
/// <see cref="TailNumberDisambiguationTool"/> built for the same agent invocation (one per turn,
/// created in <c>AgentFactory.BuildAllTools</c>).
///
/// <see cref="GetOrAskAsync"/> is keyed by the model's own raw guessed tailNumber value (or the
/// literal <c>"ALL"</c> sentinel). Two tailNumber-taking calls in the same turn that guessed the
/// *same* value (e.g. <c>SetAltitude</c> right after <c>SetSpeed</c>, both defaulting to the same
/// ungrounded guess because neither actually knows which UAV is meant) really do target one UAV,
/// so the second reuses the first's answer instead of asking again. The first caller for a given
/// key starts (and caches) the actual prompt task; anyone else with the *same* key - whether it
/// arrives after the first has resolved, or concurrently while it's still pending (tool calls in
/// the same turn can run concurrently, see <c>AllowConcurrentInvocation</c>) - just awaits that
/// same task rather than opening a second <c>OperatorPromptGate</c> round-trip.
///
/// This was previously a single, un-keyed slot shared by every ambiguous call in the turn
/// regardless of what it guessed - live-reproduced as a real safety bug: "bring UAV-2 and UAV-3
/// home" (two <c>ReturnToLaunch</c> calls, two genuinely different guessed tail numbers, neither
/// literally present in the operator's own phrasing) asked once, got "UAV-1", and then silently
/// applied that single answer to *both* calls - UAV-2 and UAV-3 never got commanded at all, and the
/// model itself could see something was wrong (identical results for calls it made with different
/// arguments) without knowing why. Keying by the guessed value fixes this: two different guesses
/// are two different UAVs as far as this scope is concerned, so each gets its own independent
/// prompt, while two calls that happened to guess the same value still correctly dedupe.
///
/// <see cref="GetOrFanOutAsync"/>: fixes a live-reproduced bug distinct from the one above - the
/// *shared answer* being correctly deduped doesn't stop the model from issuing the *same* tool call
/// (e.g. <c>Navigate</c>) multiple times in one completion, each with an unresolved tailNumber.
/// Without this, each duplicate call independently resolves via the shared "ALL" answer and
/// independently re-fans-out across the whole fleet - 3 duplicate model calls for one action times
/// a 3-UAV fleet meant 9 real mutations instead of 3 (observed live: every operation in a fleet-wide
/// command executed exactly 3x). Keyed by tool name (not argument content) since a genuine
/// multi-action turn - e.g. <c>Navigate</c> + <c>SetSpeed</c> + <c>SetAltitude</c> - still needs its
/// own independent fan-out per distinct action; only a *repeat* of the same action this turn is the
/// bug this closes.
/// </summary>
public sealed class TailNumberResolutionScope
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<string?>> _askResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string>> _fanOutResults = new(StringComparer.Ordinal);

    public Task<string?> GetOrAskAsync(string guessedTailNumber, Func<Task<string?>> ask)
    {
        lock (_lock)
        {
            if (!_askResults.TryGetValue(guessedTailNumber, out var task))
            {
                task = ask();
                _askResults[guessedTailNumber] = task;
            }
            return task;
        }
    }

    public Task<string> GetOrFanOutAsync(string toolName, Func<Task<string>> fanOut)
    {
        lock (_lock)
        {
            if (!_fanOutResults.TryGetValue(toolName, out var task))
            {
                task = fanOut();
                _fanOutResults[toolName] = task;
            }
            return task;
        }
    }
}
