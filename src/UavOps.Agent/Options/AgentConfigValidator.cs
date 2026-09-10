namespace UavOps.Agent.Options;

/// <summary>
/// Fail-fast startup validation: catches basic YAML/config authoring mistakes at boot instead of
/// silently at inference time. Any violation stops the app from starting with a message naming
/// exactly which field is wrong. Also compensates for <see cref="AgentConfig"/> using plain mutable
/// properties instead of <c>required</c>/<c>init</c> (needed for YamlDotNet deserialization) — a
/// required field silently omitted from a YAML file becomes `""` rather than a compile-time error,
/// so this explicitly checks for that instead. Takes a single <see cref="AgentConfig"/> now (one
/// flat agent) — no more cross-agent <c>Children</c>-reference check, since there's nothing left to
/// delegate to. Every real operation across every domain (Moav, watchdog, simulator) is an MCP
/// tool now (<see cref="AgentConfig.McpServers"/>), including the operator lesson-choice prompt —
/// nothing is configured in-process anymore, so their own startup check is simply whether
/// connecting to each configured server and listing its tools succeeds (see <c>Program.cs</c>,
/// same fail-fast-at-boot posture already established for the embedding endpoint), not a
/// reflection check here.
/// </summary>
public static class AgentConfigValidator
{
    private static readonly string[] KnownProviders = ["Ollama", "OpenAI"];

    public static void Validate(AgentConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Instructions))
        {
            errors.Add("BrainAgent is missing 'Instructions'.");
        }

        if (config.Provider is not null && !KnownProviders.Contains(config.Provider, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"BrainAgent has unknown provider '{config.Provider}' (from 'AgentModels' in " +
                        $"appsettings.json) — must be one of: {string.Join(", ", KnownProviders)}.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Agent configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }
    }
}
