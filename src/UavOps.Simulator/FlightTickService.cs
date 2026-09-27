using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using UavOps.Simulator.Camera;

namespace UavOps.Simulator;

/// <summary>The page's live feed: <c>state</c> events a few times a second.</summary>
public sealed class SimHub : Hub;

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
    ScenarioStore scenario,
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
        return new
        {
            connected = connection.IsConnected,
            hostHubUrl = options.HostHubUrl,
            timeScale = options.TimeScale,
            uavs = view.Uavs,
            detections = view.Detections,
            objects = scenario.All(),
            detector = options.UsesOnboardDetector
                ? new { mode = "Onboard", url = (string?)options.OnboardDetectorUrl, reachable = onboard.Reachable, tasks = onboard.Statuses, recent = onboard.Recent }
                : new { mode = "Simulated", url = (string?)null, reachable = true, tasks = (IReadOnlyList<Onboard.Contracts.SearchTaskStatus>)[], recent = (IReadOnlyList<OnboardDetectionView>)[] }
        };
    }
}
