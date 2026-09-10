namespace UavOps.Agent.Options;

/// <summary>
/// Reads which MCP servers are currently enabled — live from <see cref="IConfiguration"/> on every
/// call, same pattern as <see cref="Tooling.ConfirmationGate.CurrentMode"/> for
/// <c>ExecutionMode</c> — so a Settings-page save takes effect on the very next chat turn with no
/// restart, unlike every other setting in this app. The MCP connection loop in <c>Program.cs</c>
/// deliberately does NOT consult this: it always connects to every configured server regardless of
/// enabled state (a deliberate choice — see the Settings-page plan) — this only decides which
/// already-connected server's tools are offered to the model each turn
/// (<see cref="Agents.AgentFactory.BuildToolsForTurn"/>) and which server is shown as disabled in
/// the Agent Graph tab (<see cref="AgentGraphProjector"/>).
/// </summary>
public static class McpServerSelection
{
    public const string ConfigKey = "McpServersEnabled";

    /// <summary>A server with no entry in the map (or no map at all — the common case until an
    /// operator saves a settings change) defaults to enabled, matching today's behavior.</summary>
    public static IReadOnlySet<string> GetEnabledServerNames(IConfiguration configuration, IEnumerable<string> allServerNames)
    {
        var map = configuration.GetSection(ConfigKey).Get<Dictionary<string, bool>>() ?? [];
        return allServerNames
            .Where(name => !map.TryGetValue(name, out var enabled) || enabled)
            .ToHashSet(StringComparer.Ordinal);
    }
}
