namespace UavOps.Agent.Options;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";

    /// <summary>How many tools (out of BrainAgent's full catalog) get offered to the model each
    /// turn, ranked by <see cref="Tooling.ToolRetrievalIndex"/>. Fixed, not adaptive — see that
    /// class's own doc comment for why.</summary>
    public int TopK { get; set; } = 10;
}
