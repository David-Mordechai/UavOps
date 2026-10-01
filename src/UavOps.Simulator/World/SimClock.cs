namespace UavOps.Simulator.World;

/// <summary>
/// Simulated time: seconds since the simulator started, advancing with the time scale (the page's
/// 1×-30× buttons). Everything that moves on the ground is a function of it, so any moment can be
/// drawn again - a survey frame is rendered after it was captured, and a close-up of a candidate is
/// taken as at the frame it was seen in.
///
/// Frames carry a wall-clock capture time (<c>FrameTelemetry.CapturedAtUtc</c>, part of the
/// detector's contract), so the clock keeps the recent (wall, sim) pairs and converts between them.
/// </summary>
public sealed class SimClock
{
    private const int Kept = 12_000; // ~20 minutes of 100 ms ticks

    private readonly object _lock = new();
    private readonly (DateTime Utc, double Sim)[] _history = new (DateTime, double)[Kept];
    private int _count;
    private int _next;
    private double _now;

    public double Now
    {
        get { lock (_lock) return _now; }
    }

    /// <summary>Called by the tick loop with the simulated seconds it just advanced.</summary>
    public void Advance(double seconds, DateTime nowUtc)
    {
        lock (_lock)
        {
            _now += Math.Max(seconds, 0);
            _history[_next] = (nowUtc, _now);
            _next = (_next + 1) % Kept;
            _count = Math.Min(_count + 1, Kept);
        }
    }

    /// <summary>The simulated time at a wall-clock moment: interpolated between ticks, clamped to
    /// what's still remembered, and <see cref="Now"/> for anything after the last tick.</summary>
    public double At(DateTime utc)
    {
        lock (_lock)
        {
            if (_count == 0)
                return _now;
            var newest = (_next - 1 + Kept) % Kept;
            if (utc >= _history[newest].Utc)
                return _history[newest].Sim;
            var oldest = (_next - _count + Kept) % Kept;
            if (utc <= _history[oldest].Utc)
                return _history[oldest].Sim;

            // Binary search over the ring, oldest (0) to newest (_count - 1).
            int lo = 0, hi = _count - 1;
            while (hi - lo > 1)
            {
                var mid = (lo + hi) / 2;
                if (_history[(oldest + mid) % Kept].Utc <= utc)
                    lo = mid;
                else
                    hi = mid;
            }
            var a = _history[(oldest + lo) % Kept];
            var b = _history[(oldest + hi) % Kept];
            var span = (b.Utc - a.Utc).TotalSeconds;
            return span <= 0 ? b.Sim : a.Sim + (b.Sim - a.Sim) * (utc - a.Utc).TotalSeconds / span;
        }
    }
}
