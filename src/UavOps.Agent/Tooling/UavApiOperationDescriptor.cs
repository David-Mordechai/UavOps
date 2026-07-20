using Microsoft.OpenApi.Models;

namespace UavOps.Agent.Tooling;

public enum ParamLocation
{
    Path,
    Query,
    BodyProperty
}

/// <summary>The mechanical shape of one OpenAPI operation parameter (or flattened request-body property) — never shown to the LLM directly.</summary>
public sealed record UavApiParameterDescriptor(string Name, ParamLocation Location, bool Required, OpenApiSchema Schema);

/// <summary>The mechanical HTTP contract for one OpenAPI operation, parsed from the UAV app's spec.</summary>
public sealed record UavApiOperationDescriptor(string OperationId, OperationType HttpMethod, string Path, IReadOnlyList<UavApiParameterDescriptor> Parameters);

/// <summary>
/// Parses the UAV app's OpenAPI document and exposes each operation's mechanical contract
/// (verb, path, parameter names/types/required-ness) by operationId. This is deliberately the
/// *only* thing read from the spec — tool/parameter descriptions shown to the LLM always come
/// from appsettings.json (see <see cref="AgentToolFactory"/>).
/// </summary>
public sealed class OpenApiToolCatalog
{
    private readonly Dictionary<string, UavApiOperationDescriptor> _operations;

    public OpenApiToolCatalog(OpenApiDocument document)
    {
        _operations = new Dictionary<string, UavApiOperationDescriptor>(StringComparer.Ordinal);

        foreach (var (path, pathItem) in document.Paths)
        {
            foreach (var (method, operation) in pathItem.Operations)
            {
                if (string.IsNullOrWhiteSpace(operation.OperationId))
                {
                    continue;
                }

                var parameters = new List<UavApiParameterDescriptor>();

                foreach (var p in operation.Parameters ?? [])
                {
                    var location = p.In switch
                    {
                        Microsoft.OpenApi.Models.ParameterLocation.Path => ParamLocation.Path,
                        _ => ParamLocation.Query
                    };
                    parameters.Add(new UavApiParameterDescriptor(p.Name, location, p.Required, p.Schema));
                }

                if (operation.RequestBody?.Content is { } content &&
                    content.TryGetValue("application/json", out var media) &&
                    media.Schema is { Properties.Count: > 0 } bodySchema)
                {
                    var required = bodySchema.Required ?? new HashSet<string>();
                    foreach (var (propName, propSchema) in bodySchema.Properties)
                    {
                        parameters.Add(new UavApiParameterDescriptor(propName, ParamLocation.BodyProperty, required.Contains(propName), propSchema));
                    }
                }

                _operations[operation.OperationId] = new UavApiOperationDescriptor(operation.OperationId, method, path, parameters);
            }
        }
    }

    public IReadOnlyDictionary<string, UavApiOperationDescriptor> Operations => _operations;

    public bool TryResolve(string operationId, out UavApiOperationDescriptor? descriptor) =>
        _operations.TryGetValue(operationId, out descriptor);
}
