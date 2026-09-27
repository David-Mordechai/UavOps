using System.Text.Json;
using UavOps.Agent.Contracts;
using UavOps.Agent.Mission;

namespace UavOps.Agent.McpMoav;

/// <summary>
/// AOI search-mission tools. Zone lookup and route planning happen here, on the ground; only the
/// payload zoom, upload, search target and start go to the aircraft, through the same
/// <see cref="IOperationService"/> as every other Moav tool, so they work on either backend.
/// The route is flown at the UAV's current altitude unless the operator gives one: the aircraft
/// never changes altitude without a command saying so. Lanes are spaced from the payload field of
/// view the aircraft reports, not an assumed camera.
/// </summary>
public static partial class MoavTools
{
    private static readonly JsonSerializerOptions ReadValueOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> ListAoiZones(IAoiZoneStore zones, CancellationToken cancellationToken)
    {
        var all = await zones.ListAsync(cancellationToken);
        return Serialize(all.Select(z => z.Summarize()).Select(s => new
        {
            name = s.Name,
            areaSqKm = s.AreaSqKm,
            centroid = s.Centroid
        }));
    }

    public static async Task<string> GetAoiZone(IAoiZoneStore zones, string zoneName, CancellationToken cancellationToken)
    {
        var zone = await zones.GetAsync(zoneName, cancellationToken);
        return zone is null ? await UnknownZoneAsync(zones, zoneName, cancellationToken) : Serialize(zone.Summarize());
    }

    public static async Task<string> PlanSearchRoute(
        IOperationService moav,
        IAoiZoneStore zones,
        IRouteStore routes,
        MissionOptions options,
        string tailNumber,
        string zoneName,
        int? altitudeFt = null,
        CancellationToken cancellationToken = default)
    {
        var telemetry = await TelemetryAsync(moav, tailNumber, cancellationToken);
        if (telemetry.Error is not null)
            return telemetry.Error;
        var planned = await PlanAsync(zones, routes, options, tailNumber, zoneName, telemetry.Value!, altitudeFt, cancellationToken);
        return planned.Error ?? Serialize(new
        {
            planned.Route!.RouteId,
            planned.Route.TailNumber,
            planned.Route.ZoneName,
            waypointCount = planned.Route.Waypoints.Count,
            planned.Route.LaneCount,
            planned.Route.LaneSpacingMeters,
            planned.Route.AltitudeFt,
            payloadZoom = telemetry.Value!.PayloadZoom,
            lengthKm = Math.Round(planned.Route.LengthMeters / 1000, 1),
            estimatedMinutes = Math.Round(planned.Route.EstimatedDuration.TotalMinutes, 1),
            uploaded = false
        });
    }

    public static async Task<string> UploadRoute(IOperationService moav, IRouteStore routes, string tailNumber, CancellationToken cancellationToken)
    {
        var uploaded = await UploadAsync(moav, routes, tailNumber, cancellationToken);
        return uploaded.Error ?? Serialize(new
        {
            uploaded.Route!.TailNumber,
            uploaded.Route.ZoneName,
            uploaded.Route.RouteId,
            waypointsUploaded = uploaded.Route.Waypoints.Count,
            started = false
        });
    }

    public static async Task<string> SetSearchTarget(
        IOperationService moav,
        IRouteStore routes,
        MissionOptions options,
        MissionEventService missionEvents,
        string tailNumber,
        string targetDescription,
        CancellationToken cancellationToken)
    {
        var result = await SetTargetAsync(moav, routes, options, missionEvents, tailNumber, targetDescription, cancellationToken);
        return ToResultText(result);
    }

