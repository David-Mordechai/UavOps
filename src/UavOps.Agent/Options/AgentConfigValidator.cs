using UavOps.Agent.Tooling;

namespace UavOps.Agent.Options;

/// <summary>
/// Fail-fast startup validation: catches the PoC's "prompt advertises tools that don't exist"
/// bug class at boot instead of silently at inference time. Any violation stops the app from
/// starting with a message naming exactly which tool/parameter is wrong.
/// </summary>
public static class AgentConfigValidator
{
    public static void Validate(Dictionary<string, AgentConfig> agents, OpenApiToolCatalog catalog)
    {
        var errors = new List<string>();

        foreach (var (agentName, config) in agents)
        {
            foreach (var tool in config.Tools)
            {
                if (!catalog.TryResolve(tool.OperationId, out var descriptor) || descriptor is null)
                {
                    errors.Add($"Agent '{agentName}' references unknown OpenAPI operationId '{tool.OperationId}'. " +
                                "Check UavApi:OpenApiUrl and the operationId spelling.");
                    continue;
                }

                foreach (var p in descriptor.Parameters)
                {
                    if (tool.FixedParameters.ContainsKey(p.Name))
                    {
                        continue;
                    }

                    if (p.Required && !tool.Parameters.ContainsKey(p.Name))
                    {
                        errors.Add($"Agent '{agentName}' tool '{tool.OperationId}' is missing a description for " +
                                    $"required parameter '{p.Name}' — add it under Parameters or FixedParameters.");
                    }
                }
            }

            foreach (var delegateName in config.Delegates)
            {
                if (!agents.ContainsKey(delegateName))
                {
                    errors.Add($"Agent '{agentName}' delegates to unknown agent '{delegateName}'.");
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
