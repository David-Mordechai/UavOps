using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.OpenApi.Models;
using UavOps.Agent.Options;

namespace UavOps.Agent.Tooling;

/// <summary>
/// An AIFunction backed by one OpenAPI operation on the UAV app. The name, description, and
/// every parameter description shown to the LLM come from appsettings (<see cref="AgentToolConfig"/>);
/// the OpenAPI descriptor supplies only the mechanical HTTP contract. Every invocation goes
/// through the confirmation gate (for mutating operations, mode-dependent) and is logged.
/// </summary>
public sealed class UavApiOperationTool : AIFunction
{
    private readonly UavApiOperationDescriptor _descriptor;
    private readonly AgentToolConfig _config;
    private readonly UavApiToolInvoker _invoker;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly ConfirmationGate _confirmationGate;
    private readonly string _agentName;
    private readonly string _correlationId;

    public UavApiOperationTool(
        UavApiOperationDescriptor descriptor,
        AgentToolConfig config,
        UavApiToolInvoker invoker,
        ToolInvocationLogger toolLogger,
        ConfirmationGate confirmationGate,
        string agentName,
        string correlationId)
    {
        _descriptor = descriptor;
        _config = config;
        _invoker = invoker;
        _toolLogger = toolLogger;
        _confirmationGate = confirmationGate;
        _agentName = agentName;
        _correlationId = correlationId;
        JsonSchema = BuildSchema(descriptor, config);
    }

    public override string Name => _config.OperationId;
    public override string Description => _config.Description;
    public override JsonElement JsonSchema { get; }

    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var argDict = new Dictionary<string, object?>();
        foreach (var kvp in arguments)
        {
            argDict[kvp.Key] = kvp.Value;
        }

        foreach (var (name, value) in _config.FixedParameters)
        {
            argDict[name] = value;
        }

        if (ConfirmationGate.IsMutating(_descriptor.HttpMethod) && _confirmationGate.CurrentMode == ExecutionMode.Confirm)
        {
            var approved = await _confirmationGate.RequireConfirmationAsync(_correlationId, _agentName, _config.OperationId, argDict, cancellationToken);
            if (!approved)
            {
                return "Not executed: operator declined (or did not respond to) the confirmation request.";
            }
        }

        var result = await _toolLogger.LogAsync(
            _correlationId,
            _agentName,
            _config.OperationId,
            argDict,
            () => _invoker.InvokeAsync(_descriptor, argDict, cancellationToken),
            r => $"HTTP {r.StatusCode} {r.Body}");

        return $"HTTP {result.StatusCode}: {result.Body}";
    }

    private static JsonElement BuildSchema(UavApiOperationDescriptor descriptor, AgentToolConfig config)
    {
        var properties = new JsonObject();
        var required = new JsonArray();

        foreach (var p in descriptor.Parameters)
        {
            if (config.FixedParameters.ContainsKey(p.Name))
            {
                continue; // fixed parameters are sent on every call but never shown to the model
            }

            var description = config.Parameters.TryGetValue(p.Name, out var d) ? d : null;
            properties[p.Name] = SchemaNode(p.Schema, description);
            if (p.Required)
            {
                required.Add(p.Name);
            }
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject SchemaNode(OpenApiSchema schema, string? description)
    {
        var node = new JsonObject { ["type"] = schema.Type ?? "string" };

        if (description is not null)
        {
            node["description"] = description;
        }

        if (schema.Type == "array" && schema.Items is not null)
        {
            node["items"] = SchemaNode(schema.Items, null);
        }

        if (schema.Type == "object" && schema.Properties is { Count: > 0 })
        {
            var props = new JsonObject();
            foreach (var (name, propSchema) in schema.Properties)
            {
                props[name] = SchemaNode(propSchema, null);
            }
            node["properties"] = props;
        }

        return node;
    }
}
