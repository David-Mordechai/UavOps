using System.Text.RegularExpressions;

namespace UavOps.Agent.Tooling;

/// <summary>
/// Splits one operator turn into its separate asks ("bring them all home" / "give me full summary
/// of today session") so <see cref="Agents.AgentFactory.BuildToolsForTurn"/> can rank each on its
/// own, in addition to the whole turn. Exists because of a real, live-reproduced failure: embedded
/// as one sentence, a compound turn's second ask can dominate the ranking and push the first ask's
/// tool out of top-K entirely - "bring them all home and give me full summary of today session"
/// ranked <c>ReturnToLaunch</c> 14th on its own text and 11th with conversation history (top-K
/// is 10), because "session" pulled toward the simulator's lesson tools. The model, never offered
/// the tool, truthfully answered "No return-to-launch capability available" and brought nothing
/// home. "bring them all home" alone ranks it 1st.
///
/// Deliberately a plain separator split, not a parser or an LLM call: clause results are only ever
/// ADDED to the whole-turn candidates, never substituted for them, so a clumsy split ("at speed 70
/// and altitude 3000" → "altitude 3000") can at worst offer a few extra tools - it can't remove one
/// the whole-turn ranking already found.
/// </summary>
public static partial class RetrievalClauseSplitter
{
    [GeneratedRegex(@"\s*(?:[,;]|\b(?:and then|then|and also|also|and|plus)\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Separator();

    /// <summary>Returns the turn's clauses, or an empty list when there's nothing to split (a
    /// single clause adds nothing the whole-turn ranking doesn't already cover). One-word
    /// fragments are dropped - too little text to rank meaningfully.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var clauses = Separator().Split(text)
            .Select(c => c.Trim())
            .Where(c => c.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            .ToList();

        return clauses.Count > 1 ? clauses : [];
    }
}
