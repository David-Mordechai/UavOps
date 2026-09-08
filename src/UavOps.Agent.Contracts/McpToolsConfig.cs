using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace UavOps.Agent.Contracts;

/// <summary>
/// One MCP server's entire AI-facing configuration - its own <c>ServerInstructions</c> plus every
/// tool's description/parameter descriptions/annotations - loaded from that server's own
/// <c>ToolsConfig.yaml</c> at startup. Mirrors <c>UavOps.Agent</c>'s own <c>Agents/BrainAgent.yaml</c>
/// convention (same list-of-objects shape for <see cref="Tools"/>) so every AI-facing string in
/// this whole system, host or domain, lives in a YAML file editable without a rebuild - none of it
/// in C# attributes, which are compile-time by nature. Lives in
/// <c>UavOps.Agent.Contracts</c> (not any one Mcp* project) since all 3 servers load an instance of
/// this same shape - same reasoning as <see cref="OperationResult"/> living here.
/// </summary>
public sealed class McpToolsConfig
{
    public string ServerInstructions { get; set; } = "";
    public List<McpToolConfigEntry> Tools { get; set; } = [];
}

/// <summary>One tool's AI-facing configuration. <see cref="Operation"/> must match the name of a
/// public static method on that server's own tools class exactly (case-sensitive) - <see cref="McpToolsBuilder"/>
/// fails fast at startup if either side has an entry the other doesn't.</summary>
public sealed class McpToolConfigEntry
{
    public string Operation { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Maps to <c>Tool.Annotations.ReadOnlyHint</c>/<c>DestructiveHint</c> - both nullable
    /// so a tool can be left unspecified, keeping the MCP spec's own conservative default
    /// (destructive unless told otherwise) - see <c>AgentFactory.RequiresConfirmation</c>'s own
    /// doc comment for why this matters and what happens if a tool is left unmarked on purpose
    /// (e.g. <c>RunSimulatorLesson</c>).</summary>
    public bool? ReadOnly { get; set; }
    public bool? Destructive { get; set; }

    /// <summary>Parameter name -> description shown to the LLM. Must cover every parameter the
    /// method declares that isn't satisfied from dependency injection or a <see cref="CancellationToken"/>.</summary>
    public Dictionary<string, string> Parameters { get; set; } = [];
}

/// <summary>Loads an <see cref="McpToolsConfig"/> from a YAML file - same
/// DeserializerBuilder/CamelCaseNamingConvention/error-wrapping convention as
/// <c>UavOps.Agent.Options.AgentConfigLoader</c>, just for this shape.</summary>
public static class McpToolsConfigLoader
{
    public static McpToolsConfig Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"MCP tools config file not found: '{filePath}'.");
        }

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        try
        {
            return deserializer.Deserialize<McpToolsConfig>(File.ReadAllText(filePath))
                ?? throw new InvalidOperationException("File is empty.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to parse MCP tools config '{filePath}': {ex.Message}", ex);
        }
    }
}
