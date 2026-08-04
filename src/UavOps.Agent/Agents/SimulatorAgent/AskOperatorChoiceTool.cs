using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using UavOps.Agent.Options;
using UavOps.Agent.Tooling;

namespace UavOps.Agent.Agents.SimulatorAgent;

/// <summary>
/// Hand-built AIFunction for a <c>kind: OperatorPrompt</c> tool config entry — the ask-the-
/// operator-and-wait counterpart to <see cref="DelegateAgentTool"/>. Doesn't fit the
/// IOperationService/OperationCatalog reflection pattern (its whole job is to prompt and wait for
/// a chat reply, not to be a data operation with a mechanical parameter shape reflected off an
/// interface), so it's built directly from <see cref="AgentToolConfig"/> instead of resolved via
/// an <see cref="Tooling.OperationCatalog"/> — same reasoning <see cref="DelegateAgentTool"/>
/// already establishes for "a tool whose semantics don't fit the operation-reflection pattern."
/// Only ever used by <c>SimulatorInfrastructureAgent</c> today (the only agent with a
/// <c>kind: OperatorPrompt</c> tool), hence living under <c>Agents/SimulatorAgent/</c>.
/// </summary>
public sealed class AskOperatorChoiceTool : AIFunction
{
    private const string ChoicesParameterName = "lessons";

    private readonly OperatorPromptGate _promptGate;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly string _agentName;
    private readonly string _correlationId;
    private readonly string _operatorText;

    public AskOperatorChoiceTool(AgentToolConfig config, OperatorPromptGate promptGate, ToolInvocationLogger toolLogger,
        string agentName, string correlationId, string operatorText)
    {
        Name = config.Operation;
        Description = config.Description;
        _promptGate = promptGate;
        _toolLogger = toolLogger;
        _agentName = agentName;
        _correlationId = correlationId;
        _operatorText = operatorText;
        JsonSchema = BuildSchema(config);
    }

    public override string Name { get; }
    public override string Description { get; }
    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var choices = ExtractChoices(arguments);

        return await _toolLogger.LogAsync(
            _correlationId,
            _agentName,
            Name,
            new { lessons = choices },
            async () =>
            {
                if (choices.Count == 0)
                {
                    return "No choices were offered — cannot ask the operator anything.";
                }

                // Deterministic shortcut, not model discretion: relying on the model to notice
                // "the operator already named it" and skip asking proved unreliable in practice
                // (same class of problem this codebase already solves elsewhere with fixed
                // vocabularies instead of LLM judgment — see ChatConfirmationParser). Only
                // auto-resolves on exactly one match; zero or multiple matches falls through to
                // the real prompt, which is the safe default for anything ambiguous.
                var autoMatch = TryAutoResolve(choices, _operatorText);
                if (autoMatch is not null)
                {
                    return autoMatch;
                }

                var choice = await _promptGate.RequestChoiceAsync(_correlationId, _agentName, Description, choices, cancellationToken);
                return choice ?? "Not answered: operator did not choose (or did not respond) within the time limit.";
            },
            text => text);
    }

    private static string? TryAutoResolve(List<string> choices, string operatorText)
    {
        var matches = choices
            .Where(choice =>
                operatorText.Contains(choice, StringComparison.OrdinalIgnoreCase) ||
                operatorText.Contains(Path.GetFileNameWithoutExtension(choice), StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToList();

        return matches.Count == 1 ? matches[0] : null;
    }

    private static List<string> ExtractChoices(AIFunctionArguments arguments)
    {
        if (!arguments.TryGetValue(ChoicesParameterName, out var raw) || raw is null)
        {
            return [];
        }

        if (raw is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }

        if (raw is IEnumerable<object?> list)
        {
            return list.Select(v => v?.ToString() ?? "").Where(s => s.Length > 0).ToList();
        }

        return [];
    }

    private static JsonElement BuildSchema(AgentToolConfig config)
    {
        var description = config.Parameters.TryGetValue(ChoicesParameterName, out var d) ? d : null;

        var itemsNode = new JsonObject { ["type"] = "string" };
        var arrayNode = new JsonObject { ["type"] = "array", ["items"] = itemsNode };
        if (description is not null)
        {
            arrayNode["description"] = description;
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { [ChoicesParameterName] = arrayNode },
            ["required"] = new JsonArray(ChoicesParameterName)
        };

        return JsonSerializer.SerializeToElement(root);
    }
}
