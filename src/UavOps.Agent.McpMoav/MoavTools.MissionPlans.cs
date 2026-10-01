using System.Text.Json;
using UavOps.Agent.McpMoav.Missions;

namespace UavOps.Agent.McpMoav;

// Mission plans: the operator's whole mission as steps ("when X, do Y"), checked and shown first,
// then run by MissionEngine as things happen. See Missions/MissionEngine.cs.
public static partial class MoavTools
{
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<string> CreateMissionPlan(MissionEngine engine, MissionStepInput[] steps, CancellationToken cancellationToken)
    {
        var (plan, errors) = await engine.CreateAsync(steps ?? [], cancellationToken);
        if (plan is null)
            return "Error: The plan wasn't created - nothing will run. Fix these and call CreateMissionPlan again (or ask the operator):\n - " +
                   string.Join("\n - ", errors);
        var described = MissionDescriber.Describe(plan);
        return JsonSerializer.Serialize(new
        {
            planId = plan.Id,
            uavs = plan.Uavs,
            steps = described,
            started = false,
            nextStep = $"Show the operator these steps exactly as written:\n{described}\n" +
                       $"Nothing has been done yet. Don't ask whether to start: when the operator says to start, call StartMissionPlan once " +
                       $"with planId '{plan.Id}' (not StartMission - the plan's own steps start the UAVs)."
        }, PlanJson);
    }

    public static string StartMissionPlan(MissionEngine engine, string? planId = null)
    {
        if (engine.Find(planId) is not { } plan)
            return planId is null ? "Error: There is no mission plan to start. Create one with CreateMissionPlan first." : $"Error: There is no mission plan '{planId}'.";
        var problem = engine.Start(plan);
        if (problem.Length > 0)
            return "Error: " + problem;
        return JsonSerializer.Serialize(new
        {
            planId = plan.Id,
            started = true,
            nextStep = "Tell the operator the mission has started. Its steps run by themselves as things happen, and each one is reported " +
                       "to the operator when it runs - don't call the steps' tools yourself."
        }, PlanJson);
    }

    public static string GetMissionPlan(MissionEngine engine, string? planId = null)
    {
        if (engine.Find(planId) is not { } plan)
            return "There is no mission plan.";
        return JsonSerializer.Serialize(new
        {
            planId = plan.Id,
            status = plan.Status.ToString(),
            uavs = plan.Uavs,
            finder = plan.Finder,
            steps = plan.Steps.Select(s => new
            {
                number = s.Number,
                text = $"{s.When.Describe()}: {MissionDescriber.DescribeAction(s.Do, s.Args)}",
                state = s.State.ToString(),
                result = s.Result
            })
        }, PlanJson);
    }

    public static string CancelMissionPlan(MissionEngine engine, string? planId = null)
    {
        if (engine.Find(planId) is not { } plan)
            return "Error: There is no mission plan to cancel.";
        engine.Cancel(plan);
        return JsonSerializer.Serialize(new
        {
            planId = plan.Id,
            cancelled = true,
            nextStep = "Tell the operator the plan's remaining steps won't run. The UAVs keep doing what they're doing now - to stop one, " +
                       "call StopMission; to bring them home, ReturnToLaunch."
        }, PlanJson);
    }
}
