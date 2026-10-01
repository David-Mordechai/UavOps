using System.Reflection;
using System.Text.Json;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpMoav.Missions;

/// <summary>
/// Runs a plan step: the same static <see cref="MoavTools"/> method the MCP tool of that name wraps
/// (the lookup <c>McpToolsBuilder</c> uses), so a step does exactly what the tool does when the
/// model calls it. Arguments are bound by name from the step's JSON; parameters the container
/// satisfies (the fleet, the zone store...) and the cancellation token come from DI, as for the
/// MCP tools. A tail-number argument may be a role (<see cref="Roles"/>), filled in when the step
/// runs; a single-UAV tool given several UAVs runs once per UAV.
/// </summary>
public sealed class StepExecutor(IServiceProvider services, McpToolsConfig toolsConfig)
{
    /// <summary>The plan tools themselves: never a step.</summary>
    public static readonly string[] PlanTools = ["CreateMissionPlan", "StartMissionPlan", "GetMissionPlan", "CancelMissionPlan"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, MethodInfo> Methods =
        typeof(MoavTools).GetMethods(BindingFlags.Public | BindingFlags.Static).ToDictionary(m => m.Name, StringComparer.Ordinal);

    /// <summary>A tool a step may run: a real McpMoav tool that changes something (not read-only,
    /// not a plan tool).</summary>
    public bool IsAction(string tool) =>
        Methods.ContainsKey(tool) && !PlanTools.Contains(tool) &&
        toolsConfig.Tools.FirstOrDefault(t => t.Operation == tool)?.ReadOnly != true;

    public IEnumerable<string> Actions => Methods.Keys.Where(IsAction).Order();

    /// <summary>The arguments the model gives this tool (what its MCP schema shows).</summary>
    public IReadOnlyList<ParameterInfo> ModelParameters(string tool) =>
        Methods.TryGetValue(tool, out var method)
            ? method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken) && services.GetService(p.ParameterType) is null).ToList()
            : [];

    public static bool IsTailParameter(ParameterInfo parameter) => parameter.Name is "tailNumber" or "tailNumbers";

    public static bool IsRequired(ParameterInfo parameter) =>
        !parameter.HasDefaultValue && Nullable.GetUnderlyingType(parameter.ParameterType) is null &&
        new NullabilityInfoContext().Create(parameter).WriteState != NullabilityState.Nullable;

    /// <summary>Tail numbers a tail argument stands for: a role, or "997" / "997,998" / ["997","998"].</summary>
    public static List<string> ExpandTails(JsonElement value, IReadOnlyList<string> all, string? finder)
    {
        var parts = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.GetString() ?? "")
            : (value.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tails = new List<string>();
        foreach (var part in parts.Select(p => p.Trim()))
        {
            switch (part)
            {
                case Roles.All:
                    tails.AddRange(all);
                    break;
                case Roles.Finder:
                    tails.Add(finder ?? throw new InvalidOperationException("no UAV has found the target yet"));
                    break;
                case Roles.Others:
                    if (finder is null)
                        throw new InvalidOperationException("no UAV has found the target yet");
                    tails.AddRange(all.Where(t => !string.Equals(t, finder, StringComparison.OrdinalIgnoreCase)));
                    break;
                default:
                    if (part.Length > 0)
                        tails.Add(part);
                    break;
            }
        }
        return tails.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Runs the step's tool; one result per call (per UAV for a single-UAV tool given several).</summary>
    public async Task<IReadOnlyList<(string? Tail, string Result)>> ExecuteAsync(
        string tool, IReadOnlyDictionary<string, JsonElement> args, IReadOnlyList<string> all, string? finder, CancellationToken cancellationToken)
    {
        if (!Methods.TryGetValue(tool, out var method) || !IsAction(tool))
            throw new InvalidOperationException($"'{tool}' is not a tool a mission step can run.");

        var single = method.GetParameters().FirstOrDefault(p => p.Name == "tailNumber");
        IReadOnlyList<string?> perUav = single is not null && args.TryGetValue("tailNumber", out var tailArg)
            ? ExpandTails(tailArg, all, finder).Cast<string?>().ToList()
            : [null];
        if (perUav.Count == 0)
            return [(null, "Skipped: no UAV to run it for.")];

        var results = new List<(string?, string)>();
        foreach (var tail in perUav)
        {
            var arguments = Bind(method, args, tail, all, finder, cancellationToken);
            var returned = method.Invoke(null, arguments);
            var text = returned switch
            {
                Task<string> task => await task,
                string s => s,
                _ => returned?.ToString() ?? ""
            };
            results.Add((tail, text));
        }
        return results;
    }

    private object?[] Bind(MethodInfo method, IReadOnlyDictionary<string, JsonElement> args, string? tail, IReadOnlyList<string> all, string? finder,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var values = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(CancellationToken))
                values[i] = cancellationToken;
            else if (services.GetService(p.ParameterType) is { } service)
                values[i] = service;
            else if (p.Name == "tailNumber" && tail is not null)
                values[i] = tail;
            else if (p.Name == "tailNumbers" && args.TryGetValue("tailNumbers", out var list))
                values[i] = ExpandTails(list, all, finder).ToArray();
            else if (args.TryGetValue(p.Name!, out var value))
                values[i] = value.Deserialize(p.ParameterType, Json);
            else if (p.HasDefaultValue)
                values[i] = p.DefaultValue;
            else
                throw new ArgumentException($"'{p.Name}' is missing.");
        }
        return values;
    }
}
