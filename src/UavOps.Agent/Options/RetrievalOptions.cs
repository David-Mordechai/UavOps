namespace UavOps.Agent.Options;

public sealed class RetrievalOptions
{
    public const string SectionName = "Retrieval";
    public int MaxDelegatesPerAgent { get; set; } = 5;   // top-K candidates offered per agent per turn
    public int MaxDelegationDepth { get; set; } = 2;      // recursion bound: MainAgent = depth 0
}
