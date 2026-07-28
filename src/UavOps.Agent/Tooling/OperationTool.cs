using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using UavOps.Agent.Operations;
using UavOps.Agent.Options;

namespace UavOps.Agent.Tooling;

/// <summary>
/// An AIFunction backed by one <see cref="IOperationService"/> method. The name, description,
/// and every parameter description shown to the LLM come from appsettings/YAML
/// (<see cref="AgentToolConfig"/>); the reflected <see cref="OperationDescriptor"/> supplies
/// only the mechanical parameter shape (names/CLR types). Every invocation goes through the
/// confirmation gate (only when this tool opts in via <see cref="AgentToolConfig.RequiresConfirmation"/>
/// and mode-dependent) and is logged. Calls the operation in-process via reflection
/// (<see cref="OperationDescriptor.Method"/>) rather than an HTTP invoker — there is no
/// separate invoker class, since there is no network hop to make.
/// </summary>
public sealed class OperationTool : AIFunction
{
    private static readonly JsonSerializerOptions ArgumentDeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    // Result DTOs (TelemetrySnapshot etc.) are plain PascalCase C# records — camelCase here so
    // what the model reads back ("speedKts", "altitudeFt") matches the casing its own tool-call
    // arguments and the JSON schema already use, instead of default PascalCase.
    private static readonly JsonSerializerOptions ResultSerializeOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly OperationDescriptor _descriptor;
    private readonly AgentToolConfig _config;
    private readonly IOperationService _operationService;
    private readonly ToolInvocationLogger _toolLogger;
    private readonly ConfirmationGate _confirmationGate;
    private readonly string _agentName;
    private readonly string _correlationId;

    public OperationTool(
        OperationDescriptor descriptor,
        AgentToolConfig config,
        IOperationService operationService,
        ToolInvocationLogger toolLogger,
        ConfirmationGate confirmationGate,
        string agentName,
        string correlationId)
    {
        _descriptor = descriptor;
        _config = config;
        _operationService = operationService;
        _toolLogger = toolLogger;
        _confirmationGate = confirmationGate;
        _agentName = agentName;
        _correlationId = correlationId;
        JsonSchema = BuildSchema(descriptor, config);
    }

    public override string Name => _config.Operation;
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

        if (_config.RequiresConfirmation && _confirmationGate.CurrentMode == ExecutionMode.Confirm)
        {
            var approved = await _confirmationGate.RequireConfirmationAsync(_correlationId, _agentName, _config.Operation, _config.Description, argDict, cancellationToken);
            if (!approved)
            {
                return "Not executed: operator declined (or did not respond to) the confirmation request.";
            }
        }

        return await _toolLogger.LogAsync(
            _correlationId,
            _agentName,
            _config.Operation,
            argDict,
            () => InvokeOperationAsync(argDict, cancellationToken),
            text => text);
    }

    private async Task<string> InvokeOperationAsync(Dictionary<string, object?> argDict, CancellationToken cancellationToken)
    {
        var methodParams = _descriptor.Method.GetParameters();
        var methodArgs = new object?[methodParams.Length];

        for (var i = 0; i < methodParams.Length; i++)
        {
            var p = methodParams[i];
            methodArgs[i] = p.ParameterType == typeof(CancellationToken)
                ? cancellationToken
                : ConvertArgument(argDict.GetValueOrDefault(p.Name!), p.ParameterType);
        }

        var task = (Task<OperationResult>)_descriptor.Method.Invoke(_operationService, methodArgs)!;
        var result = await task;

        return result.Success ? JsonSerializer.Serialize(result.Value, ResultSerializeOptions) : $"Error: {result.ErrorMessage}";
    }

    private static object? ConvertArgument(object? raw, Type targetType)
    {
        if (raw is null)
        {
            return null;
        }

        if (targetType.IsInstanceOfType(raw))
        {
            return raw;
        }

        if (raw is JsonElement element)
        {
            return element.Deserialize(targetType, ArgumentDeserializeOptions);
        }

        if (raw is string s && targetType != typeof(string))
        {
            return Convert.ChangeType(s, targetType, CultureInfo.InvariantCulture);
        }

        return raw;
    }

    private static JsonElement BuildSchema(OperationDescriptor descriptor, AgentToolConfig config)
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
            properties[p.Name] = SchemaNode(p.ClrType, description);
            required.Add(p.Name); // every IOperationService parameter is non-optional today
        }

        var root = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject SchemaNode(Type clrType, string? description)
    {
        var node = new JsonObject { ["type"] = JsonTypeFor(clrType) };

        if (description is not null)
        {
            node["description"] = description;
        }

        if (TryGetListItemType(clrType, out var itemType))
        {
            node["items"] = SchemaNode(itemType, null);
        }
        else if (!IsSimpleType(clrType))
        {
            var props = new JsonObject();
            foreach (var prop in clrType.GetProperties())
            {
                props[ToCamelCase(prop.Name)] = SchemaNode(prop.PropertyType, null);
            }
            node["properties"] = props;
        }

        return node;
    }

    private static string JsonTypeFor(Type clrType)
    {
        if (clrType == typeof(string)) return "string";
        if (clrType == typeof(int) || clrType == typeof(long)) return "integer";
        if (clrType == typeof(double) || clrType == typeof(float) || clrType == typeof(decimal)) return "number";
        if (clrType == typeof(bool)) return "boolean";
        if (TryGetListItemType(clrType, out _)) return "array";
        return "object";
    }

    private static bool IsSimpleType(Type clrType) =>
        clrType == typeof(string) || clrType.IsPrimitive || clrType == typeof(decimal);

    private static bool TryGetListItemType(Type clrType, out Type itemType)
    {
        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>))
        {
            itemType = clrType.GetGenericArguments()[0];
            return true;
        }

        itemType = typeof(object);
        return false;
    }

    private static string ToCamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
