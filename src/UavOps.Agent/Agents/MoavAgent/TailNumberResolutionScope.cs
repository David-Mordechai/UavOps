namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Shares one in-flight (or already-answered) "which UAV do you mean?" resolution across every
/// <see cref="TailNumberDisambiguationTool"/> built for the same agent invocation (one per turn,
/// created in <c>AgentFactory.BuildAgentTools</c>) - a delegate call always targets exactly one
/// UAV (see <c>MoavAgent.yaml</c>'s instructions), so if a sibling tailNumber-taking tool call in
/// the same turn (e.g. <c>SetAltitude</c> right after <c>SetSpeed</c>) already asked and got an
/// answer, every other ambiguous call in that same turn reuses it instead of asking again.
///
/// The first caller starts (and caches) the actual prompt task; anyone else - whether it arrives
/// after the first has resolved, or concurrently while it's still pending (tool calls in the same
/// turn can run concurrently, see <c>AllowConcurrentInvocation</c>) - just awaits that same task
/// rather than opening a second <c>OperatorPromptGate</c> round-trip.
/// </summary>
public sealed class TailNumberResolutionScope
{
    private readonly object _lock = new();
    private Task<string?>? _pending;

    public Task<string?> GetOrAskAsync(Func<Task<string?>> ask)
    {
        lock (_lock)
        {
            return _pending ??= ask();
        }
    }
}
