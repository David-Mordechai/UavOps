namespace UavOps.Agent.Operations;

public enum OperationError
{
    None,
    VehicleNotFound,
    ValidationFailed,
    NoClientConnected,
    Timeout,
    ClientReportedError
}

/// <summary>
/// The uniform outcome of any <see cref="IOperationService"/> call. Non-generic (<see
/// cref="Value"/> is boxed) so <c>Tooling/OperationTool.cs</c> can invoke any of the 12 operations
/// via plain reflection — no <c>dynamic</c>, no per-operation switch. Each concrete
/// implementation (<c>Simulation/SimulatedUavOperationService</c>,
/// <c>Operations/Remote/RemoteOperationService</c>) stays fully typed internally and only boxes
/// to <see cref="object"/> at the return statement.
/// </summary>
public readonly record struct OperationResult(bool Success, OperationError Error, string? ErrorMessage, object? Value)
{
    public static OperationResult Ok(object? value) => new(true, OperationError.None, null, value);

    public static OperationResult NotFound(string tailNumber) =>
        new(false, OperationError.VehicleNotFound, $"Unknown UAV tail number '{tailNumber}'.", null);

    public static OperationResult Invalid(string message) =>
        new(false, OperationError.ValidationFailed, message, null);

    public static OperationResult Fail(OperationError error, string? errorMessage) =>
        new(false, error, errorMessage, null);
}
