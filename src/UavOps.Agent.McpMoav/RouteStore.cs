using System.Collections.Concurrent;
using UavOps.Agent.Mission;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// The latest planned search route per UAV, so UploadRoute and SetSearchTarget can find it by
/// tail number alone: the model never has to copy a route id or a waypoint list between calls.
/// In memory only; a restart forgets unuploaded plans.
/// </summary>
public interface IRouteStore
{
    void Save(SearchRoute route);
    SearchRoute? Get(string tailNumber);
}

public sealed class InMemoryRouteStore : IRouteStore
{
    private readonly ConcurrentDictionary<string, SearchRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    public void Save(SearchRoute route) => _routes[route.TailNumber] = route;

    public SearchRoute? Get(string tailNumber) => _routes.TryGetValue(tailNumber, out var route) ? route : null;
}
