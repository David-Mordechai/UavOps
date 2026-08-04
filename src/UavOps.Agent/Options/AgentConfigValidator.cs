using UavOps.Agent.Tooling;

namespace UavOps.Agent.Options;

/// <summary>
/// Fail-fast startup validation: catches the "prompt advertises tools that don't exist" bug class
/// at boot instead of silently at inference time. Any violation stops the app from starting with a
/// message naming exactly which tool/parameter/field is wrong. Also compensates for
/// <see cref="AgentConfig"/>/<see cref="AgentToolConfig"/> using plain mutable properties instead
/// of <c>required</c>/<c>init</c> (needed for YamlDotNet deserialization) — a required field
/// silently omitted from a YAML file becomes `""` rather than a compile-time error, so this
/// explicitly checks for that instead.
/// </summary>
public static class AgentConfigValidator
{
    private const string RootAgentName = "BrainAgent";
    private const string OperatorPromptKind = "OperatorPrompt";

    public static void Validate(Dictionary<string, AgentConfig> agents, OperationCatalog catalog, OperationCatalog simulatorCatalog)
    {
        var errors = new List<string>();

        foreach (var (agentName, config) in agents)
        {
            if (string.IsNullOrWhiteSpace(config.Instructions))
            {
                errors.Add($"Agent '{agentName}' is missing 'Instructions'.");
            }

            foreach (var tool in config.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Description))
                {
                    errors.Add($"Agent '{agentName}' tool '{tool.Operation}' is missing a 'Description'.");
                }

                if (tool.Kind == OperatorPromptKind)
                {
                    // Bespoke ask-the-operator tool (AskOperatorChoiceTool) — not reflected off
                    // any catalog, so there's no parameter contract to check it against.
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tool.Operation))
                {
                    errors.Add($"Agent '{agentName}' has a tool with a missing 'Operation'.");
                    continue;
                }

                var resolved = catalog.TryResolve(tool.Operation, out var descriptor) && descriptor is not null;
                if (!resolved)
                {
                    resolved = simulatorCatalog.TryResolve(tool.Operation, out descriptor) && descriptor is not null;
                }

                if (!resolved || descriptor is null)
                {
                    errors.Add($"Agent '{agentName}' references unknown operation '{tool.Operation}'. " +
                                "Check the spelling against IOperationService's/ISimulatorService's method names.");
                    continue;
                }

                foreach (var p in descriptor.Parameters)
                {
                    if (tool.FixedParameters.ContainsKey(p.Name))
                    {
                        continue;
                    }

                    if (!tool.Parameters.ContainsKey(p.Name))
                    {
                        errors.Add($"Agent '{agentName}' tool '{tool.Operation}' is missing a description for " +
                                    $"parameter '{p.Name}' — add it under Parameters or FixedParameters.");
                    }
                }
            }

            if (config.Children is not null)
            {
                foreach (var child in config.Children)
                {
                    if (!agents.ContainsKey(child))
                    {
                        errors.Add($"Agent '{agentName}' declares child '{child}' which does not exist in AgentsConfig.");
                    }
                }
            }

            if (agentName != RootAgentName && string.IsNullOrWhiteSpace(config.Description))
            {
                errors.Add($"Agent '{agentName}' is missing a non-blank 'Description' (required for retrieval, " +
                            "and shown as this agent's tool description whenever it's delegated to).");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Agent configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }
    }
}
