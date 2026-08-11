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

    private static readonly string[] KnownProviders = ["Ollama", "OpenAI"];

    public static void Validate(Dictionary<string, AgentConfig> agents, OperationCatalog catalog, OperationCatalog simulatorCatalog, OperationCatalog watchdogCatalog, OperationCatalog watchdogConfigCatalog, OpenAiOptions openAiOptions)
    {
        var errors = new List<string>();

        foreach (var (agentName, config) in agents)
        {
            if (string.IsNullOrWhiteSpace(config.Instructions))
            {
                errors.Add($"Agent '{agentName}' is missing 'Instructions'.");
            }

            if (config.Provider is not null && !KnownProviders.Contains(config.Provider, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Agent '{agentName}' has unknown provider '{config.Provider}' (from 'AgentModels' in " +
                            $"appsettings.json) — must be one of: {string.Join(", ", KnownProviders)}.");
            }

            // Config-only check (no network call, same as everything else here) — turns "forgot to
            // set the secret" into an immediate startup error instead of a confusing failure the
            // first time this agent tries to respond.
            if (string.Equals(config.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(openAiOptions.ApiKey))
            {
                errors.Add($"Agent '{agentName}' has provider: OpenAI (from 'AgentModels' in appsettings.json) but " +
                            "no API key is configured. Set one via " +
                            "`dotnet user-secrets set \"OpenAI:ApiKey\" \"...\" --project src/UavOps.Agent`.");
            }

            foreach (var tool in config.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.Description))
                {
                    errors.Add($"Agent '{agentName}' tool '{tool.Operation}' is missing a 'Description'.");
                }

                if (string.IsNullOrWhiteSpace(tool.ExampleUtterance))
                {
                    errors.Add($"Agent '{agentName}' tool '{tool.Operation}' is missing an 'ExampleUtterance'.");
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
                if (!resolved)
                {
                    resolved = watchdogCatalog.TryResolve(tool.Operation, out descriptor) && descriptor is not null;
                }
                if (!resolved)
                {
                    resolved = watchdogConfigCatalog.TryResolve(tool.Operation, out descriptor) && descriptor is not null;
                }

                if (!resolved || descriptor is null)
                {
                    errors.Add($"Agent '{agentName}' references unknown operation '{tool.Operation}'. " +
                                "Check the spelling against IOperationService's/ISimulatorService's/IWatchdogService's/IWatchdogConfigService's method names.");
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

            if (agentName != RootAgentName && string.IsNullOrWhiteSpace(config.ExampleUtterance))
            {
                errors.Add($"Agent '{agentName}' is missing a non-blank 'ExampleUtterance' (required so the " +
                            "agent graph UI can show an example operator utterance that would trigger it).");
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Agent configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }
    }
}
