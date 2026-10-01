using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using UavOps.Agent.Mission;
using UavOps.Simulator.Camera;
using UavOps.Simulator.World;

namespace UavOps.Simulator;

/// <summary>The page's live feed: <c>state</c> events a few times a second. The page reports what
/// part of the map it shows (<see cref="SetView"/>), so traffic is sent only where someone can see it.</summary>
public sealed class SimHub(PageView view) : Hub
{
    public void SetView(double west, double south, double east, double north, double zoom) => view.Set(west, south, east, north, zoom);
}

/// <summary>The map area the page last showed (one page at a time is the dev use).</summary>
public sealed class PageView
{
    private volatile Box? _view;

    public void Set(double west, double south, double east, double north, double zoom) => _view = new Box(west, south, east, north, zoom);

    public Box? Current => _view;

    public sealed record Box(double West, double South, double East, double North, double Zoom);
}

/// <summary>
/// The sim clock: every tick advances the fleet by the real time elapsed times
/// <see cref="SimOptions.TimeScale"/>, reports detections and mission ends to the host, hands the
/// survey frames the cameras took to <see cref="SurveyCameraWorker"/>, and every few ticks pushes
/// the whole picture to the page.
/// </summary>
public sealed class FlightTickService(
    SimFleet fleet,
    SurveyCameraWorker surveyCamera,
    OnboardDetectorClient onboard,
    GroundWorld world,
    PageView pageView,
    FleetConnectionService connection,
    IHubContext<SimHub> hub,
    SimOptions options,
    ILogger<FlightTickService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(options.TickMilliseconds));
        var clock = Stopwatch.StartNew();
        var lastTick = TimeSpan.Zero;
        var lastPush = TimeSpan.Zero;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = clock.Elapsed;
            // Capped, so a stall (debugger, sleep) doesn't teleport everyone on the next tick.
            var seconds = Math.Min((now - lastTick).TotalSeconds, 1) * Math.Max(options.TimeScale, 0);
            lastTick = now;

            try
            {
                world.Clock.Advance(seconds, DateTime.UtcNow);
                var result = fleet.Advance(seconds, DateTime.UtcNow);
                foreach (var detection in result.Detections)
                    _ = connection.ReportDetectionAsync(detection);
                foreach (var missionEvent in result.MissionEvents)
                    _ = connection.ReportMissionEventAsync(missionEvent);
                foreach (var capture in result.SurveyCaptures)
                    surveyCamera.Capture(capture.TailNumber, capture.Telemetry);

                if ((now - lastPush).TotalMilliseconds >= options.StatePushMilliseconds)
                {
                    lastPush = now;
                    await hub.Clients.All.SendAsync("state", Snapshot(), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Simulation tick failed.");
            }
        }
    }

    public object Snapshot()
    {
        var view = fleet.View();
        var simTime = world.Clock.Now;
        return new
        {
            connected = connection.IsConnected,
            hostHubUrl = options.HostHubUrl,
            timeScale = options.TimeScale,
            simTime,
            uavs = view.Uavs,
            detections = view.Detections,
            objects = world.ObjectsAt(simTime),
            vehicles = Vehicles(view.Uavs, simTime),
            detector = options.UsesOnboardDetector
                ? new { mode = "Onboard", url = (string?)onboard.LinkDescription, reachable = onboard.Reachable, tasks = onboard.Statuses, recent = onboard.Recent }
                : new { mode = "Simulated", url = (string?)null, reachable = true, tasks = (IReadOnlyList<Onboard.Contracts.SearchTaskStatus>)[], recent = (IReadOnlyList<OnboardDetectionView>)[] }
        };
    }

    private const double CameraTrafficRadiusMeters = 4000;
    private const int MaxVehiclesSent = 2500;

    /// <summary>Traffic someone can see: around each UAV (its camera view) and in the page's map
    /// view when zoomed in enough for a car to show. Compact rows: [id, lat, lng, heading, sprite].</summary>
    private List<object[]> Vehicles(List<SimUavView> uavs, double simTime)
    {
        var rows = new Dictionary<string, object[]>();
        void Take(GeoPoint center, double radius)
        {
            foreach (var (vehicle, position, heading) in world.TrafficNear(center, radius, simTime))
            {
                if (rows.Count >= MaxVehiclesSent)
                    return;
                rows.TryAdd(vehicle.Id, [vehicle.Id, Math.Round(position.Lat, 6), Math.Round(position.Lng, 6), Math.Round(heading), vehicle.SpriteKey]);
            }
        }
        foreach (var u in uavs.Where(u => u.Mode != "Landed"))
            Take(new GeoPoint(u.Lat, u.Lng), CameraTrafficRadiusMeters);
        if (pageView.Current is { Zoom: >= 12.5 } v)
        {
            var center = new GeoPoint((v.South + v.North) / 2, (v.West + v.East) / 2);
            var radius = Math.Min(GeoProjection.DistanceMeters(center, new GeoPoint(v.North, v.East)), 10_000);
            Take(center, radius);
        }
        return rows.Values.ToList();
    }
}
