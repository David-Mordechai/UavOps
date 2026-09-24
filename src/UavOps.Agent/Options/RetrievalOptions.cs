namespace UavOps.Agent.Options;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>How many tools (out of BrainAgent's full catalog) get offered to the model each
    /// turn, ranked by <see cref="Tooling.ToolRetrievalIndex"/>. Fixed, not adaptive — see that
    /// class's own doc comment for why.</summary>
    public int TopK { get; set; } = 10;

    /// <summary>How far below the best score across the FULL tool catalog (every connected server,
    /// regardless of enabled state) a candidate's own score may fall before
    /// <see cref="Tooling.ToolRetrievalIndex.RankCandidates"/> refuses to offer it, even into an
    /// otherwise-empty top-K slot. Exists specifically for the case where the true best match is
    /// hidden by a disabled MCP server (see that method's own doc comment for the live-reproduced
    /// incident) — when nothing enabled comes reasonably close to what the operator's own words
    /// best match overall, offering the next-best-of-a-bad-lot substitute is worse than offering
    /// nothing, since it can cause a real, unrequested action. Has essentially no effect on a normal
    /// turn where the true best match is actually enabled, since that candidate's own score already
    /// equals (or is very close to) the full-catalog best. Empirically derived against real score
    /// distributions via <c>eval/tool-retrieval-lab</c> (see that project's own `Program.cs` comment
    /// for the measured numbers behind this default) — not a guessed constant.</summary>
    public float MaxScoreGapFromBest { get; set; } = 0.25f;

    /// <summary>How many tools each separate clause of a compound turn adds on top of the
    /// whole-turn candidates (see <see cref="Tooling.RetrievalClauseSplitter"/>). Smaller than
    /// <see cref="TopK"/> on purpose: a clause is one ask, so its real tool ranks at or near the
    /// top - measured against the real tool catalog, 5 kept every required tool across the tested
    /// compound phrasings while holding a compound turn to 10-13 tools offered, vs up to 17 at 10
    /// per clause.</summary>
    public int ClauseTopK { get; set; } = 5;
}
