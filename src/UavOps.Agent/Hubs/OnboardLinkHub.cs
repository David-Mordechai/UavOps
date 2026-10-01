using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using UavOps.Onboard.Contracts;

namespace UavOps.Agent.Hubs;

/// <summary>
/// Routes messages between aircraft (the fleet app / simulator) and their onboard computers
/// (<see cref="OnboardLink"/>). Purely a router: a message is (tail, kind, JSON payload), and the
/// host never looks inside - what a task, a detection or a payload command means is decided at the
/// two ends (the domain-separation rule). Both sides connect out to this hub, so an onboard computer
/// needs no open port.
/// </summary>
public sealed class OnboardLinkHub(OnboardLinkRouter router, ILogger<OnboardLinkHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query;
        var role = query?[OnboardLink.RoleQuery].ToString();
        if (role == OnboardLink.Onboard)
        {
            var tails = query?[OnboardLink.TailsQuery].ToString() is { Length: > 0 } t ? t : "*";
            router.AddOnboard(Context.ConnectionId, tails.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            logger.LogInformation("Onboard computer connected ({ConnectionId}), serving {Tails}.", Context.ConnectionId, tails);
        }
        else if (role == OnboardLink.Aircraft)
        {
            router.AddAircraft(Context.ConnectionId);
            logger.LogInformation("Aircraft link connected ({ConnectionId}).", Context.ConnectionId);
        }
        else
        {
            logger.LogWarning("Connection {ConnectionId} to the onboard hub without a role; closing it.", Context.ConnectionId);
            Context.Abort();
        }
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        router.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Aircraft -> the onboard computer serving <paramref name="tail"/> (dropped, with a
    /// warning, when none is connected: the aircraft sees it from the missing status).</summary>
    public async Task ToOnboard(string tail, string kind, JsonElement payload)
    {
        if (router.OnboardFor(tail) is not { } connectionId)
        {
            logger.LogWarning("No onboard computer connected for {Tail}; dropped '{Kind}'.", tail, kind);
            return;
        }
        await Clients.Client(connectionId).SendAsync(OnboardLink.Receive, tail, kind, payload);
    }

    /// <summary>Onboard computer -> the aircraft (every aircraft link; normally one).</summary>
    public Task ToAircraft(string tail, string kind, JsonElement payload) =>
        Clients.Clients(router.Aircraft).SendAsync(OnboardLink.Receive, tail, kind, payload);
}

/// <summary>Who is connected to <see cref="OnboardLinkHub"/>: aircraft links, and onboard computers
/// with the tails each serves. Hub instances are per call, so this lives in a singleton.</summary>
public sealed class OnboardLinkRouter
{
    private readonly ConcurrentDictionary<string, byte> _aircraft = new();
    private readonly ConcurrentDictionary<string, (string[] Tails, DateTime Since)> _onboard = new();

    public IReadOnlyList<string> Aircraft => _aircraft.Keys.ToList();

    public void AddAircraft(string connectionId) => _aircraft[connectionId] = 0;

    public void AddOnboard(string connectionId, string[] tails) => _onboard[connectionId] = (tails, DateTime.UtcNow);

    public void Remove(string connectionId)
    {
        _aircraft.TryRemove(connectionId, out _);
        _onboard.TryRemove(connectionId, out _);
    }

    /// <summary>The newest onboard connection that serves this tail by name, else the newest serving all.</summary>
    public string? OnboardFor(string tail)
    {
        var all = _onboard.ToList();
        return all.Where(o => o.Value.Tails.Contains(tail, StringComparer.OrdinalIgnoreCase)).OrderByDescending(o => o.Value.Since).Select(o => o.Key).FirstOrDefault()
               ?? all.Where(o => o.Value.Tails.Contains("*")).OrderByDescending(o => o.Value.Since).Select(o => o.Key).FirstOrDefault();
    }

    public int OnboardCount => _onboard.Count;
}
