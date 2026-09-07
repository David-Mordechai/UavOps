namespace UavOps.Agent.Options;

/// <summary>
/// Projects the loaded/validated <c>AgentsConfig/BrainAgent.yaml</c> into the node/edge shape the
/// chat UI's Agent Graph tab's graph library (vis-network) consumes directly. Now a flat star
/// graph — one BrainAgent node plus one node per real tool, one "uses" edge each — since the
/// multi-agent delegation tree this used to project (agent nodes, "delegates" edges,
/// retrieval-vs-explicit-children distinction) was collapsed into a single flat agent. Returns
/// vis-network's native <c>{ nodes, edges }</c> shape directly so the frontend stays a pure
/// renderer with no client-side graph-building logic.
/// </summary>
public static class AgentGraphProjector
{
    private const string RootAgentName = "BrainAgent";

    public static object Build(AgentConfig config)
    {
        var nodes = new List<object>
        {
            new { id = RootAgentName, type = "agent", isRoot = true }
        };
        var edges = new List<object>();

        foreach (var tool in config.Tools)
        {
            var toolId = $"{RootAgentName}::{tool.Operation}";
            nodes.Add(new
            {
                id = toolId,
                type = "tool",
                ownerAgent = RootAgentName,
                operation = tool.Operation,
                kind = tool.Kind,
                description = tool.Description,
                exampleUtterance = tool.ExampleUtterance,
                requiresConfirmation = tool.RequiresConfirmation,
                // Only what the LLM sees — FixedParameters are never shown to it, same rule
                // followed everywhere else this distinction matters (e.g. OperationTool.BuildSchema).
                parameters = tool.Parameters.Keys.ToArray()
            });
            edges.Add(new { from = RootAgentName, to = toolId, kind = "uses" });
        }

        return new { nodes, edges };
    }
}
