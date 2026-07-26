using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

public enum UavCommandError
{
    None,
    VehicleNotFound,
    ValidationFailed
}

public readonly record struct UavCommandResult(bool Success, UavCommandError Error, string? ErrorMessage, TelemetrySnapshot? Snapshot)
{
    public static UavCommandResult Ok(TelemetrySnapshot snapshot) => new(true, UavCommandError.None, null, snapshot);

    public static UavCommandResult NotFound(string tailNumber) =>
        new(false, UavCommandError.VehicleNotFound, $"Unknown UAV tail number '{tailNumber}'.", null);

    public static UavCommandResult Invalid(string message) =>
        new(false, UavCommandError.ValidationFailed, message, null);
}
