using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace UavOps.Agent.Contracts;

/// <summary>
/// Builds one MCP server's real <see cref="McpServerTool"/> instances from its own reflected tool
/// methods plus a loaded <see cref="McpToolsConfig"/> - no <c>[McpServerTool]</c>/<c>[Description]</c>
/// attributes involved anywhere, since those are compile-time C# metadata and the whole point here
/// is that a description/parameter-description change needs only an edit to that server's own
/// <c>ToolsConfig.yaml</c>, never a rebuild. Verified against the installed SDK's own XML docs
/// before writing this (not assumed): <see cref="McpServerTool.Create(MethodInfo, object?, McpServerToolCreateOptions)"/>
/// itself documents that programmatic creation "provide[s] the same level of configuration
/// flexibility" as attributes, and <see cref="AIJsonSchemaCreateOptions.ParameterDescriptionProvider"/>
/// exists specifically to supply a parameter's description from anywhere other than
/// <see cref="System.ComponentModel.DescriptionAttribute"/>.
/// </summary>
public static class McpToolsBuilder
{
    /// <param name="toolsType">The static class holding this server's tool methods (e.g. <c>MoavTools</c>) -
    /// every public static method on it is treated as one tool.</param>
    /// <param name="config">Loaded from that server's own <c>ToolsConfig.yaml</c>.</param>
    /// <param name="services">The server's own <see cref="IServiceCollection"/>, still being built
    /// (before <c>Build()</c>) - only used to build a throwaway <see cref="IServiceProvider"/> that
    /// reflects the same registrations as the real one, which is all
    /// <see cref="McpServerToolCreateOptions.Services"/> needs per its own doc comment (used only to
    /// decide which parameters are DI-satisfied, not to actually resolve them - that happens for
    /// real at invocation time against the real built container).</param>
    public static List<McpServerTool> Build(Type toolsType, McpToolsConfig config, IServiceCollection services)
    {
        var throwawayServices = services.BuildServiceProvider();
        var methods = toolsType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var configByOperation = config.Tools.ToDictionary(t => t.Operation);

        var errors = new List<string>();
        var tools = new List<McpServerTool>();

        foreach (var method in methods)
        {
            if (!configByOperation.Remove(method.Name, out var entry))
            {
                errors.Add($"Method '{method.Name}' on {toolsType.Name} has no matching entry in ToolsConfig.yaml.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                errors.Add($"Tool '{entry.Operation}' is missing a 'description' in ToolsConfig.yaml.");
            }

            foreach (var parameter in method.GetParameters())
            {
                if (IsDiOrCancellationToken(parameter, throwawayServices))
                {
                    continue;
                }

                if (!entry.Parameters.ContainsKey(parameter.Name!))
                {
                    errors.Add($"Tool '{entry.Operation}' parameter '{parameter.Name}' has no description in ToolsConfig.yaml.");
                }
            }

            tools.Add(McpServerTool.Create(method, target: null, new McpServerToolCreateOptions
            {
                Name = entry.Operation,
                Description = entry.Description,
                ReadOnly = entry.ReadOnly,
                Destructive = entry.Destructive,
                Services = throwawayServices,
                SchemaCreateOptions = new AIJsonSchemaCreateOptions
                {
                    ParameterDescriptionProvider = parameter => entry.Parameters.GetValueOrDefault(parameter.Name ?? "")
                }
            }));
        }

        // Whatever's left in configByOperation names an entry with no matching method.
        errors.AddRange(configByOperation.Keys.Select(orphan =>
            $"ToolsConfig.yaml has an entry for '{orphan}', but {toolsType.Name} has no matching method."));

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"{toolsType.Name}'s ToolsConfig.yaml is invalid:{Environment.NewLine} - {string.Join(Environment.NewLine + " - ", errors)}");
        }

        return tools;
    }

    private static bool IsDiOrCancellationToken(ParameterInfo parameter, IServiceProvider services)
    {
        if (parameter.ParameterType == typeof(CancellationToken))
        {
            return true;
        }

        return services.GetService(parameter.ParameterType) is not null;
    }
}
