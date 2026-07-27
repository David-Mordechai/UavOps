using UavOps.ControlApi.Hubs;
using UavOps.ControlApi.Models;

namespace UavOps.ControlApi.Services;

/// <summary>
/// <see cref="IGdtService"/> backed by <see cref="IUavCommandBroker"/> — see
/// <see cref="SignalRUavFleetService"/> for the blocking-call and error-mapping rationale, which
/// applies identically here.
/// </summary>
public sealed class SignalRGdtService(IUavCommandBroker broker, ILogger<SignalRGdtService> logger) : IGdtService
{
    public (bool Found, GdtLinkStatus? Status) GetLinkStatus(string tailNumber)
    {
        var result = Send((proxy, correlationId) => proxy.GetLinkStatus(correlationId, tailNumber));
        if (!result.Success)
        {
            LogFailure(nameof(GetLinkStatus), tailNumber, result);
            return (false, null);
        }

        return (true, result.Value);
    }

    public (bool Found, GdtLinkStatus? Status, string? Error) SetTrackingMode(string tailNumber, string mode)
    {
        var result = Send((proxy, correlationId) => proxy.SetTrackingMode(correlationId, tailNumber, mode));
        if (!result.Success)
        {
            LogFailure(nameof(SetTrackingMode), tailNumber, result);
            return (true, null, result.ErrorMessage ?? "Fleet command client did not respond.");
        }

        return (true, result.Value, null);
    }

    private BrokerResult<GdtLinkStatus> Send(Func<IUavCommandClientProxy, string, Task> invoke) =>
        broker.SendAsync<GdtLinkStatus>(invoke, CancellationToken.None).GetAwaiter().GetResult();

    private void LogFailure(string operation, string tailNumber, BrokerResult<GdtLinkStatus> result) =>
        logger.LogWarning(
            "{Operation}({TailNumber}) failed via the fleet command bridge: {Error} {Message}",
            operation, tailNumber, result.Error, result.ErrorMessage);
}
