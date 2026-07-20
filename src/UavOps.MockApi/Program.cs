using UavOps.MockApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<UavState>();

var app = builder.Build();

app.MapOpenApi("/openapi/v1.json");

app.MapPost("/navigate", (NavigateRequest req, UavState state) =>
{
    if (!KnownPoints.TryResolve(req.Location, out var lat, out var lng))
    {
        return Results.BadRequest(new { error = $"Unknown location '{req.Location}'." });
    }

    state.Navigate(req.Location, lat, lng);
    return Results.Ok(state.Snapshot());
})
.WithName("NavigateTo")
.WithSummary("Send the UAV to a named location.")
.Accepts<NavigateRequest>("application/json");

app.MapPost("/speed", (SpeedRequest req, UavState state) =>
{
    if (req.SpeedKts is < 1 or > 500)
    {
        return Results.BadRequest(new { error = "speedKts must be between 1 and 500." });
    }

    state.SetSpeed(req.SpeedKts);
    return Results.Ok(state.Snapshot());
})
.WithName("SetSpeed")
.WithSummary("Change the UAV's target cruise speed.");

app.MapPost("/altitude", (AltitudeRequest req, UavState state) =>
{
    if (req.AltitudeFt is < 0 or > 60000)
    {
        return Results.BadRequest(new { error = "altitudeFt must be between 0 and 60000." });
    }

    state.SetAltitude(req.AltitudeFt);
    return Results.Ok(state.Snapshot());
})
.WithName("SetAltitude")
.WithSummary("Change the UAV's target altitude.");

app.MapPost("/rtl", (UavState state) =>
{
    state.ReturnToLaunch();
    return Results.Ok(state.Snapshot());
})
.WithName("ReturnToLaunch")
.WithSummary("Command the UAV to return to and land at its launch point.");

app.MapPost("/payload/point", (PointPayloadRequest req, UavState state) =>
{
    if (!KnownPoints.TryResolve(req.Location, out _, out _))
    {
        return Results.BadRequest(new { error = $"Unknown location '{req.Location}'." });
    }

    state.PointPayload(req.Location);
    return Results.Ok(state.Snapshot());
})
.WithName("PointPayload")
.WithSummary("Point the UAV's sensor/gimbal at a named location.");

app.MapPost("/payload/reset", (UavState state) =>
{
    state.ResetPayload();
    return Results.Ok(state.Snapshot());
})
.WithName("ResetPayload")
.WithSummary("Reset the payload/gimbal to its default (forward) position.");

app.MapGet("/telemetry", (UavState state) => Results.Ok(state.Snapshot()))
.WithName("GetTelemetry")
.WithSummary("Get the UAV's current telemetry snapshot.");

app.MapPost("/mission/waypoints", (UploadWaypointsRequest req, UavState state) =>
{
    state.UploadWaypoints(req.Waypoints);
    return Results.Ok(new { accepted = req.Waypoints.Count });
})
.WithName("UploadWaypoints")
.WithSummary("Upload a list of mission waypoints to the UAV.");

app.MapGet("/mission/status", (UavState state) =>
{
    var snap = state.Snapshot();
    return Results.Ok(new { mode = snap.Mode, waypointCount = state.Waypoints.Count });
})
.WithName("GetMissionStatus")
.WithSummary("Get the current mission execution status.");

app.Run();

public sealed record NavigateRequest(string Location);
public sealed record SpeedRequest(int SpeedKts);
public sealed record AltitudeRequest(int AltitudeFt);
public sealed record PointPayloadRequest(string Location);
public sealed record UploadWaypointsRequest(List<Waypoint> Waypoints);
