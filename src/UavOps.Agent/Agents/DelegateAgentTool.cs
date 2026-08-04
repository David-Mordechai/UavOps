using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents;

/// <summary>
/// Exposes a domain <see cref="AIAgent"/> as a callable tool of its parent agent — the agent-as-
/// tool pattern used instead of a hardcoded router switch statement. Any agent (not just the
/// root) can hold these: the parent decides which specialist(s) to invoke by calling these like
/// any other tool; each call is logged like any other tool call, attributed to the actual parent
/// (<see cref="_parentAgentName"/>), not a fixed name.
/// </summary>
public sealed class DelegateAgentTool : AIFunction
{
    private readonly AIAgent _subAgent;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly string _parentAgentName;
    private readonly string _correlationId;

    public DelegateAgentTool(string name, string description, AIAgent subAgent, ToolInvocationLogger toolLogger, string parentAgentName, string correlationId)
    {
        Name = name;
        Description = description;
        _subAgent = subAgent;
        _toolLogger = toolLogger;
        _parentAgentName = parentAgentName;
        _correlationId = correlationId;
        JsonSchema = BuildSchema();
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        arguments.TryGetValue("instruction", out var instructionObj);
        var instruction = instructionObj?.ToString() ?? "";

        return await _toolLogger.LogAsync(
            _correlationId,
            _parentAgentName,
            Name,
            new { instruction },
            async () =>
            {
                var response = await _subAgent.RunAsync(instruction, cancellationToken: cancellationToken);
                return response.Text;
            },
            text => text);
    }

    private static JsonElement BuildSchema()
    {
        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["instruction"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "The operator's request (or the relevant part of it) to hand to this specialist agent."
                }
            },
            ["required"] = new JsonArray("instruction")
        };

        return JsonSerializer.SerializeToElement(root);
    }
}
