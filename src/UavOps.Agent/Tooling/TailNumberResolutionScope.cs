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
/// regardless of what it guessed - live-reproduced as a real safety bug: "bring 998 and 999
/// home" (two <c>ReturnToLaunch</c> calls, two genuinely different guessed tail numbers, neither
/// literally present in the operator's own phrasing) asked once, got "997", and then silently
/// applied that single answer to *both* calls - 998 and 999 never got commanded at all, and the
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
///
/// <see cref="RegisterRealGuessAndGetSiblingsAsync"/>: closes a real, live-reproduced gap distinct
/// from both above - a model asked to act on a whole fleet it already addressed earlier (same turn
/// or an entirely separate later turn) sometimes doesn't recognize this as a single "every UAV"
/// action and doesn't emit the <c>"ALL"</c> sentinel (see <see cref="TailNumberDisambiguationTool"/>'s
/// own doc comment) or a comma-separated subset at all - instead it issues several SEPARATE calls to
/// the same tool, each with a genuinely different, genuinely real, individually-guessed tail number.
/// No prompt wording reliably controls which of these equivalent-in-substance shapes a small local
/// model picks turn to turn - live-reproduced repeatedly, several different wordings each measurably
/// helped one scenario while regressing another, never reaching full reliability on either. Rather
/// than keep tuning wording, this recognizes the shape directly: several distinct, each individually
/// real (fleet-verified) tailNumber guesses for the *same* tool in the *same* turn is unambiguous
/// structural proof of multi-target intent regardless of phrasing, so those calls are trusted and
/// executed directly with no <see cref="OperatorPromptGate"/> round trip at all (which would
/// otherwise time out unanswered, exactly the failure this closes). A single real-but-ungrounded
/// guess for a tool - the case this must NOT change - still falls through to the existing ask flow;
/// this only fires once a *second*, *different*, *also real* guess for the same tool actually
/// arrives. Concurrent tool calls from one completion (<c>AllowConcurrentInvocation</c>) all start
/// within microseconds of each other on the thread pool, so every caller registers its own guess
/// then waits <see cref="SiblingCoordinationWindow"/> - long enough for every true sibling from the
/// same completion to have registered, vanishingly short next to the 120s <c>OperatorPromptGate</c>
/// timeout this replaces - before deciding; whichever guess arrives last still sees every earlier
/// one already registered, so decisions agree regardless of arrival order.
/// </summary>
public sealed class TailNumberResolutionScope
{
    /// <summary>How long a real-but-ungrounded guess waits for a sibling call (same tool, same
    /// turn, different real tail number) to register before falling back to the ask flow. Real
    /// sibling registrations land within microseconds of each other (same completion, same
    /// <c>Task.WhenAll</c>-style dispatch) - this is generous headroom against thread-pool
    /// scheduling jitter, not a real wait for slow work.</summary>
    private static readonly TimeSpan SiblingCoordinationWindow = TimeSpan.FromMilliseconds(300);

    private readonly object _lock = new();
    private readonly Dictionary<string, Task<string?>> _askResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<string>> _fanOutResults = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _realGuessesByTool = new(StringComparer.Ordinal);

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

    /// <summary>Registers a real (fleet-verified), but not text-grounded, tailNumber guess for
    /// <paramref name="toolName"/> this turn, then returns every distinct real guess for the same
    /// tool seen so far (including this one) once the coordination window has passed - two or more
    /// distinct entries is unambiguous proof of multi-target intent worth trusting without asking
    /// (see this class's own doc comment), and the full set also lets a caller compare against
    /// <see cref="FleetGroupMemory"/> to detect a partially-completed repeat of an earlier group.</summary>
    public async Task<IReadOnlySet<string>> RegisterRealGuessAndGetSiblingsAsync(string toolName, string guessedTailNumber, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_realGuessesByTool.TryGetValue(toolName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _realGuessesByTool[toolName] = set;
            }
            set.Add(guessedTailNumber);
        }

        await Task.Delay(SiblingCoordinationWindow, cancellationToken);

        lock (_lock)
        {
            return new HashSet<string>(_realGuessesByTool[toolName], StringComparer.OrdinalIgnoreCase);
        }
    }
}
