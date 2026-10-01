using System.Text.Json;
using UavOps.Agent.Contracts;
using UavOps.Agent.Mission;

namespace UavOps.Agent.McpMoav.Missions;

/// <summary>
/// Checks a plan the agent wrote before anything runs: every trigger is known, every action is a
/// real McpMoav tool that changes something, every argument is one of its real parameters with a
/// value of the right type (tail numbers may be roles), required ones are there, "after N" points
/// to an earlier step, a role that needs the finder is only used once a target was found, tail
/// numbers are the fleet's and zones exist. Every problem is named by its step, so the agent can fix
/// the plan or ask the operator; a plan with any problem is never stored or run.
/// </summary>
public sealed class MissionPlanValidator(StepExecutor executor, IOperationService fleet, IAoiZoneStore zones)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public const int MaxSteps = 20;

    public async Task<(List<PlanStep> Steps, IReadOnlyList<string> Uavs, List<string> Errors)> ValidateAsync(
        IReadOnlyList<MissionStepInput> input, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var steps = new List<PlanStep>();
        if (input.Count == 0)
            errors.Add("The plan has no steps.");
        if (input.Count > MaxSteps)
            errors.Add($"The plan has {input.Count} steps; at most {MaxSteps}.");

        var known = await KnownTailsAsync(cancellationToken);
        var uavs = new List<string>();
        for (var i = 0; i < input.Count; i++)
        {
            var number = i + 1;
            var s = input[i];
            var trigger = Trigger.Parse(s.When);
            if (trigger is null)
            {
                errors.Add($"Step {number}: '{s.When}' isn't a trigger. Use start, after N, target.found, target.lost, target.regained, " +
                           "target.given_up, pass.completed or time N (seconds after start).");
                continue;
            }
            if (trigger.Kind == TriggerKind.After && (trigger.Value < 1 || trigger.Value >= number))
                errors.Add($"Step {number}: 'after {trigger.Value}' must name an earlier step (1-{number - 1}).");

            if (!executor.IsAction(s.Do))
            {
                errors.Add($"Step {number}: '{s.Do}' isn't a tool a step can run. Steps can run: {string.Join(", ", executor.Actions)}.");
                continue;
            }

            var args = s.Args ?? [];
            var parameters = executor.ModelParameters(s.Do);
            foreach (var key in args.Keys.Where(k => parameters.All(p => p.Name != k)))
                errors.Add($"Step {number}: {s.Do} has no argument '{key}' (it takes: {string.Join(", ", parameters.Select(p => p.Name))}).");
            foreach (var p in parameters.Where(p => StepExecutor.IsRequired(p) && !args.ContainsKey(p.Name!)))
                errors.Add($"Step {number}: {s.Do} needs '{p.Name}'.");

            foreach (var p in parameters.Where(p => args.ContainsKey(p.Name!)))
            {
                var value = args[p.Name!];
                if (StepExecutor.IsTailParameter(p))
                {
                    var parts = Parts(value);
                    if (parts.Any(v => v is Roles.Finder or Roles.Others) && !NeedsFinderOk(input, i))
                        errors.Add($"Step {number}: {Roles.Finder}/{Roles.Others} only mean something once the target was found " +
                                   "(a target.* step, or one after it).");
                    foreach (var tail in parts.Where(v => !Roles.IsRole(v)))
                    {
                        if (tail.Equals("ALL", StringComparison.OrdinalIgnoreCase))
                            errors.Add($"Step {number}: 'ALL' isn't a tail number; name the UAVs, or use {Roles.All} for every UAV the plan names.");
                        else if (known is not null && !known.Contains(tail, StringComparer.OrdinalIgnoreCase))
                            errors.Add($"Step {number}: '{tail}' isn't a known UAV (known: {string.Join(", ", known)}).");
                        else if (!uavs.Contains(tail, StringComparer.OrdinalIgnoreCase))
                            uavs.Add(tail);
                    }
                    continue;
                }
                try
                {
                    value.Deserialize(p.ParameterType, Json);
                }
                catch (JsonException)
                {
                    errors.Add($"Step {number}: '{p.Name}' must be {TypeName(p.ParameterType)}, not {value}.");
                }
                if (p.Name == "zoneName" && value.ValueKind == JsonValueKind.String &&
                    await zones.GetAsync(value.GetString()!, cancellationToken) is null)
                    errors.Add($"Step {number}: there is no AOI zone '{value.GetString()}'.");
            }
            steps.Add(new PlanStep(number, trigger, s.Do, new Dictionary<string, JsonElement>(args)));
        }

        if (input.Count > 0 && !input.Any(s => Trigger.Parse(s.When)?.Kind == TriggerKind.Start))
            errors.Add("No step runs at start: the plan would never begin.");
        if (errors.Count == 0 && uavs.Count == 0)
            errors.Add("The plan names no UAV.");
        return (steps, uavs, errors);
    }

    /// <summary>A step may use the finder when it, or a step it waits for, is about the target.</summary>
    private static bool NeedsFinderOk(IReadOnlyList<MissionStepInput> input, int index)
    {
        for (var guard = 0; guard < MaxSteps && index >= 0 && index < input.Count; guard++)
        {
            var trigger = Trigger.Parse(input[index].When);
            if (trigger is null)
                return false;
            if (trigger.IsAboutTarget)
                return true;
            if (trigger.Kind != TriggerKind.After)
                return false;
            index = trigger.Value - 1;
        }
        return false;
    }

    private static List<string> Parts(JsonElement value) =>
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : e.ToString()).ToList()
            : value.ValueKind == JsonValueKind.String
                ? value.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : [value.ToString()];

    private async Task<List<string>?> KnownTailsAsync(CancellationToken cancellationToken)
    {
        var result = await fleet.ListFleet(cancellationToken);
        if (!result.Success || result.Value is null)
            return null; // can't tell: the fleet is checked when the step runs
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Value, Json));
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(u => u.TryGetProperty("tailNumber", out var t) ? t.GetString() : null).OfType<string>().ToList()
            : null;
    }

    private static string TypeName(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(string) ? "text" : t == typeof(bool) ? "true or false" : t == typeof(int) ? "a whole number"
            : t == typeof(double) ? "a number" : t.IsArray ? "a list" : t.Name;
    }
}