    public static async Task<string> PrepareAoiSearch(
        IOperationService moav,
        IAoiZoneStore zones,
        IRouteStore routes,
        MissionOptions options,
        MissionEventService missionEvents,
        string tailNumber,
        string zoneName,
        string targetDescription,
        CancellationToken cancellationToken)
    {
        var telemetry = await TelemetryAsync(moav, tailNumber, cancellationToken);
        if (telemetry.Error is not null)
            return telemetry.Error;

        // Zoom in so the frame shows about SearchGroundWidthMeters of ground from where the UAV
        // flies now - what the onboard model needs to recognise a vehicle - and plan the lanes from
        // the field of view the payload actually took.
        var zoom = SearchZoom(telemetry.Value!, options);
        if (zoom.Error is not null)
            return zoom.Error;
        var zoomed = await ReadTelemetryAsync(await moav.SetPayloadZoom(tailNumber, zoom.Value, cancellationToken));
        if (zoomed.Error is not null)
            return $"{zoomed.Error} (Setting the payload zoom failed; nothing was planned or started.)";

        var planned = await PlanAsync(zones, routes, options, tailNumber, zoneName, zoomed.Value!, null, cancellationToken);
        if (planned.Error is not null)
            return planned.Error;

        var uploaded = await UploadAsync(moav, routes, tailNumber, cancellationToken);
        if (uploaded.Error is not null)
            return $"{uploaded.Error} (The route was planned but not uploaded; nothing was started.)";

        var target = await SetTargetAsync(moav, routes, options, missionEvents, tailNumber, targetDescription, cancellationToken);
        if (!target.Success)
            return $"Error: The route was uploaded to {tailNumber}, but setting the search target failed: {target.ErrorMessage}. Nothing was started.";

        var route = planned.Route!;
        return Serialize(new
        {
            route.TailNumber,
            route.ZoneName,
            searchTarget = targetDescription,
            waypointsUploaded = route.Waypoints.Count,
            route.LaneCount,
            route.AltitudeFt,
            payloadZoom = zoomed.Value!.PayloadZoom,
            lengthKm = Math.Round(route.LengthMeters / 1000, 1),
            estimatedMinutes = Math.Round(route.EstimatedDuration.TotalMinutes, 1),
            started = false,
            nextStep = "Tell the operator the search is ready. Don't ask whether to start: call StartMission only when the operator says to start."
        });
    }

    public static async Task<string> StartMission(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.StartMission(tailNumber, cancellationToken));

    private sealed record Step(SearchRoute? Route, string? Error);

    private sealed record Result<T>(T? Value, string? Error);

