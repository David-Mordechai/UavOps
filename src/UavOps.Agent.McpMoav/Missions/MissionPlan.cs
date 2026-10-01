using System.Globalization;
using System.Text.Json;

namespace UavOps.Agent.McpMoav.Missions;

/// <summary>One step as the agent writes it: when it runs, which McpMoav tool it calls, with which
/// arguments (the tool's own argument names; tail numbers may be a role, <see cref="Roles"/>).</summary>
public sealed record MissionStepInput(string When, string Do, Dictionary<string, JsonElement>? Args = null);

/// <summary>What makes a step run.</summary>
public enum TriggerKind { Start, After, TargetFound, TargetLost, TargetRegained, TargetGivenUp, PassCompleted, Time }

public sealed record Trigger(TriggerKind Kind, int Value = 0)
{
    /// <summary>"start", "after 2", "target.found", "target.lost", "target.regained",
    /// "target.given_up", "pass.completed", "time 300" (seconds after start). Null if it isn't one.</summary>
    public static Trigger? Parse(string text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant().Replace("_", ".").Replace(" ", "");
        if (t.StartsWith("after", StringComparison.Ordinal) && int.TryParse(t["after".Length..].TrimStart(':', '.', '#').Replace("step", ""), out var n))
            return new Trigger(TriggerKind.After, n);
        if (t.StartsWith("time", StringComparison.Ordinal) &&
            double.TryParse(t["time".Length..].TrimStart(':', '.').TrimEnd('s'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return new Trigger(TriggerKind.Time, (int)Math.Round(seconds));
        return t switch
        {
            "start" => new Trigger(TriggerKind.Start),
            "target.found" => new Trigger(TriggerKind.TargetFound),
            "target.lost" => new Trigger(TriggerKind.TargetLost),
            "target.regained" => new Trigger(TriggerKind.TargetRegained),
            "target.given.up" or "target.givenup" => new Trigger(TriggerKind.TargetGivenUp),
            "pass.completed" => new Trigger(TriggerKind.PassCompleted),
            _ => null
        };
    }

    /// <summary>Whether a step on this trigger runs because of something about the target (so the
    /// <see cref="Roles.Finder"/> is known).</summary>
    public bool IsAboutTarget => Kind is TriggerKind.TargetFound or TriggerKind.TargetLost or TriggerKind.TargetRegained or TriggerKind.TargetGivenUp;

    public string Describe() => Kind switch
    {
        TriggerKind.Start => "At start",
        TriggerKind.After => $"After step {Value}",
        TriggerKind.TargetFound => "When the target is found",
        TriggerKind.TargetLost => "When the target is lost",
        TriggerKind.TargetRegained => "When the target is found again",
        TriggerKind.TargetGivenUp => "When the target can't be found again",
        TriggerKind.PassCompleted => "After each full search pass",
        TriggerKind.Time => Value % 60 == 0 ? $"{Value / 60} min after start" : $"{Value} s after start",
        _ => Kind.ToString()
    };
}

/// <summary>Who a tail-number argument means, filled in when the step runs.</summary>
public static class Roles
{
    /// <summary>The UAV that found the target (the first of the mission's UAVs to report it).</summary>
    public const string Finder = "{finder}";
    /// <summary>The mission's other UAVs.</summary>
    public const string Others = "{others}";
    /// <summary>Every UAV the mission names.</summary>
    public const string All = "{all}";

    public static bool IsRole(string value) => value.Trim() is Finder or Others or All;
}

public enum StepState { Waiting, Running, Done, Failed, Skipped }

public enum PlanStatus { Created, Running, Finished, Cancelled }

public sealed class PlanStep(int number, Trigger when, string tool, Dictionary<string, JsonElement> args)
{
    public int Number { get; } = number;
    public Trigger When { get; } = when;
    public string Do { get; } = tool;
    public Dictionary<string, JsonElement> Args { get; } = args;
    public StepState State { get; set; } = StepState.Waiting;
    public string? Result { get; set; }
    public DateTime? RanAtUtc { get; set; }
}

/// <summary>An operator's mission as steps, checked (<see cref="MissionPlanValidator"/>), shown, and
/// run by <see cref="MissionEngine"/> when the operator says start.</summary>
public sealed class MissionPlan(string id, List<PlanStep> steps, IReadOnlyList<string> uavs)
{
    public string Id { get; } = id;
    public List<PlanStep> Steps { get; } = steps;

    /// <summary>Every UAV the steps name (what <see cref="Roles.All"/> means).</summary>
    public IReadOnlyList<string> Uavs { get; } = uavs;

    public PlanStatus Status { get; set; } = PlanStatus.Created;
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedUtc { get; set; }

    /// <summary>The UAV that found the target, once one has.</summary>
    public string? Finder { get; set; }

    /// <summary>Whether a step makes a search track its target (so only one UAV may track it).</summary>
    public bool Tracks => Steps.Any(s => s.Args.TryGetValue("track", out var v) && v.ValueKind == JsonValueKind.True);
}
