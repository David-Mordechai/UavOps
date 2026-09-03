using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>One planned delegation step - <paramref name="Agent"/> must be one of BrainAgent's real
/// available delegates, <paramref name="Instruction"/> the fully explicit instruction to give it.</summary>
public sealed record PlanStep(string Agent, string Instruction);

/// <summary>
/// BrainAgent's only tool. Replaces giving it each of its children (<c>MoavAgent</c>,
/// <c>SimulatorAgent</c>, <c>MaintenanceAgent</c>) as independently-callable tools of their own -
/// this is the direct, structural fix for BrainAgent itself answering a new, actionable message with
/// a confident-sounding claim while delegating nothing at all, observed directly several turns into a
/// long session once it had real memory of its own prior successful delegations to pattern-complete
/// against (the original stateless-first incident, resurfacing one level up - see
/// <see cref="RequireToolOnFirstTurnChatClient"/>'s own doc comment for the full history).
///
/// With every one of BrainAgent's children callable directly, "should I actually delegate, or just
/// say it happened" was a free choice available on every single turn. Collapsing that choice into
/// one tool removes it entirely: <see cref="RequireToolOnFirstTurnChatClient"/> forces BrainAgent to
/// call *this* tool - and only this tool - on the first completion of every new message, and once
/// called, this tool's own code (not another model decision) walks every step in order and actually
/// invokes the named delegate, for real, every time. A plan with zero steps is a completely valid,
/// forceable response to a purely conversational message (a greeting, a recall question) - so this
/// doesn't conflict with BrainAgent's legitimate need to sometimes act on nothing at all, unlike
/// forcing a real leaf-style tool call would. BrainAgent's subsequent completion (now free to answer
/// in plain text, per <see cref="RequireToolOnFirstTurnChatClient"/>) is grounded in this tool's own
/// real per-step results, not something it has to separately remember to double-check.
///
/// Steps run sequentially, not concurrently - same reasoning already established for the "all UAVs"
/// fan-out (<c>TailNumberDisambiguationTool.InvokeForAllUavsAsync</c>'s own doc comment): keeps a
/// <c>requiresConfirmation</c> step's own confirmation prompt serialized through
/// <c>ConfirmationGate</c>'s single turnstile in a predictable order, and avoids compounding
/// concurrency-related model reliability variance across multiple steps of one plan.
/// </summary>
public sealed class CreatePlanTool : AIFunction
{
    private const string StepsParameterName = "steps";
    private const string AgentPropertyName = "agent";
    private const string InstructionPropertyName = "instruction";

    private readonly IReadOnlyDictionary<string, AIFunction> _delegateToolsByAgent;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly string _agentName;
    private readonly string _correlationId;

    public CreatePlanTool(IReadOnlyDictionary<string, AIFunction> delegateToolsByAgent, ToolInvocationLogger toolLogger,
        string agentName, string correlationId)
    {
        _delegateToolsByAgent = delegateToolsByAgent;
        _toolLogger = toolLogger;
        _agentName = agentName;
        _correlationId = correlationId;
        JsonSchema = BuildSchema(delegateToolsByAgent);
    }

    public override string Name => "CreatePlan";

    public override string Description =>
        "Build and immediately execute a plan for the operator's request: an ordered list of delegation steps, " +
        "each naming one of your available agents and the fully explicit instruction to give it. Use an empty " +
        "list when nothing needs delegating (e.g. a greeting, or a question you can answer from conversation " +
        "history alone). You must call this once for every message before replying - your reply must be based " +
        "on what this tool actually reports back, never written before or instead of calling it.";

    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var steps = ExtractSteps(arguments);

        return await _toolLogger.LogAsync(
            _correlationId,
            _agentName,
            Name,
            new { steps },
            async () =>
            {
                if (steps.Count == 0)
                {
                    return "No delegation needed for this request.";
                }

                var results = new List<string>();
                foreach (var step in steps)
                {
                    if (!_delegateToolsByAgent.TryGetValue(step.Agent, out var delegateTool))
                    {
                        results.Add($"{step.Agent}: NOT EXECUTED - unknown agent. Available agents are: " +
                                    string.Join(", ", _delegateToolsByAgent.Keys) + ".");
                        continue;
                    }

                    var stepArgs = new AIFunctionArguments(new Dictionary<string, object?> { ["instruction"] = step.Instruction });
                    var result = await delegateTool.InvokeAsync(stepArgs, cancellationToken);
                    results.Add($"{step.Agent} (\"{step.Instruction}\"): {result}");
                }

                return "Plan executed. Per-step results, in order:\n" + string.Join("\n", results);
            },
            text => text);
    }

    private static List<PlanStep> ExtractSteps(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(StepsParameterName, out var raw) || raw is not JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            return [];
        }

        var steps = new List<PlanStep>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var agent = item.TryGetProperty(AgentPropertyName, out var agentEl) ? agentEl.GetString() : null;
            var instruction = item.TryGetProperty(InstructionPropertyName, out var instructionEl) ? instructionEl.GetString() : null;

            if (!string.IsNullOrWhiteSpace(agent) && !string.IsNullOrWhiteSpace(instruction))
            {
                steps.Add(new PlanStep(agent, instruction));
            }
        }

        return steps;
    }

    private static JsonElement BuildSchema(IReadOnlyDictionary<string, AIFunction> delegateToolsByAgent)
    {
        // Each child was previously its own independently-callable tool, so the model saw its full
        // config-authored Description (see AgentConfig.Description / DelegateAgentTool) right next to
        // its name when choosing where to delegate. Collapsing every child into this one CreatePlan
        // tool must not lose that signal - a bare list of agent names with no behavioral description
        // was tried first and observed live to misroute even simple, unambiguous requests (e.g. "fly
        // UAV-1 to target alpha" going to the simulator agent) - so each agent's real description is
        // repeated here, exactly as it is when the agent is offered as its own tool.
        var agentList = string.Join(" | ", delegateToolsByAgent.Select(kv => $"{kv.Key}: {kv.Value.Description}"));

        var stepNode = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [AgentPropertyName] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"Which of your available agents handles this step - choose based on what each one actually does. Must be exactly one of these (name: what it handles): {agentList}."
                },
                [InstructionPropertyName] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The fully explicit instruction for that agent - name every place, target, tail number, and " +
                        "value directly, resolved from the operator's request and conversation history as needed. Never a pronoun " +
                        "or vague reference - the agent receiving it sees only this text, not the rest of the conversation."
                }
            },
            ["required"] = new JsonArray(AgentPropertyName, InstructionPropertyName)
        };

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                [StepsParameterName] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = stepNode,
                    ["description"] = "The ordered list of delegation steps needed to fulfill the operator's request. " +
                        "Empty if nothing needs to be delegated."
                }
            },
            ["required"] = new JsonArray(StepsParameterName)
        };

        return JsonSerializer.SerializeToElement(root);
    }
}
