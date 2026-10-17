namespace GameServer.Host;

/// <summary>
/// Runs a tick at a fixed rate (spec 1.6, architecture rule 12: 20 Hz from <c>StartingValuesM1.TickRateHz</c>).
/// <para>
/// Tick n starts at <c>start + n * 1 s / rate</c>, computed from the start each time, never by adding the time just
/// waited, so waking late never shifts the ticks after it (no drift). A late tick runs at once. When a tick starts a
/// full period or more late (the process stalled, or ticks run longer than the budget), the loop does not burst to
/// catch up: it counts an overrun and restarts the schedule from now.
/// </para>
/// <para>
/// Stats are written by the tick thread; read them on that thread or after <see cref="Run"/> returns.
/// <c>Telemetry.WorldTelemetry</c> records them in phase 12 (tick timing per stats interval).
/// </para>
/// </summary>
public sealed class TickLoop
{
    private const long MicrosPerSecond = 1_000_000;

    private readonly ITickClock _clock;
    private readonly int _tickRateHz;
    private readonly Action _tick;
    private long _scheduleStart;
    private long _ticksSinceScheduleStart;
    private bool _started;

    public TickLoop(ITickClock clock, int tickRateHz, Action tick)
    {
        if (tickRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(tickRateHz), "must be positive");
        _clock = clock;
        _tickRateHz = tickRateHz;
        _tick = tick;
    }

    /// <summary>Length of one tick, rounded down to whole microseconds (the schedule itself does not round).</summary>
    public long PeriodMicros => MicrosPerSecond / _tickRateHz;

    public long TicksRun { get; private set; }

    /// <summary>Times the schedule was restarted because a tick started a full period or more late.</summary>
    public long Overruns { get; private set; }

    public long LastTickMicros { get; private set; }

    public long MaxTickMicros { get; private set; }

    /// <summary>When the last tick started, compared with its scheduled time.</summary>
    public long LastStartLatenessMicros { get; private set; }

    /// <summary>Runs ticks until <paramref name="cancellation"/> is set.</summary>
    public void Run(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
            RunOne(cancellation);
    }

    /// <summary>Waits for the next scheduled time and runs one tick. The first call runs at once.</summary>
    public void RunOne(CancellationToken cancellation = default)
    {
        if (!_started)
        {
            _scheduleStart = _clock.NowMicros;
            _ticksSinceScheduleStart = 0;
            _started = true;
        }

        long due = _scheduleStart + _ticksSinceScheduleStart * MicrosPerSecond / _tickRateHz;
        _clock.WaitUntil(due, cancellation);
        if (cancellation.IsCancellationRequested)
            return;

        long started = _clock.NowMicros;
        long lateness = started - due;
        if (lateness >= PeriodMicros)
        {
            Overruns++;
            _scheduleStart = started;
            _ticksSinceScheduleStart = 0;
            lateness = 0;
        }

        _tick();

        long duration = _clock.NowMicros - started;
        TicksRun++;
        _ticksSinceScheduleStart++;
        LastStartLatenessMicros = lateness;
        LastTickMicros = duration;
        if (duration > MaxTickMicros)
            MaxTickMicros = duration;
    }
}
