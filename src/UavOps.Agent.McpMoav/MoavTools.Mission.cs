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

    private static readonly JsonSerializerOptions TeamResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

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
        CancellationToken cancellationToken,
        bool track = false)
    {
        var result = await SetTargetAsync(moav, routes, options, missionEvents, tailNumber, targetDescription, track, cancellationToken);
        return ToResultText(result);
    }

    /// <summary>
    /// Prepares one UAV, or a team, to search a zone: zooms each payload for the search, plans,
    /// uploads each UAV's route and sets its target. Never starts. A team splits the zone
    /// (<see cref="SearchRoutePlanner.PlanTeam"/>): one call with every UAV, since one call per UAV
    /// would plan the whole zone for each. The list is always real tail numbers; which UAVs the
    /// operator meant is the model's call, grounded by the host before this runs.
    /// </summary>
    public static async Task<string> PrepareAoiSearch(
        IOperationService moav,
        IAoiZoneStore zones,
        IRouteStore routes,
        MissionOptions options,
        MissionEventService missionEvents,
        string[] tailNumbers,
        string zoneName,
        string targetDescription,
        CancellationToken cancellationToken,
        bool track = false)
    {
        var tails = (tailNumbers ?? [])
            .Select(t => t?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tails.Count == 0)
            return "Error: No UAV given. Pass the tail numbers of the UAVs to search with.";
        if (tails.Any(t => string.Equals(t, "ALL", StringComparison.OrdinalIgnoreCase)))
            return "Error: 'ALL' is not a tail number. Call ListFleet and pass every UAV's tail number. Nothing was sent to any UAV.";
        if (tails.Count > options.MaxTeamSize)
            return $"Error: A search team is at most {options.MaxTeamSize} UAVs. Nothing was sent to any UAV.";
        if (tails.Count == 1)
            return await PrepareSingleSearchAsync(moav, zones, routes, options, missionEvents, tails[0], zoneName, targetDescription, track, cancellationToken);

        // Everything that can fail without touching a UAV is checked first: telemetry, the zone,
        // and each UAV's search zoom.
        var zone = await zones.GetAsync(zoneName, cancellationToken);
        if (zone is null)
            return await UnknownZoneAsync(zones, zoneName, cancellationToken);
        var before = new List<(string Tail, TelemetrySnapshot Telemetry, double Zoom)>();
        foreach (var tail in tails)
        {
            var telemetry = await TelemetryAsync(moav, tail, cancellationToken);
            if (telemetry.Error is not null)
                return $"{telemetry.Error} ({tail}.) Nothing was sent to any UAV.";
            var zoom = SearchZoom(telemetry.Value!, options);
            if (zoom.Error is not null)
                return $"{zoom.Error} ({tail}.) Nothing was sent to any UAV.";
            before.Add((tail, telemetry.Value!, zoom.Value));
        }

        // Who takes part is decided before any payload moves, from where each UAV is and the
        // ground width the search zooms to: a UAV that would only slow the team (too far away) is
        // left out and its camera left alone.
        var members = before.Select(b => new SearchTeamMember(
            b.Tail,
            new GeoPoint(b.Telemetry.Lat, b.Telemetry.Lng),
            b.Telemetry.AltitudeFt,
            options.SearchSpeedKts)).ToList();
        List<string> taking;
        List<string> leftOut;
        try
        {
            var expected = SearchRoutePlanner.PlanTeam(zone, members, options.SearchGroundWidthMeters * (1 - options.SideOverlap), options.MaxWaypoints);
            taking = tails.Where(t => expected.Routes.Any(r => r.TailNumber == t)).ToList();
            leftOut = expected.UnusedTails.ToList();
        }
        catch (SearchPlanException ex)
        {
            return $"Error: {ex.Message} Nothing was sent to any UAV.";
        }

        var zoomed = new List<(string Tail, TelemetrySnapshot Telemetry)>();
        foreach (var (tail, _, zoom) in before.Where(b => taking.Contains(b.Tail)))
        {
            var result = await ReadTelemetryAsync(await moav.SetPayloadZoom(tail, zoom, cancellationToken));
            if (result.Error is not null)
                return $"{result.Error} (Setting {tail}'s payload zoom failed; nothing was planned or started" +
                       $"{(zoomed.Count > 0 ? $", though {string.Join(" and ", zoomed.Select(z => z.Tail))} already zoomed in" : "")}.)";
            var slowed = await moav.SetSpeed(tail, options.SearchSpeedKts, cancellationToken);
            if (!slowed.Success)
                return $"{ToResultText(slowed)} (Setting {tail}'s search speed failed; nothing was planned or started.)";
            zoomed.Add((tail, result.Value!));
        }

        // One set of lanes for the whole team, spaced for the narrowest footprint the payloads
        // actually took, so every UAV's strip is fully seen. Each UAV keeps its own altitude.
        var spacing = zoomed.Min(z => new SearchPlanParameters(z.Telemetry.AltitudeFt, z.Telemetry.PayloadHfovDeg, options.SideOverlap,
            options.MaxWaypoints, options.DefaultSpeedKts).LaneSpacingMeters);
        TeamSearchPlan plan;
        try
        {
            plan = SearchRoutePlanner.PlanTeam(zone, members.Where(m => taking.Contains(m.TailNumber)).ToList(), spacing, options.MaxWaypoints);
        }
        catch (SearchPlanException ex)
        {
            return $"Error: {ex.Message} Nothing was uploaded or started.";
        }
        leftOut.AddRange(plan.UnusedTails);
        // Reported in the order the UAVs were given, not the order of their strips.
        var planned = plan.Routes.OrderBy(r => tails.IndexOf(r.TailNumber)).ToList();
        foreach (var route in planned)
            routes.Save(route);
        if (planned.Count > 1)
            missionEvents.RememberTeam(plan.TeamId, planned.Select(r => (r.TailNumber, r.RouteId)).ToList());

        var ready = new List<object>();
        var readyTails = new List<string>();
        var failed = new List<string>();
        foreach (var route in planned)
        {
            var uploaded = await UploadAsync(moav, routes, route.TailNumber, cancellationToken);
            if (uploaded.Error is not null)
            {
                failed.Add($"{route.TailNumber}: the route didn't upload ({uploaded.Error})");
                continue;
            }
            var target = await SetTargetAsync(moav, routes, options, missionEvents, route.TailNumber, targetDescription, track, cancellationToken);
            if (!target.Success)
            {
                failed.Add($"{route.TailNumber}: the route was uploaded but setting the search target failed ({target.ErrorMessage})");
                continue;
            }
            var telemetry = zoomed.Single(z => z.Tail == route.TailNumber).Telemetry;
            var transit = GeoProjection.DistanceMeters(new GeoPoint(telemetry.Lat, telemetry.Lng), route.Waypoints[0]);
            var speed = Math.Max(members.Single(m => m.TailNumber == route.TailNumber).SpeedKts, 1) * SearchPlanParameters.MetersPerSecondPerKnot;
            readyTails.Add(route.TailNumber);
            ready.Add(new
            {
                route.TailNumber,
                route.LaneCount,
                waypointsUploaded = route.Waypoints.Count,
                route.AltitudeFt,
                payloadZoom = telemetry.PayloadZoom,
                lengthKm = Math.Round(route.LengthMeters / 1000, 1),
                estimatedMinutesUntilDone = Math.Round((transit / speed + route.EstimatedDuration.TotalSeconds) / 60, 1)
            });
        }

        // What the operator must hear goes into nextStep itself, word for word: a separate
        // "mention this" field was dropped from the reply in the live demo.
        var laneTotal = plan.Routes.Sum(r => r.LaneCount);
        var notUsed = leftOut.Count == 0
            ? null
            : $"{JoinAnd(leftOut)} {(leftOut.Count == 1 ? "was" : "were")} left out of the search: " +
              (laneTotal < taking.Count + leftOut.Count && plan.UnusedTails.Count > 0
                  ? $"the zone has only {laneTotal} search lanes."
                  : $"{(leftOut.Count == 1 ? "it is" : "they are")} too far away - the other UAVs finish the zone sooner without {(leftOut.Count == 1 ? "it" : "them")}.");
        var altitudeNote = CloseAltitudes(planned, options.TeamAltitudeSeparationFt);
        var mustSay = string.Join(" ", new[] { notUsed, altitudeNote }.Where(n => n is not null));
        return JsonSerializer.Serialize(new
        {
            zoneName = zone.Name,
            searchTarget = targetDescription,
            findAndTrack = track,
            teamSearch = planned.Count > 1,
            zoneSplitBetween = ready,
            notReady = failed.Count > 0 ? failed : null,
            notUsed,
            altitudeNote,
            started = false,
            nextStep = readyTails.Count == 0
                ? "Nothing is ready; tell the operator what failed."
                : $"Tell the operator the search is ready for {JoinAnd(readyTails)}, searching at {options.SearchSpeedKts} kts{(failed.Count > 0 ? ", and what failed for the others" : "")}" +
                  $"{(mustSay.Length > 0 ? $", and also tell them: \"{mustSay}\"" : "")}. " +
                  $"Don't ask whether to start: when the operator says to start, call StartMission once with tailNumber '{string.Join(",", readyTails)}'."
        }, TeamResultOptions);
    }

    /// <summary>"998", "998 and 999", "997, 998 and 999".</summary>
    private static string JoinAnd(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Join("", items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    /// <summary>UAVs searching neighbouring strips within <paramref name="separationFt"/> of each
    /// other's altitude. Altitudes are never changed for a search; the operator is told instead.</summary>
    private static string? CloseAltitudes(IReadOnlyList<SearchRoute> routes, int separationFt)
    {
        var close = new List<string>();
        for (var i = 0; i < routes.Count; i++)
        for (var j = i + 1; j < routes.Count; j++)
        {
            if (Math.Abs(routes[i].AltitudeFt - routes[j].AltitudeFt) < separationFt)
                close.Add($"{routes[i].TailNumber} ({routes[i].AltitudeFt} ft) and {routes[j].TailNumber} ({routes[j].AltitudeFt} ft)");
        }
        return close.Count == 0
            ? null
            : $"{string.Join("; ", close)} search at altitudes less than {separationFt} ft apart, side by side; their altitudes were not changed.";
    }

    private static async Task<string> PrepareSingleSearchAsync(
        IOperationService moav,
        IAoiZoneStore zones,
        IRouteStore routes,
        MissionOptions options,
        MissionEventService missionEvents,
        string tailNumber,
        string zoneName,
        string targetDescription,
        bool track,
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
        var slowed = await moav.SetSpeed(tailNumber, options.SearchSpeedKts, cancellationToken);
        if (!slowed.Success)
            return $"{ToResultText(slowed)} (Setting the search speed failed; nothing was planned or started.)";

        var planned = await PlanAsync(zones, routes, options, tailNumber, zoneName, zoomed.Value!, null, cancellationToken, options.SearchSpeedKts);
        if (planned.Error is not null)
            return planned.Error;

        var uploaded = await UploadAsync(moav, routes, tailNumber, cancellationToken);
        if (uploaded.Error is not null)
            return $"{uploaded.Error} (The route was planned but not uploaded; nothing was started.)";

        var target = await SetTargetAsync(moav, routes, options, missionEvents, tailNumber, targetDescription, track, cancellationToken);
        if (!target.Success)
            return $"Error: The route was uploaded to {tailNumber}, but setting the search target failed: {target.ErrorMessage}. Nothing was started.";

        var route = planned.Route!;
        return Serialize(new
        {
            route.TailNumber,
            route.ZoneName,
            searchTarget = targetDescription,
            findAndTrack = track,
            waypointsUploaded = route.Waypoints.Count,
            route.LaneCount,
            route.AltitudeFt,
            searchSpeedKts = options.SearchSpeedKts,
            payloadZoom = zoomed.Value!.PayloadZoom,
            lengthKm = Math.Round(route.LengthMeters / 1000, 1),
            estimatedMinutes = Math.Round(route.EstimatedDuration.TotalMinutes, 1),
            started = false,
            nextStep = $"Tell the operator the search is ready, searching at {options.SearchSpeedKts} kts. Don't ask whether to start: call StartMission only when the operator says to start."
        });
    }

    public static async Task<string> StartMission(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.StartMission(tailNumber, cancellationToken));

    public static async Task<string> StopMission(IOperationService moav, string tailNumber, CancellationToken cancellationToken) =>
        ToResultText(await moav.StopMission(tailNumber, cancellationToken));

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
        CancellationToken cancellationToken,
        int? speedKts = null)
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
        // entry corner; the speed (the search's own, when it sets one) only feeds the time estimate.
        var parameters = new SearchPlanParameters(
            altitude,
            telemetry.PayloadHfovDeg,
            options.SideOverlap,
            options.MaxWaypoints,
            speedKts ?? (telemetry.SpeedKts > 0 ? telemetry.SpeedKts : options.DefaultSpeedKts));

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
        bool track,
        CancellationToken cancellationToken)
    {
        // Tied to the planned route when there is one, so detections name the zone being searched.
        var route = routes.Get(tailNumber);
        var request = new SearchTargetRequest(
            route?.RouteId ?? $"search-{tailNumber}-{Guid.NewGuid().ToString("N")[..6]}",
            route?.ZoneName ?? "",
            targetDescription.Trim(),
            options.MinDetectionConfidence,
            track,
            Repeat: true);
        missionEvents.RememberSearchTarget(request, tailNumber);
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
