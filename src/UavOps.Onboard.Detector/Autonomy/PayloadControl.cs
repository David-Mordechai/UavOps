using System.Net.Http.Json;
using UavOps.Onboard.Contracts;

namespace UavOps.Onboard.Detector.Autonomy;

/// <summary>The payload as the onboard computer drives it: aim at a ground point, zoom, let go.
/// Over HTTP to the simulated aircraft today (<see cref="PayloadPaths"/>); a real gimbal's own
/// commands would implement this without changing the executive.</summary>
public interface IPayloadControl
{
    Task PointAtAsync(SearchTask task, double lat, double lng, CancellationToken cancellationToken);
    Task ZoomAsync(SearchTask task, double groundWidthMeters, CancellationToken cancellationToken);
    Task ReleaseAsync(SearchTask task, CancellationToken cancellationToken);
}

/// <summary>Where each tracked target's position goes: the aircraft's callback.</summary>
public interface ITrackSink
{
    Task SendAsync(string callbackUrl, TargetTrackReport report, CancellationToken cancellationToken);
}

public sealed class HttpPayloadControl(HttpClient http) : IPayloadControl
{
    public Task PointAtAsync(SearchTask task, double lat, double lng, CancellationToken cancellationToken) =>
        Post(task, PayloadPaths.Point, new PointAtCommand(lat, lng), cancellationToken);

    public Task ZoomAsync(SearchTask task, double groundWidthMeters, CancellationToken cancellationToken) =>
        Post(task, PayloadPaths.Zoom, new ZoomCommand(groundWidthMeters), cancellationToken);

    public Task ReleaseAsync(SearchTask task, CancellationToken cancellationToken) =>
        Post<object?>(task, PayloadPaths.Release, null, cancellationToken);

    private async Task Post<T>(SearchTask task, string path, T body, CancellationToken cancellationToken)
    {
        if (task.PayloadUrl is not { } baseUrl)
            return;
        using var response = await http.PostAsJsonAsync($"{baseUrl.TrimEnd('/')}/{path}", body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class HttpTrackSink(HttpClient http) : ITrackSink
{
    public async Task SendAsync(string callbackUrl, TargetTrackReport report, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(callbackUrl, report, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
