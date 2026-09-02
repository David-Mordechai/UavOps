namespace UavOps.Agent.Agents.MoavAgent;

/// <summary>
/// Ambient (AsyncLocal) holder for "the instruction text the current agent was actually
/// delegated" — set by <see cref="DelegateAgentTool"/> right before it runs the child agent, read
/// by <see cref="TailNumberDisambiguationTool"/> so it checks the real text a delegate received
/// instead of the root operator message it never actually sees (per this codebase's own
/// architecture note: "each delegate only sees the instruction text its parent gives it, not the
/// rest of the conversation"). <see cref="AsyncLocal{T}"/> correctly isolates concurrent delegate
/// calls (e.g. three concurrent fan-out calls to FlightControlAgent, one per UAV, each see only
/// their own instruction), which a plain shared field would not under
/// <c>AllowConcurrentInvocation</c>.
/// </summary>
public static class DelegatedInstructionContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public static string? Current => _current.Value;

    public static IDisposable Push(string instruction)
    {
        var previous = _current.Value;
        _current.Value = instruction;
        return new Popper(previous);
    }

    private sealed class Popper(string? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
