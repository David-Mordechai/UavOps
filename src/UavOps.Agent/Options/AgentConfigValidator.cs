using UavOps.Agent.Tooling;

namespace UavOps.Agent.Options;

/// <summary>
/// Fail-fast startup validation: catches the "prompt advertises tools that don't exist" bug class
/// at boot instead of silently at inference time. Any violation stops the app from starting with a
/// message naming exactly which tool/parameter/field is wrong. Also compensates for
/// <see cref="AgentConfig"/>/<see cref="AgentToolConfig"/> using plain mutable properties instead
/// of <c>required</c>/<c>init</c> (needed for YamlDotNet deserialization) — a required field
/// silently omitted from a YAML file becomes `""` rather than a compile-time error, so this
/// explicitly checks for that instead. Takes a single <see cref="AgentConfig"/> now (one flat
/// agent) — no more cross-agent <c>Children</c>-reference check, since there's nothing left to
/// delegate to.
/// </summary>
public static class AgentConfigValidator
{
    private const string OperatorPromptKind = "OperatorPrompt";

    private static readonly string[] KnownProviders = ["Ollama", "OpenAI"];

    public static void Validate(AgentConfig config, OperationCatalog catalog, OperationCatalog simulatorCatalog, OperationCatalog watchdogCatalog, OperationCatalog watchdogConfigCatalog, OpenAiOptions openAiOptions)
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

        // Config-only check (no network call, same as everything else here) — turns "forgot to
        // set the secret" into an immediate startup error instead of a confusing failure the
        // first time this agent tries to respond.
        if (string.Equals(config.Provider, "OpenAI", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(openAiOptions.ApiKey))
        {
            errors.Add("BrainAgent has provider: OpenAI (from 'AgentModels' in appsettings.json) but " +
                        "no API key is configured. Set one via " +
                        "`dotnet user-secrets set \"OpenAI:ApiKey\" \"...\" --project src/UavOps.Agent`.");
        }

        foreach (var tool in config.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Description))
            {
                errors.Add($"Tool '{tool.Operation}' is missing a 'Description'.");
            }

            if (string.IsNullOrWhiteSpace(tool.ExampleUtterance))
            {
                errors.Add($"Tool '{tool.Operation}' is missing an 'ExampleUtterance'.");
            }

            if (tool.Kind == OperatorPromptKind)
            {
                // Bespoke ask-the-operator tool (AskOperatorChoiceTool) — not reflected off
                // any catalog, so there's no parameter contract to check it against.
                continue;
            }

            if (string.IsNullOrWhiteSpace(tool.Operation))
            {
                errors.Add("A tool is missing an 'Operation'.");
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
                errors.Add($"References unknown operation '{tool.Operation}'. " +
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
                    errors.Add($"Tool '{tool.Operation}' is missing a description for " +
                                $"parameter '{p.Name}' — add it under Parameters or FixedParameters.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Agent configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }
    }
}
