using System.Diagnostics;

namespace GameServer.Host;

/// <summary>
/// Monotonic time for the tick schedule (spec 1.6). Separate from <see cref="Shared.Util.IClock"/>, which is UTC wall
/// time for timestamps and can jump when the system clock is corrected; a schedule driven by it would stall or burst.
/// Tests use a fake whose <see cref="WaitUntil"/> moves time forward.
/// </summary>
public interface ITickClock
{
    /// <summary>Microseconds since an arbitrary fixed point; never goes back.</summary>
    long NowMicros { get; }

    /// <summary>Returns once <see cref="NowMicros"/> reaches <paramref name="micros"/>, or early on cancellation.</summary>
    void WaitUntil(long micros, CancellationToken cancellation);
}

/// <summary>
/// <see cref="ITickClock"/> over <see cref="Stopwatch"/>. Sleeps in 1 ms steps while the deadline is far, then yields
/// until it arrives, so a tick starts on time without burning a core for the whole wait.
/// On Windows a 1 ms sleep can last a full system timer period (about 15.6 ms); the tick then starts late within the
/// period, and the absolute schedule in <see cref="TickLoop"/> keeps that lateness from adding up.
/// </summary>
public sealed class StopwatchTickClock : ITickClock
{
    /// <summary>Below this much remaining time the wait yields instead of sleeping.</summary>
    private const long YieldBelowMicros = 2_000;

    private const long MicrosPerSecond = 1_000_000;

    public long NowMicros
    {
        get
        {
            long timestamp = Stopwatch.GetTimestamp();
            long frequency = Stopwatch.Frequency;
            // Split so timestamp * 1e6 cannot overflow.
            return timestamp / frequency * MicrosPerSecond + timestamp % frequency * MicrosPerSecond / frequency;
        }
    }

    public void WaitUntil(long micros, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            long remaining = micros - NowMicros;
            if (remaining <= 0)
                return;
            if (remaining > YieldBelowMicros)
                Thread.Sleep(1);
            else
                Thread.Yield();
        }
    }
}
