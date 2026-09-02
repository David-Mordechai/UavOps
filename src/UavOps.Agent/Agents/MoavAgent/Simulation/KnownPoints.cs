using System.Text.RegularExpressions;

namespace UavOps.Agent.Agents.MoavAgent.Simulation;

/// <summary>
/// Stand-in for a real points registry. Maps a human-friendly location name to coordinates
/// so navigation and payload-pointing operations have something to resolve.
/// </summary>
public static class KnownPoints
{
    private static readonly Dictionary<string, (double Lat, double Lng)> Points = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = (31.801447, 34.643497),
        ["alpha"] = (31.812000, 34.660000),
        ["bravo"] = (31.790000, 34.630000),
    };

    /// <summary>Strips a leading "target " (case-insensitive) - a fixed, known filler word an
    /// operator commonly says before a point's real name (e.g. "target alpha"), never part of the
    /// name itself - so every caller works with and reports the same canonical bare name regardless
    /// of which shape the operator/model used. This is deterministic string handling on this
    /// module's own small, fixed reference vocabulary, not an attempt to interpret free-form
    /// operator intent (observed directly: a model asked to summarize just repeats back whichever
    /// shape it happened to use in its own tool call, "target alpha" or "alpha", rather than
    /// normalizing - so the canonical form has to be established before that call, not left to the
    /// model to get consistent).</summary>
    public static string Canonicalize(string name)
    {
        var trimmed = name.Trim();
        return trimmed.StartsWith("target ", StringComparison.OrdinalIgnoreCase)
            ? trimmed["target ".Length..].Trim()
            : trimmed;
    }

    public static bool TryResolve(string name, out double lat, out double lng)
    {
        if (Points.TryGetValue(Canonicalize(name), out var p))
        {
            (lat, lng) = p;
            return true;
        }

        lat = 0;
        lng = 0;
        return false;
    }

    // Rewriting a tool call's own "location" argument (see LocationCanonicalizationTool) isn't
    // enough on its own: a model still tends to write its own free-text summary from what it
    // recalls the operator (or an ancestor delegate's own instruction text) saying, not from a tool
    // result - observed directly, even after a leaf-level grounding note corrected one hop's own
    // summary, the very next hop up (which still had the operator's raw "target alpha" wording in
    // its own context) reverted right back to it. A single, deterministic pass over any free text
    // this module's known point names might appear in - applied at the true final choke point,
    // right before text reaches the operator - is the one guarantee that survives however many
    // agent hops reached back to the raw wording in between.
    private static readonly Regex TargetPrefixInTextPattern = new(
        $@"\btarget\s+({string.Join("|", Points.Keys.Select(Regex.Escape))})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CanonicalizeText(string text) => TargetPrefixInTextPattern.Replace(text, m => m.Groups[1].Value);
}
