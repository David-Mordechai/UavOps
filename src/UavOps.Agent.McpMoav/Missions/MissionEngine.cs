using System.Text.Json;
using Microsoft.Extensions.Logging;
using UavOps.Agent.Contracts;

namespace UavOps.Agent.McpMoav.Missions;

/// <summary>
/// Holds the operator's mission plans and carries them out. A plan is created (checked, shown),
/// then started by the operator's "start"; from then on its steps run as their triggers happen -
/// the plan's start, an earlier step finishing, a time, or what the fleet reports about the target
/// (found, lost, found again, given up) and the searches (a pass completed) - each once, in this
/// process, deterministically. BrainAgent only acts when the operator sends a message; this is how
/// "when the car is found, send the other UAV home" happens at the moment it should.
///
/// The fleet's reports arrive on the hub connection's receive loop; steps run off it (a step calls
/// the hub, and calling it from inside its own handler can deadlock).
///
/// One rule needs no step: in a plan whose search tracks its target, only the first UAV to find it
/// tracks it. Another of the plan's UAVs locking on (the same car, seen from two UAVs) is stopped and
/// holds, and the operator is told.
/// </summary>
public sealed class MissionEngine(
    MissionPlanValidator validator,
    StepExecutor executor,
    IOperatorNotifier notifier,
    MissionEventService missionEvents,
    ILogger<MissionEngine> logger)
{
    private readonly object _lock = new();
    private readonly List<MissionPlan> _plans = [];
    private int _next = 1;

    public async Task<(MissionPlan? Plan, IReadOnlyList<string> Errors)> CreateAsync(IReadOnlyList<MissionStepInput> steps, CancellationToken cancellationToken)
    {
        var (planSteps, uavs, errors) = await validator.ValidateAsync(steps, cancellationToken);
        if (errors.Count > 0)
            return (null, errors);
        lock (_lock)
        {
            var plan = new MissionPlan($"P{_next++}", planSteps, uavs);
            _plans.Add(plan);
            return (plan, []);
        }
    }

    /// <summary>A plan by id; without one, the newest (the one just shown, or the one running).</summary>
    public MissionPlan? Find(string? id)
    {
        lock (_lock)
            return string.IsNullOrWhiteSpace(id)
                ? _plans.LastOrDefault()
                : _plans.FirstOrDefault(p => string.Equals(p.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The operator's "start": the plan's start steps run, and its timed steps are scheduled.</summary>
    public string Start(MissionPlan plan)
    {
        lock (_lock)
        {
            if (plan.Status != PlanStatus.Created)
                return $"Plan {plan.Id} is {plan.Status.ToString().ToLowerInvariant()}, it can't be started again.";
            plan.Status = PlanStatus.Running;
            plan.StartedUtc = DateTime.UtcNow;
        }
        foreach (var timed in plan.Steps.Where(s => s.When.Kind == TriggerKind.Time))
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(timed.When.Value));
                await RunIfWaitingAsync(plan, timed, "time");
            });
        Fire(plan, TriggerKind.Start, null, "the plan started");
        return "";
    }

    public void Cancel(MissionPlan plan)
    {
        lock (_lock)
        {
            if (plan.Status is PlanStatus.Created or PlanStatus.Running)
                plan.Status = PlanStatus.Cancelled;
            foreach (var step in plan.Steps.Where(s => s.State == StepState.Waiting))
                step.State = StepState.Skipped;
        }
    }

    // ----- What the fleet reports (called from McpMoav's fleet-event handlers) -----

    /// <summary>A detection: the first one by a plan's UAV (in a plan that hasn't found its target
    /// yet) is the target found, and that UAV is the finder.</summary>
    public void OnDetection(DetectionReport report) => OnTargetFound(report.TailNumber, $"{report.TailNumber} found the {report.Prompt.Trim()}");

    public void OnMissionEvent(MissionEventReport report)
    {
        switch (report.Kind)
        {
            case MissionEventKinds.Tracking:
                if (!OnTargetFound(report.TailNumber, $"{report.TailNumber} locked on the target"))
                    StopSecondTracker(report);
                break;
            case MissionEventKinds.TargetLost:
                FireForFinder(report.TailNumber, TriggerKind.TargetLost, "the target was lost");
                break;
            case MissionEventKinds.TargetRegained:
                FireForFinder(report.TailNumber, TriggerKind.TargetRegained, "the target was found again");
                break;
            case MissionEventKinds.SearchResumed:
                FireForFinder(report.TailNumber, TriggerKind.TargetGivenUp, "the target couldn't be found again");
                break;
            case MissionEventKinds.PassCompleted:
                foreach (var plan in RunningPlansOf(report.TailNumber))
                    Fire(plan, TriggerKind.PassCompleted, null, $"{report.TailNumber} finished a search pass", once: false);
                break;
        }
    }

    /// <returns>Whether this made the tail a plan's finder (false: no plan, or the plan already has one).</returns>
    private bool OnTargetFound(string tail, string why)
    {
        var found = false;
        foreach (var plan in RunningPlansOf(tail))
        {
            lock (_lock)
            {
                if (plan.Finder is not null)
                {
                    if (string.Equals(plan.Finder, tail, StringComparison.OrdinalIgnoreCase))
                        found = true; // the finder's own lock after its detection
                    continue;
                }
                plan.Finder = tail;
            }
            found = true;
            Fire(plan, TriggerKind.TargetFound, tail, why);
        }
        return found;
    }

    /// <summary>The one-tracker rule: another of the plan's UAVs locking on the target is stopped.</summary>
    private void StopSecondTracker(MissionEventReport report)
    {
        foreach (var plan in RunningPlansOf(report.TailNumber).Where(p => p.Tracks && p.Finder is not null))
        {
            var finder = plan.Finder!;
            _ = Task.Run(async () =>
            {
                missionEvents.MarkStoppedOnPurpose(report.MissionId);
                try
                {
                    await executor.ExecuteAsync("StopMission", new Dictionary<string, JsonElement> { ["tailNumber"] = JsonSerializer.SerializeToElement(report.TailNumber) },
                        plan.Uavs, finder, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Plan {Plan}: stopping {Tail} (a second tracker) failed.", plan.Id, report.TailNumber);
                }
                await notifier.PostAsync(
                    $"{report.TailNumber} also locked on the target, but {finder} is already tracking it; {report.TailNumber} stopped and is holding where it is.",
                    $"Mission plan {plan.Id}: only one UAV tracks the target. UAV {report.TailNumber} locked on it too and was stopped (it holds where " +
                    $"it is); UAV {finder} keeps tracking.",
                    new OperatorVoice($"{report.TailNumber} also found it; {finder} keeps tracking."));
                // Its lock message made it the operator's current UAV; the tracker is the finder.
                await notifier.SetOperatorUavAsync(finder);
            });
        }
    }

    private void FireForFinder(string tail, TriggerKind kind, string why)
    {
        foreach (var plan in RunningPlansOf(tail).Where(p => string.Equals(p.Finder, tail, StringComparison.OrdinalIgnoreCase)))
            Fire(plan, kind, tail, why, once: kind is not (TriggerKind.TargetLost or TriggerKind.TargetRegained));
    }

    private List<MissionPlan> RunningPlansOf(string tail)
    {
        lock (_lock)
            return _plans.Where(p => p.Status == PlanStatus.Running && p.Uavs.Contains(tail, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Runs every waiting step on this trigger (off the caller's thread). With
    /// <paramref name="once"/> false (lost / found again / a pass), a step on it runs each time.</summary>
    private void Fire(MissionPlan plan, TriggerKind kind, string? tail, string why, bool once = true, int value = 0)
    {
        List<PlanStep> due;
        lock (_lock)
        {
            due = plan.Steps.Where(s => s.When.Kind == kind && (kind != TriggerKind.After || s.When.Value == value) &&
                                        (s.State == StepState.Waiting || (!once && s.State is StepState.Done or StepState.Failed))).ToList();
            foreach (var step in due)
                step.State = StepState.Running;
        }
        foreach (var step in due)
            _ = Task.Run(() => RunAsync(plan, step, why));
    }

    private async Task RunIfWaitingAsync(MissionPlan plan, PlanStep step, string why)
    {
        lock (_lock)
        {
            if (plan.Status != PlanStatus.Running || step.State != StepState.Waiting)
                return;
            step.State = StepState.Running;
        }
        await RunAsync(plan, step, why);
    }

    private async Task RunAsync(MissionPlan plan, PlanStep step, string why)
    {
        if (plan.Status != PlanStatus.Running)
        {
            step.State = StepState.Skipped;
            return;
        }
        var action = MissionDescriber.DescribeAction(step.Do, step.Args);
        // Shown and spoken short: which UAVs did what, plus why only when a target event set it off
        // ("the plan started" / "after step 1" says nothing the operator needs).
        var stepTails = (step.Args.TryGetValue("tailNumbers", out var tailList) || step.Args.TryGetValue("tailNumber", out tailList))
            ? StepExecutor.ExpandTails(tailList, plan.Uavs, plan.Finder)
            : [];
        var done = MissionDescriber.DescribeDone(step.Do, step.Args, stepTails);
        var reason = why.StartsWith("the plan started", StringComparison.Ordinal) || why.StartsWith("after step", StringComparison.Ordinal) ? "" : $" ({why})";
        string message, note, spoken;
        try
        {
            // A step that ends a UAV's search (home, elsewhere, stop) does it on purpose: the
            // search's end isn't announced again as "stopped before finishing".
            if (step.Do is "ReturnToLaunch" or "Navigate" or "StopMission" && step.Args.TryGetValue("tailNumber", out var tails))
                foreach (var t in StepExecutor.ExpandTails(tails, plan.Uavs, plan.Finder))
                    if (missionEvents.MissionOf(t) is { } missionId)
                        missionEvents.MarkStoppedOnPurpose(missionId);

            var results = await executor.ExecuteAsync(step.Do, step.Args, plan.Uavs, plan.Finder, CancellationToken.None);
            var failed = results.Where(r => r.Result.StartsWith("Error", StringComparison.OrdinalIgnoreCase)).ToList();
            step.Result = string.Join(" | ", results.Select(r => (r.Tail is null ? "" : r.Tail + ": ") + Shorten(r.Result)));
            step.RanAtUtc = DateTime.UtcNow;
            step.State = failed.Count == 0 ? StepState.Done : StepState.Failed;
            message = failed.Count == 0
                ? $"Step {step.Number}{reason}: {done}."
                : $"Step {step.Number}{reason} failed: {action} - {string.Join("; ", failed.Select(f => (f.Tail is null ? "" : f.Tail + ": ") + Shorten(f.Result)))}";
            spoken = failed.Count == 0 ? $"{done}." : $"Step {step.Number} failed.";
            note = $"Mission plan {plan.Id}, step {step.Number} ran ({why}): {action}. Result: {step.Result}. " +
                   "This was done by the plan - don't do it again.";
        }
        catch (Exception ex)
        {
            step.State = StepState.Failed;
            step.Result = ex.InnerException?.Message ?? ex.Message;
            message = $"Step {step.Number}{reason} failed: {action} - {step.Result}";
            spoken = $"Step {step.Number} failed.";
            note = $"Mission plan {plan.Id}, step {step.Number} failed ({why}): {step.Result}.";
            logger.LogWarning(ex, "Plan {Plan} step {Step} failed.", plan.Id, step.Number);
        }

        logger.LogInformation("Plan {Plan}: {Message}", plan.Id, message);
        await notifier.PostAsync(message, note, new OperatorVoice(spoken));

        if (step.State == StepState.Done)
            Fire(plan, TriggerKind.After, plan.Finder, $"after step {step.Number}", value: step.Number);
        // Finished once nothing is left to run. Not a tracking plan: its one-tracker rule holds for
        // as long as its searches go on, so it runs until cancelled.
        lock (_lock)
        {
            if (plan.Status == PlanStatus.Running && !plan.Tracks &&
                plan.Steps.All(s => s.State is StepState.Done or StepState.Failed or StepState.Skipped) &&
                !plan.Steps.Any(s => s.When.Kind is TriggerKind.TargetLost or TriggerKind.TargetRegained or TriggerKind.PassCompleted))
                plan.Status = PlanStatus.Finished;
        }
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..157] + "...";
}
