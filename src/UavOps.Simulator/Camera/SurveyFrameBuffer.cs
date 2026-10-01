using System.Threading.Channels;
using UavOps.Onboard.Contracts;

namespace UavOps.Simulator.Camera;

public sealed record SurveyFrame(FrameTelemetry Telemetry, byte[] Jpeg);

/// <summary>
/// The survey frames each UAV's camera took while its onboard agent was looking, for the onboard
/// detector to pull in order (<see cref="FrameBuffer.NextAfterAsync"/>). Frames are taken by distance flown,
/// not by time (see <see cref="SimFleet"/>), so how fast the sim runs changes how quickly they
/// arrive, never how much ground they cover. The oldest are dropped past
/// <see cref="SimOptions.SurveyFramesKept"/>.
/// </summary>
public sealed class SurveyFrameBuffer(SimOptions options) : FrameBuffer(options.SurveyFramesKept);

/// <summary>
/// The payload's live video for the onboard computer's every-frame detector
/// (<see cref="OnboardVideoWorker"/>): read newest-first (<see cref="FrameBuffer.NewestAfterAsync"/>),
/// since a real-time loop that falls behind skips frames rather than working through a backlog.
/// </summary>
public sealed class VideoFrameBuffer(SimOptions options) : FrameBuffer(options.VideoFramesKept);

/// <summary>Frames per UAV, in the order taken, the oldest dropped past a count.</summary>
public abstract class FrameBuffer(int kept)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, List<SurveyFrame>> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource> _arrived = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string tail, SurveyFrame frame)
    {
        TaskCompletionSource? signal;
        lock (_lock)
        {
            if (!_frames.TryGetValue(tail, out var list))
                _frames[tail] = list = [];
            list.Add(frame);
            if (list.Count > kept)
                list.RemoveRange(0, list.Count - kept);
            _arrived.Remove(tail, out signal);
        }
        signal?.TrySetResult();
    }

    public SurveyFrame? Get(string tail, long seq)
    {
        lock (_lock)
            return _frames.TryGetValue(tail, out var list) ? list.FirstOrDefault(f => f.Telemetry.Seq == seq) : null;
    }

    /// <summary>The first frame after <paramref name="after"/>, waiting up to <paramref name="wait"/>
    /// for one to be taken; null if none came.</summary>
    public Task<SurveyFrame?> NextAfterAsync(string tail, long after, TimeSpan wait, CancellationToken cancellationToken) =>
        AfterAsync(tail, after, wait, newest: false, cancellationToken);

    /// <summary>The newest frame after <paramref name="after"/>, waiting up to <paramref name="wait"/>.</summary>
    public Task<SurveyFrame?> NewestAfterAsync(string tail, long after, TimeSpan wait, CancellationToken cancellationToken) =>
        AfterAsync(tail, after, wait, newest: true, cancellationToken);

    private async Task<SurveyFrame?> AfterAsync(string tail, long after, TimeSpan wait, bool newest, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            Task arrived;
            lock (_lock)
            {
                if (_frames.TryGetValue(tail, out var list) && list.Count > 0)
                {
                    var frame = newest ? list[^1] : list.FirstOrDefault(f => f.Telemetry.Seq > after);
                    if (frame is not null && frame.Telemetry.Seq > after)
                        return frame;
                }
                if (!_arrived.TryGetValue(tail, out var signal))
                    _arrived[tail] = signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                arrived = signal.Task;
            }
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return null;
            try
            {
                await arrived.WaitAsync(remaining, cancellationToken);
            }
            catch (TimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>How many frames after <paramref name="after"/> are waiting to be pulled.</summary>
    public int CountAfter(string tail, long after)
    {
        lock (_lock)
            return _frames.TryGetValue(tail, out var list) ? list.Count(f => f.Telemetry.Seq > after) : 0;
    }

    public void Clear()
    {
        lock (_lock)
            _frames.Clear();
    }
}

/// <summary>Renders survey frames off the sim tick (a frame takes tens of milliseconds, the tick
/// must not wait), in the order they were taken.</summary>
public sealed class SurveyCameraWorker(CameraRenderer camera, SurveyFrameBuffer buffer, ILogger<SurveyCameraWorker> logger) : BackgroundService
{
    private readonly Channel<(string Tail, FrameTelemetry Telemetry)> _queue = Channel.CreateUnbounded<(string, FrameTelemetry)>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Capture(string tail, FrameTelemetry telemetry) => _queue.Writer.TryWrite((tail, telemetry));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (tail, telemetry) in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                buffer.Add(tail, new SurveyFrame(telemetry, camera.RenderJpeg(telemetry, tail)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Rendering survey frame {Seq} for {Tail} failed.", telemetry.Seq, tail);
            }
        }
    }
}
