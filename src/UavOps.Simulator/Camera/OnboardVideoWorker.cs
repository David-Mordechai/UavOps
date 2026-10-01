namespace UavOps.Simulator.Camera;

/// <summary>
/// The payload's live video for the onboard computer: while a UAV's onboard agent is looking, a
/// frame of what its payload sees (where it points, at its zoom, straight down on a search route)
/// every 1/<see cref="SimOptions.OnboardVideoFps"/> seconds, into <see cref="VideoFrameBuffer"/>.
/// A UAV whose last frame is still rendering skips a beat rather than queueing, like a camera
/// feeding a busy encoder. Nothing is rendered while no one is looking.
/// </summary>
public sealed class OnboardVideoWorker(
    SimFleet fleet,
    CameraRenderer camera,
    VideoFrameBuffer buffer,
    SimOptions options,
    ILogger<OnboardVideoWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.UsesOnboardDetector)
            return;
        var interval = TimeSpan.FromSeconds(1 / Math.Clamp(options.OnboardVideoFps, 0.5, 30));
        using var timer = new PeriodicTimer(interval);
        var rendering = new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var tail in fleet.Looking())
            {
                if (rendering.TryGetValue(tail, out var busy) && !busy.IsCompleted)
                    continue;
                if (fleet.NextVideoFrame(tail, DateTime.UtcNow) is not { } telemetry)
                    continue;
                rendering[tail] = Task.Run(() =>
                {
                    try
                    {
                        buffer.Add(tail, new SurveyFrame(telemetry, camera.RenderJpeg(telemetry, tail)));
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Rendering video frame {Seq} for {Tail} failed.", telemetry.Seq, tail);
                    }
                }, stoppingToken);
            }
        }
    }
}
