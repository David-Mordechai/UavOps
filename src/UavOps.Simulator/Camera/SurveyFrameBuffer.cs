using System.Threading.Channels;
using UavOps.Onboard.Contracts;

namespace UavOps.Simulator.Camera;

public sealed record SurveyFrame(FrameTelemetry Telemetry, byte[] Jpeg);

/// <summary>
/// The survey frames each UAV's camera took while its onboard agent was looking, for the onboard
/// detector to pull in order (<see cref="NextAfterAsync"/>). Frames are taken by distance flown,
/// not by time (see <see cref="SimFleet"/>), so how fast the sim runs changes how quickly they
/// arrive, never how much ground they cover. The oldest are dropped past
/// <see cref="SimOptions.SurveyFramesKept"/>.
/// </summary>
public sealed class SurveyFrameBuffer(SimOptions options)
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
            if (list.Count > options.SurveyFramesKept)
                list.RemoveRange(0, list.Count - options.SurveyFramesKept);
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
    public async Task<SurveyFrame?> NextAfterAsync(string tail, long after, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            Task arrived;
            lock (_lock)
            {
                if (_frames.TryGetValue(tail, out var list) && list.FirstOrDefault(f => f.Telemetry.Seq > after) is { } frame)
                    return frame;
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
