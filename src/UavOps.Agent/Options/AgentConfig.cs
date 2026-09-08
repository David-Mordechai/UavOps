namespace UavOps.Agent.Options;

/// <summary>
/// The single flat agent (BrainAgent) — one consolidated YAML file
/// (<c>Agents/BrainAgent.yaml</c>) replaces what used to be 11 files across a multi-agent
/// delegation tree; every real operation is now discovered from a connected MCP server (see
/// <see cref="McpServers"/>) — nothing is configured in-process anymore. No
/// <c>Children</c>/<c>Description</c>/<c>ExampleUtterance</c> concept anymore — both existed only
/// to be shown to a parent agent as this agent's own tool description, or fed to agent-selection
/// embedding retrieval; with exactly one agent that is nobody's delegate, neither purpose exists.
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Not read from YAML — populated from the <c>AgentModels</c> section of
    /// appsettings.json instead (see <c>Program.cs</c>). Null means the app-wide Ollama default.</summary>
    public string? Model { get; set; }

    /// <summary>Which backend <see cref="Model"/> is resolved against — "OpenAI" (any OpenAI-
    /// compatible endpoint, see <see cref="OpenAiOptions"/>), or omitted/null (the default) for
    /// the local Ollama endpoint. Same <c>AgentModels</c>-appsettings.json origin as
    /// <see cref="Model"/>, not YAML.</summary>
    public string? Provider { get; set; }

    public string Instructions { get; set; } = "";

    /// <summary>Sampling temperature for this agent's model calls. Lower values (e.g. 0.1-0.3)
    /// make tool-calling decisions more consistent across repeated identical requests. Null uses
    /// the provider's default.</summary>
    public float? Temperature { get; set; }

    /// <summary>MCP servers BrainAgent connects to at startup for domain-split tools (see
    /// <c>CLAUDE.md</c>, "Split BrainAgent's 3 domains into separate MCP servers") — each is a
    /// separate .NET process launched over stdio, exposing only that domain's own tools with its
    /// own <c>[Description]</c>-authored names/descriptions/schemas, never mixed with another
    /// domain's. Every real tool BrainAgent can call comes from here — see
    /// <c>AgentFactory.BuildAllTools</c>.</summary>
    public List<McpServerConfig> McpServers { get; set; } = [];
}

/// <summary>One MCP server BrainAgent launches (stdio transport) and connects to at startup.
/// <see cref="Command"/>/<see cref="Args"/> are a plain process launch, deliberately not a bespoke
/// per-server config shape — <c>dotnet run --project ...</c> for every server today, but nothing
/// here assumes that specifically.</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];

    /// <summary>Optional: the name of this server's own MCP tool that enumerates the real UAV
    /// fleet - set only on the <c>moav</c> server's entry. Lets
    /// <c>AgentFactory.ListRealMoavFleetAsync</c> (the tail-number disambiguation safety net's
    /// fleet lookup) find the right tool without the host hardcoding the Moav domain's own tool
    /// naming in C#. Null for every server that doesn't supply one.</summary>
    public string? FleetListingTool { get; set; }
}

public enum ExecutionMode
{
    Direct,
    Confirm
}
