using Microsoft.Extensions.AI;
using UavOps.Agent.Agents;

namespace UavOps.Agent.Options;

/// <summary>
/// Projects the connected MCP servers and their real, discovered tools into the node/edge shape
/// the chat UI's Agent Graph tab's graph library (vis-network) consumes directly. A real 3-tier
/// star graph — BrainAgent → each connected MCP server → each of that server's own tools — sourced
/// from <see cref="AgentFactory.McpServerToolGroups"/>, not from YAML: every tool's name/description
/// is the domain's own <c>[Description]</c>-authored metadata, discovered live at startup, since
/// nothing about a tool is configured in the host anymore. Returns vis-network's native
/// <c>{ nodes, edges }</c> shape directly so the frontend stays a pure renderer with no client-side
/// graph-building logic.
/// </summary>
public static class AgentGraphProjector
{
    private const string RootAgentName = AgentFactory.RootAgentName;

    /// <summary><paramref name="enabledServerNames"/> never changes which servers/tools are
    /// included here — a disabled server is still connected (see <see cref="McpServerSelection"/>'s
    /// own doc comment) and still shown, just marked <c>disabled: true</c> so the graph doesn't
    /// misleadingly imply a disabled server's tools are still being offered to the model.</summary>
    public static object Build(
        IReadOnlyList<(string ServerName, IReadOnlyList<AIFunction> Tools)> mcpServerToolGroups,
        IReadOnlySet<string> enabledServerNames)
    {
        var nodes = new List<object>
        {
            new { id = RootAgentName, type = "agent", isRoot = true }
        };
        var edges = new List<object>();

        foreach (var (serverName, tools) in mcpServerToolGroups)
        {
            var serverId = $"server::{serverName}";
            var disabled = !enabledServerNames.Contains(serverName);
            nodes.Add(new { id = serverId, type = "server", name = serverName, disabled });
            edges.Add(new { from = RootAgentName, to = serverId, kind = "connects", disabled });

            foreach (var tool in tools)
            {
                var toolId = $"{serverId}::{tool.Name}";
                nodes.Add(new
                {
                    id = toolId,
                    type = "tool",
                    ownerAgent = RootAgentName,
                    ownerServer = serverName,
                    operation = tool.Name,
                    description = tool.Description,
                    // The exact same check AgentFactory uses at tool-build time - calling it
                    // directly (not a separate copy) after a real bug where this projector's own
                    // duplicate of the logic drifted out of sync with a fix made only to the other
                    // copy (see AgentFactory.RequiresConfirmation's own doc comment).
                    requiresConfirmation = AgentFactory.RequiresConfirmation(tool),
                    // Only what the LLM sees - the JSON schema's own parameter names, same rule
                    // followed everywhere else this distinction matters.
                    parameters = tool.JsonSchema.TryGetProperty("properties", out var properties)
                        ? properties.EnumerateObject().Select(p => p.Name).ToArray()
                        : [],
                    disabled
                });
                edges.Add(new { from = serverId, to = toolId, kind = "uses", disabled });
            }
        }

        return new { nodes, edges };
    }
}
