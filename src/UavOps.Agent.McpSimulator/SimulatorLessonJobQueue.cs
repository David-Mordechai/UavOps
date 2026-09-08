using System.Threading.Channels;

namespace UavOps.Agent.McpSimulator;

/// <summary>
/// <see cref="System.Threading.Channels.Channel{T}"/>-backed implementation of
/// <see cref="ISimulatorLessonJobQueue"/> — the standard .NET producer/consumer primitive.
/// Unbounded: enqueuing never blocks or fails on a full queue (lesson jobs are rare/manual, not a
/// high-throughput stream).
/// </summary>
public sealed class SimulatorLessonJobQueue : ISimulatorLessonJobQueue
{
    private readonly Channel<SimulatorLessonJob> _channel = Channel.CreateUnbounded<SimulatorLessonJob>();

    public void Enqueue(SimulatorLessonJob job) => _channel.Writer.TryWrite(job);

    public IAsyncEnumerable<SimulatorLessonJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}
