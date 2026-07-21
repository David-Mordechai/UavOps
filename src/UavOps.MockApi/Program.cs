using UavOps.MockApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSingleton<UavRegistry>();

var app = builder.Build();

app.MapOpenApi("/openapi/v1.json");

static bool TryResolveUav(UavRegistry registry, string tailNumber, out UavState state, out IResult notFound)
{
    if (registry.TryGet(tailNumber, out state!))
    {
        notFound = null!;
        return true;
    }

    state = null!;
    notFound = Results.NotFound(new { error = $"Unknown UAV tail number '{tailNumber}'." });
    return false;
}

app.MapGet("/uavs", (UavRegistry registry) =>
{
    var fleet = registry.All.Select(kvp =>
    {
        var snap = kvp.Value.Snapshot();
        return new { tailNumber = kvp.Key, mode = snap.Mode, lat = snap.Lat, lng = snap.Lng };
    });
    return Results.Ok(fleet);
})
.WithName("ListUavs")
.WithSummary("List all known UAVs and a brief status summary for each.");

app.MapPost("/uavs/{tailNumber}/navigate", (string tailNumber, NavigateRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

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

app.MapPost("/uavs/{tailNumber}/speed", (string tailNumber, SpeedRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    if (req.SpeedKts is < 1 or > 500)
    {
        return Results.BadRequest(new { error = "speedKts must be between 1 and 500." });
    }

    state.SetSpeed(req.SpeedKts);
    return Results.Ok(state.Snapshot());
})
.WithName("SetSpeed")
.WithSummary("Change the UAV's target cruise speed.");

app.MapPost("/uavs/{tailNumber}/altitude", (string tailNumber, AltitudeRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    if (req.AltitudeFt is < 0 or > 60000)
    {
        return Results.BadRequest(new { error = "altitudeFt must be between 0 and 60000." });
    }

    state.SetAltitude(req.AltitudeFt);
    return Results.Ok(state.Snapshot());
})
.WithName("SetAltitude")
.WithSummary("Change the UAV's target altitude.");

app.MapPost("/uavs/{tailNumber}/rtl", (string tailNumber, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    state.ReturnToLaunch();
    return Results.Ok(state.Snapshot());
})
.WithName("ReturnToLaunch")
.WithSummary("Command the UAV to return to and land at its launch point.");

app.MapPost("/uavs/{tailNumber}/payload/point", (string tailNumber, PointPayloadRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    if (!KnownPoints.TryResolve(req.Location, out _, out _))
    {
        return Results.BadRequest(new { error = $"Unknown location '{req.Location}'." });
    }

    state.PointPayload(req.Location);
    return Results.Ok(state.Snapshot());
})
.WithName("PointPayload")
.WithSummary("Point the UAV's sensor/gimbal at a named location.");

app.MapPost("/uavs/{tailNumber}/payload/reset", (string tailNumber, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    state.ResetPayload();
    return Results.Ok(state.Snapshot());
})
.WithName("ResetPayload")
.WithSummary("Reset the payload/gimbal to its default (forward) position.");

app.MapGet("/uavs/{tailNumber}/telemetry", (string tailNumber, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    return Results.Ok(state.Snapshot());
})
.WithName("GetTelemetry")
.WithSummary("Get the UAV's current telemetry snapshot.");

app.MapPost("/uavs/{tailNumber}/mission/waypoints", (string tailNumber, UploadWaypointsRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    state.UploadWaypoints(req.Waypoints);
    return Results.Ok(new { accepted = req.Waypoints.Count });
})
.WithName("UploadWaypoints")
.WithSummary("Upload a list of mission waypoints to the UAV.");

app.MapGet("/uavs/{tailNumber}/mission/status", (string tailNumber, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    var snap = state.Snapshot();
    return Results.Ok(new { mode = snap.Mode, waypointCount = state.Waypoints.Count });
})
.WithName("GetMissionStatus")
.WithSummary("Get the current mission execution status.");

app.MapGet("/uavs/{tailNumber}/gdt/link-status", (string tailNumber, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    return Results.Ok(state.GdtStatus());
})
.WithName("GetLinkStatus")
.WithSummary("Get the ground data terminal's link status for this UAV (signal strength, link state, tracking mode).");

app.MapPost("/uavs/{tailNumber}/gdt/tracking-mode", (string tailNumber, SetTrackingModeRequest req, UavRegistry registry) =>
{
    if (!TryResolveUav(registry, tailNumber, out var state, out var notFound)) return notFound;

    if (!string.Equals(req.Mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(req.Mode, "Manual", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "mode must be 'Auto' or 'Manual'." });
    }

    state.SetGdtTrackingMode(req.Mode);
    return Results.Ok(state.GdtStatus());
})
.WithName("SetAntennaTrackingMode")
.WithSummary("Set the ground data terminal antenna's tracking mode (Auto or Manual) for this UAV.");

app.Run();

public sealed record NavigateRequest(string Location);
public sealed record SpeedRequest(int SpeedKts);
public sealed record AltitudeRequest(int AltitudeFt);
public sealed record PointPayloadRequest(string Location);
public sealed record UploadWaypointsRequest(List<Waypoint> Waypoints);
public sealed record SetTrackingModeRequest(string Mode);
