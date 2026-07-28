namespace UavOps.Agent.Tooling;

/// <summary>
/// Reflects over <see cref="UavOps.Agent.Operations.IOperationService"/>'s methods once at startup
/// to build the mechanical contract (parameter names + CLR types) for each operation — replaces
/// the old OpenAPI-spec-based catalog. No network call, no remotely-fetched document: the
/// interface itself is the single source of truth. Add a 13th operation by adding one method to
/// <see cref="UavOps.Agent.Operations.IOperationService"/>; nothing else needs to change here.
/// </summary>
public sealed class OperationCatalog
{
    private readonly Dictionary<string, OperationDescriptor> _operations;

    public OperationCatalog(Type serviceType)
    {
        _operations = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);

        foreach (var method in serviceType.GetMethods())
        {
            var parameters = method.GetParameters()
                .Where(p => p.ParameterType != typeof(CancellationToken))
                .Select(p => new OperationParameterDescriptor(p.Name!, p.ParameterType))
                .ToList();

            _operations[method.Name] = new OperationDescriptor(method.Name, method, parameters);
        }
    }

    public IReadOnlyDictionary<string, OperationDescriptor> Operations => _operations;

    public bool TryResolve(string operation, out OperationDescriptor? descriptor) =>
        _operations.TryGetValue(operation, out descriptor);
}
