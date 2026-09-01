namespace UavOps.Agent.Options;

/// <summary>
/// Projects the already-loaded/validated <c>AgentsConfig</c> into the node/edge shape the chat
/// UI's Agent Graph tab's graph library (vis-network) consumes directly — kept as a small static class
/// rather than inlined into <c>Program.cs</c> (unlike the flat one-level `/healthz` object) because
/// this has nested agent×tool×parameter loops plus the root-exempt and retrieval-based branches,
/// the same complexity level already extracted into a dedicated static class elsewhere
/// (<see cref="AgentConfigLoader"/>/<see cref="AgentConfigValidator"/>). Returns vis-network's
/// native <c>{ nodes, edges }</c> shape directly so the frontend stays a pure renderer with no
/// client-side graph-building logic — the whole point of this being "dynamically loaded": add a
/// new agent/tool YAML file, restart, and it appears with zero frontend changes.
/// </summary>
public static class AgentGraphProjector
{
    private const string RootAgentName = "BrainAgent";

    public static object Build(Dictionary<string, AgentConfig> agents)
    {
        var nodes = new List<object>();
        var edges = new List<object>();

        foreach (var (agentName, config) in agents)
        {
            nodes.Add(new
            {
                id = agentName,
                type = "agent",
                isRoot = agentName == RootAgentName,
                retrievalBased = config.Children is null,
                description = config.Description,
                exampleUtterance = config.ExampleUtterance
            });

            if (config.Children is not null)
            {
                foreach (var child in config.Children)
                {
                    edges.Add(new { from = agentName, to = child, kind = "delegates" });
                }
            }

            foreach (var tool in config.Tools)
            {
                var toolId = $"{agentName}::{tool.Operation}";
                nodes.Add(new
                {
                    id = toolId,
                    type = "tool",
                    ownerAgent = agentName,
                    operation = tool.Operation,
                    kind = tool.Kind,
                    description = tool.Description,
                    exampleUtterance = tool.ExampleUtterance,
                    requiresConfirmation = tool.RequiresConfirmation,
                    // Only what the LLM sees — FixedParameters are never shown to it, same rule
                    // followed everywhere else this distinction matters (e.g. OperationTool.BuildSchema).
                    parameters = tool.Parameters.Keys.ToArray()
                });
                edges.Add(new { from = agentName, to = toolId, kind = "uses" });
            }
        }

        return new { nodes, edges };
    }
}
