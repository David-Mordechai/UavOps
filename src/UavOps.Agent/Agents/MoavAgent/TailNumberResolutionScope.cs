namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Shares one in-flight (or already-answered) "which UAV do you mean?" resolution, AND one
/// in-flight (or already-executed) "every known UAV" fan-out per tool name, across every
/// <see cref="TailNumberDisambiguationTool"/> built for the same agent invocation (one per turn,
/// created in <c>AgentFactory.BuildAgentTools</c>).
///
/// <see cref="GetOrAskAsync"/>: a delegate call always targets exactly one UAV, so if a sibling
/// tailNumber-taking tool call in the same turn (e.g. <c>SetAltitude</c> right after
/// <c>SetSpeed</c>) already asked and got an answer, every other ambiguous call in that same turn
/// reuses it instead of asking again. The first caller starts (and caches) the actual prompt task;
/// anyone else - whether it arrives after the first has resolved, or concurrently while it's still
/// pending (tool calls in the same turn can run concurrently, see <c>AllowConcurrentInvocation</c>)
/// - just awaits that same task rather than opening a second <c>OperatorPromptGate</c> round-trip.
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
    private Task<string?>? _pending;
    private readonly Dictionary<string, Task<string>> _fanOutResults = new(StringComparer.Ordinal);

    public Task<string?> GetOrAskAsync(Func<Task<string?>> ask)
    {
        lock (_lock)
        {
            return _pending ??= ask();
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
