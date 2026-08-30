namespace UavOps.Agent.Options;

public sealed class MemoryOptions
{
    public const string SectionName = "Memory";

    // Bounds BrainAgent's reused conversation history (see AgentFactory's persistent root agent) so a
    // long-running session doesn't grow the prompt sent to the model without limit.
    public int MaxHistoryMessages { get; set; } = 40;
}