    private static async Task<Result<TelemetrySnapshot>> TelemetryAsync(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        await ReadTelemetryAsync(await moav.GetTelemetry(tailNumber, cancellationToken));

    private static Task<Result<TelemetrySnapshot>> ReadTelemetryAsync(OperationResult result)
    {
        if (!result.Success)
            return Task.FromResult(new Result<TelemetrySnapshot>(null, ToResultText(result)));
        var telemetry = ReadValue<TelemetrySnapshot>(result);
        return Task.FromResult(telemetry is null
            ? new Result<TelemetrySnapshot>(null, "Error: The UAV returned no telemetry.")
            : new Result<TelemetrySnapshot>(telemetry, null));
    }

    /// <summary>The zoom that makes the payload's frame about <see cref="MissionOptions.SearchGroundWidthMeters"/>
    /// across at the UAV's current altitude, from its reported field of view and zoom.</summary>
    private static Result<double> SearchZoom(TelemetrySnapshot telemetry, MissionOptions options)
    {
        if (telemetry.PayloadHfovDeg <= 0)
            return new Result<double>(0, "Error: The UAV doesn't report its payload field of view, so a search route can't be planned for its camera.");
        if (telemetry.AltitudeFt <= 0)
            return new Result<double>(0, "Error: The UAV is on the ground; it can't search from there.");
        var wideHfov = telemetry.PayloadHfovDeg * Math.Max(telemetry.PayloadZoom, 1);
        var wantedHfov = 2 * Math.Atan(options.SearchGroundWidthMeters / 2 / (telemetry.AltitudeFt * 0.3048)) * 180 / Math.PI;
        return new Result<double>(Math.Max(1, Math.Round(wideHfov / wantedHfov, 1)), null);
    }

    private static async Task<Step> PlanAsync(
        IAoiZoneStore zones,
        IRouteStore routes,
        MissionOptions options,
        string tailNumber,
        string zoneName,
        TelemetrySnapshot telemetry,
        int? altitudeFt,
        CancellationToken cancellationToken)
    {
        var zone = await zones.GetAsync(zoneName, cancellationToken);
        if (zone is null)
            return new Step(null, await UnknownZoneAsync(zones, zoneName, cancellationToken));
        if (telemetry.PayloadHfovDeg <= 0)
            return new Step(null, "Error: The UAV doesn't report its payload field of view, so a search route can't be planned for its camera.");
        var altitude = altitudeFt ?? telemetry.AltitudeFt;
        if (altitude <= 0)
            return new Step(null, "Error: The UAV is on the ground; give a search altitude.");

        // Flown at the UAV's current altitude unless the operator gave one. The position picks the
        // entry corner; the speed only feeds the time estimate.
        var parameters = new SearchPlanParameters(
            altitude,
            telemetry.PayloadHfovDeg,
            options.SideOverlap,
            options.MaxWaypoints,
            telemetry.SpeedKts > 0 ? telemetry.SpeedKts : options.DefaultSpeedKts);

        try
        {
            var position = new GeoPoint(telemetry.Lat, telemetry.Lng);
            var route = SearchRoutePlanner.Plan(zone, tailNumber, position, parameters);
            routes.Save(route);
            return new Step(route, null);
        }
        catch (SearchPlanException ex)
        {
            return new Step(null, $"Error: {ex.Message}");
        }
    }

    private static async Task<Step> UploadAsync(IOperationService moav, IRouteStore routes, string tailNumber, CancellationToken cancellationToken)
    {
        var route = routes.Get(tailNumber);
        if (route is null)
            return new Step(null, $"Error: No search route has been planned for {tailNumber}. Plan one over a zone first.");

        var waypoints = route.Waypoints.Select(p => new Waypoint(p.Lat, p.Lng, route.AltitudeFt)).ToList();
        var result = await moav.UploadWaypoints(tailNumber, waypoints, cancellationToken);
        return result.Success ? new Step(route, null) : new Step(null, ToResultText(result));
    }

    private static Task<OperationResult> SetTargetAsync(
        IOperationService moav,
        IRouteStore routes,
        MissionOptions options,
        MissionEventService missionEvents,
        string tailNumber,
        string targetDescription,
        CancellationToken cancellationToken)
    {
        // Tied to the planned route when there is one, so detections name the zone being searched.
        var route = routes.Get(tailNumber);
        var request = new SearchTargetRequest(
            route?.RouteId ?? $"search-{tailNumber}-{Guid.NewGuid().ToString("N")[..6]}",
            route?.ZoneName ?? "",
            targetDescription.Trim(),
            options.MinDetectionConfidence);
        missionEvents.RememberSearchTarget(request);
        return moav.SetSearchTarget(tailNumber, request, cancellationToken);
    }

    private static async Task<string> UnknownZoneAsync(IAoiZoneStore zones, string zoneName, CancellationToken cancellationToken)
    {
        var known = (await zones.ListAsync(cancellationToken)).Select(z => z.Name);
        return $"Error: Unknown AOI zone '{zoneName}'. Known zones: {string.Join(", ", known)}.";
    }

    /// <summary>A result's value as <typeparamref name="T"/>: the typed object from the simulated
    /// backend, or the JSON the relay backend hands back.</summary>
    private static T? ReadValue<T>(OperationResult result) where T : class =>
        result.Value as T ?? JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result.Value), ReadValueOptions);

    private static string Serialize(object value) => JsonSerializer.Serialize(value, ResultSerializeOptions);
}
