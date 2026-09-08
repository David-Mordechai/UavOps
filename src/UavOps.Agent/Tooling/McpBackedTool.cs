using System.Text.Json;
using Microsoft.Extensions.AI;

namespace UavOps.Agent.Tooling;

/// <summary>
/// An <see cref="AIFunction"/> backed by a tool discovered from a connected MCP server
/// (<c>McpClientTool</c>, itself an <see cref="AIFunction"/>). Name/description/schema are the MCP
/// server's own (authored via <c>[Description]</c> in that server's project, not YAML - see
/// <c>Agents/BrainAgent.yaml</c>'s <c>mcpServers:</c> section) — this class adds only what
/// every tool call needs regardless of where it executes: confirmation gating, structured
/// logging/tracing, and buffering a "yaml" result field into this turn's final chat reply (see
/// <see cref="BufferYamlSnippetIfPresent"/>) so it lands in the same bubble as the model's own
/// answer. <see cref="ConfirmationGate"/> and <see cref="ToolInvocationLogger"/> only ever see a
/// generic tool-call shape, unaware of and unaffected by where a tool actually executes.
/// </summary>
public sealed class McpBackedTool : AIFunction
{
    private readonly AIFunction _mcpTool;
    private readonly bool _requiresConfirmation;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly ConfirmationGate _confirmationGate;
    private readonly string _agentName;
    private readonly string _correlationId;

    public McpBackedTool(AIFunction mcpTool, bool requiresConfirmation, ToolInvocationLogger toolLogger,
        ConfirmationGate confirmationGate, string agentName, string correlationId)
    {
        _mcpTool = mcpTool;
        _requiresConfirmation = requiresConfirmation;
        _toolLogger = toolLogger;
        _confirmationGate = confirmationGate;
        _agentName = agentName;
        _correlationId = correlationId;
    }

    public override string Name => _mcpTool.Name;
    public override string Description => _mcpTool.Description;
    public override JsonElement JsonSchema => _mcpTool.JsonSchema;

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var argDict = new Dictionary<string, object?>();
        foreach (var kvp in arguments)
        {
            argDict[kvp.Key] = kvp.Value;
        }

        if (_requiresConfirmation && _confirmationGate.CurrentMode == Options.ExecutionMode.Confirm)
        {
            var approved = await _confirmationGate.RequireConfirmationAsync(_correlationId, _agentName, Name, Description, argDict, cancellationToken);
            if (!approved)
            {
                return "Not executed: operator declined (or did not respond to) the confirmation request.";
            }
        }

        return await _toolLogger.LogAsync(
            _correlationId,
            _agentName,
            Name,
            argDict,
            () => _mcpTool.InvokeAsync(arguments, cancellationToken).AsTask(),
            result =>
            {
                var text = result?.ToString() ?? "";
                BufferYamlSnippetIfPresent(text);
                return text;
            });
    }

    /// <summary>If the result includes a top-level "yaml" string property, buffers it to be
    /// folded into this turn's final chat reply — a generic, reusable convention (not specific to
    /// any one domain) so operations whose output an operator needs to visually verify (e.g.
    /// <c>IWatchdogConfigService.AddConfiguredService</c>/<c>UpdateConfiguredService</c>) don't
    /// depend on a small model reliably choosing to relay it verbatim in its own final reply - the
    /// same "don't trust the model with something that must be reliable" reasoning already behind
    /// <c>ChatConfirmationParser</c>'s fixed vocabulary and the proactive lesson-outcome
    /// notifications in <c>UavOps.Agent.McpSimulator.SimulatorLessonJobProcessor</c>. Buffered
    /// rather than sent immediately so it lands in the same chat bubble as the model's own final
    /// answer instead of a separate one — see <see cref="ToolInvocationLogger.BufferProactiveMessage"/>.
    /// A plain non-JSON error string (e.g. "Error: ...") simply fails to parse and is ignored here,
    /// same as before.</summary>
    private void BufferYamlSnippetIfPresent(string resultText)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(resultText);
        }
        catch (JsonException)
        {
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("yaml", out var yamlProp)
                || yamlProp.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var snippet = yamlProp.GetString();
            if (string.IsNullOrWhiteSpace(snippet))
            {
                return;
            }

            var message = $"Here's the resulting configuration:\n\n```yaml\n{snippet.TrimEnd()}\n```";
            _toolLogger.BufferProactiveMessage(_correlationId, message);
        }
    }
}
