using System.Text.RegularExpressions;

namespace UavOps.Agent.Tooling;

/// <summary>
/// The one UAV the operator is currently working with, across turns: the last single tail number
/// they named themselves or picked when asked "Which UAV do you mean?". Lets
/// <see cref="TailNumberDisambiguationTool"/> accept a follow-up that names no UAV ("start the
/// mission" right after preparing 997's search) instead of asking again for a UAV the operator
/// already chose. Only ever set from the operator's own words or answers, never from a model
/// guess, and it only grounds a guess of that exact same UAV.
///
/// Not used when the operator's current message refers to a group ("the rest", "all", "them",
/// "both"...): there the last single UAV is exactly the wrong answer - e.g. "bring the rest home"
/// right after "bring 997 home" must never resolve to 997. An action on several UAVs clears it.
/// Session-lifetime, like <see cref="FleetGroupMemory"/>.
/// </summary>
public sealed class OperatorUavContext
{
    private static readonly Regex GroupReference = new(
        @"\b(all|every|each|both|rest|other|others|remaining|them|they|their|theirs|fleet|everyone)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly object _lock = new();
    private string? _current;

    public string? Current
    {
        get { lock (_lock) return _current; }
    }

    public void Set(string tailNumber)
    {
        lock (_lock) _current = tailNumber;
    }

    public void Clear()
    {
        lock (_lock) _current = null;
    }

    /// <summary>Whether <paramref name="guessed"/> is the operator's current UAV and the current
    /// message doesn't refer to a group.</summary>
    public bool Grounds(string guessed, string operatorText) =>
        Current is { } current &&
        string.Equals(current, guessed, StringComparison.OrdinalIgnoreCase) &&
        !GroupReference.IsMatch(operatorText);
}
