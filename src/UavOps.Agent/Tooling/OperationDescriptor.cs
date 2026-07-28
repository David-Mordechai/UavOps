using System.Reflection;

namespace UavOps.Agent.Tooling;

/// <summary>One parameter of an operation — the mechanical shape, never shown to the LLM directly.</summary>
public sealed record OperationParameterDescriptor(string Name, Type ClrType);

/// <summary>The mechanical contract for one operation, reflected from <see
/// cref="UavOps.Agent.Operations.IOperationService"/> by <see cref="OperationCatalog"/>.</summary>
public sealed record OperationDescriptor(string Operation, MethodInfo Method, IReadOnlyList<OperationParameterDescriptor> Parameters);
