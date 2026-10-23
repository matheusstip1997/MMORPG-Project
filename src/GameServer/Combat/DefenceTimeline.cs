using Shared.Data.Tables;
using Shared.Simulation.Actions;

namespace GameServer.Combat;

/// <summary>
/// The clock-sync limits of R-3.1-03: how far behind the server a client may claim to see the world. At most the cap
/// (<c>lagCompensationCapMs</c>), and at most the measured round trip plus <c>interpolationDelayMs</c> plus
/// <c>clockSyncToleranceMs</c> once the transport has measured a round trip.
/// </summary>
internal readonly struct ClockSyncLimits
{
    public ClockSyncLimits(int tickRateHz, int capTicks, int interpolationDelayMs, int toleranceMs)
    {
        TickRateHz = tickRateHz;
        CapTicks = capTicks;
        InterpolationDelayMs = interpolationDelayMs;
        ToleranceMs = toleranceMs;
    }

    public int TickRateHz { get; }
    public int CapTicks { get; }
    public int InterpolationDelayMs { get; }
    public int ToleranceMs { get; }

    /// <summary>The largest view lag a client may claim, in 1/64 ticks; only the cap without an RTT sample (-1).</summary>
    public long MaxLag64(int roundTripMs)
    {
        long cap = (long)CapTicks * DefenceTimeline.SubTicks;
        if (roundTripMs < 0)
            return cap;
        long ms = (long)roundTripMs + InterpolationDelayMs + ToleranceMs;
        long lag = (ms * TickRateHz * DefenceTimeline.SubTicks + 999) / 1000;
        return Math.Min(cap, lag);
    }
}

/// <summary>A dodge or parry the defender started (or a parry press refused for want of a charge), on its own timeline.</summary>
internal struct DefenceStart
{
    public int ActionIndex;
    /// <summary>Server tick the action started on.</summary>
    public uint StartTick;
    /// <summary>When it started on the defender's view clock, in 1/64 ticks (<see cref="DefenceTimeline.SubTicks"/>).</summary>
    public long ViewStart64;
    /// <summary>Input sequence applied on the start tick (the parry result echoes it).</summary>
    public ushort Sequence;
    /// <summary>The server machine no longer runs it; <see cref="RanTicks"/> is then how long it ran.</summary>
    public bool Ended;
    public int RanTicks;
    /// <summary>R-3.3-03: a parry press refused with <c>ParryNoCharge</c>; it covers the window the parry would have had.</summary>
    public bool NoCharge;
}

/// <summary>
/// One player's timeline for defence (R-3.1-03, R-3.3-02): monsters' hits on it are judged on what it saw, not on the
/// server's clock. Phase 1 turns each applied frame's view clock (<c>ViewTick + SubTick / 64</c>) into a view lag under
/// the clock-sync limits:
/// <list type="bullet">
/// <item>no view yet (0) or a view of a tick not run yet (impossible) gives lag 0;</item>
/// <item>the view clock never goes back: an older claim is raised to the last applied one;</item>
/// <item>the lag is at most <see cref="ClockSyncLimits.MaxLag64"/> (round trip, interpolation and tolerance, and the cap).</item>
/// </list>
/// Phase 3 records each dodge or parry start with when it started on the view clock (the start tick minus the lag), and
/// how long the server machine really ran it, so a dodge a stagger cut short covers only those ticks. A pending hit is
/// judged against them (<see cref="TryJudge"/>). A ring of <see cref="Capacity"/> starts covers the 200 ms cap plus the
/// longest defensive action at 20 Hz. Allocation-free. World simulation thread only.
/// </summary>
internal sealed class DefenceTimeline
{
    /// <summary>Resolution of the view clock: <c>InputFrame.SubTick</c> is in 1/64 of a tick.</summary>
    public const int SubTicks = 64;
    public const int Capacity = 4;

    private readonly DefenceStart[] _starts = new DefenceStart[Capacity];
    private int _count;
    private int _newest = -1;
    private long _lastView64;
    private bool _hasView;

    /// <summary>View lag of the newest applied frame after the limits, in 1/64 ticks.</summary>
    public long Lag64 { get; private set; }

    /// <summary>The lag in whole ticks, rounded down (the attacker rewind, <c>Player.ViewLagTicks</c>).</summary>
    public int LagTicks => (int)(Lag64 / SubTicks);

    /// <summary>The lag in whole ticks, rounded up: a pending hit waits this long for the defender's input.</summary>
    public int LagTicksCeil => (int)((Lag64 + SubTicks - 1) / SubTicks);

    /// <summary>Frames that claimed to see a tick not run yet.</summary>
    public long FutureClaims { get; private set; }

    /// <summary>Frames whose view clock went back.</summary>
    public long BackwardClaims { get; private set; }

    /// <summary>Frames that claimed more lag than the clock-sync limit allows.</summary>
    public long TooOldClaims { get; private set; }

    public int Count => _count;

    /// <summary>The <paramref name="age"/>-th newest start (0 = newest).</summary>
    public DefenceStart this[int age] => _starts[(_newest - age + Capacity) % Capacity];

    /// <summary>Phase 1: the frame applied at <paramref name="tick"/>, with the peer's round trip (-1 when unknown).</summary>
    public void OnFrame(uint tick, uint viewTick, byte subTick, int roundTripMs, in ClockSyncLimits limits)
    {
        if (viewTick == 0)
        {
            Lag64 = 0;
            return;
        }
        // The lag is the wrapping difference of the ticks, as the 32-bit wire ticks wrap.
        long tick64 = (long)tick * SubTicks;
        long claimed = (long)(int)(tick - viewTick) * SubTicks - Math.Min((int)subTick, SubTicks - 1);
        if (claimed <= 0)
        {
            FutureClaims++;
            Lag64 = 0;
            return;
        }
        long view64 = tick64 - claimed;
        if (_hasView && view64 < _lastView64)
        {
            BackwardClaims++;
            view64 = _lastView64;
        }
        long lag = Math.Max(0, tick64 - view64);
        long max = limits.MaxLag64(roundTripMs);
        if (lag > max)
        {
            TooOldClaims++;
            lag = max;
            view64 = tick64 - lag;
        }
        _lastView64 = view64;
        _hasView = true;
        Lag64 = lag;
    }

    /// <summary>
    /// Phase 3, after the machine stepped: closes the newest start if the machine no longer runs it, then records a dodge
    /// or parry that started this tick (a buffered one too, at this tick's lag).
    /// </summary>
    public void Step(ActionMachineState machine, uint tick, int startedIndex, ushort sequence)
    {
        for (int i = 0; i < _count; i++)
        {
            ref var s = ref _starts[i];
            if (!s.Ended && !(machine.ActionIndex == s.ActionIndex && machine.Elapsed == (int)(tick - s.StartTick)))
            {
                s.Ended = true;
                s.RanTicks = (int)(tick - s.StartTick);
            }
        }
        if (startedIndex < 0)
            return;
        var kind = machine.Catalog[startedIndex].Kind;
        if (kind != ActionKind.Dodge && kind != ActionKind.Parry)
            return;
        Push(new DefenceStart
        {
            ActionIndex = startedIndex, StartTick = tick, ViewStart64 = (long)tick * SubTicks - Lag64, Sequence = sequence,
        });
    }

    /// <summary>Phase 3: a parry press was refused with <c>ParryNoCharge</c> (R-3.3-03).</summary>
    public void RefusedNoCharge(ActionTiming parry, uint tick, ushort sequence) =>
        Push(new DefenceStart
        {
            ActionIndex = parry.Index, StartTick = tick, ViewStart64 = (long)tick * SubTicks - Lag64, Sequence = sequence,
            Ended = true, RanTicks = parry.TotalTicks, NoCharge = true,
        });

    /// <summary>
    /// The defence that covers a hit at server tick <paramref name="hitTick"/> on this timeline: the newest start whose
    /// span on the view clock contains the hit, with the action tick the hit falls on. False when none covers it, or when
    /// the server machine stopped that action before reaching that tick.
    /// </summary>
    public bool TryJudge(uint hitTick, ActionCatalog catalog, out DefenceStart start, out int elapsedTicks)
    {
        long hit64 = (long)hitTick * SubTicks;
        for (int age = 0; age < _count; age++)
        {
            var s = this[age];
            long e64 = hit64 - s.ViewStart64;
            if (e64 < 0 || e64 >= (long)catalog[s.ActionIndex].TotalTicks * SubTicks)
                continue;
            elapsedTicks = (int)(e64 / SubTicks);
            start = s;
            return !s.Ended || elapsedTicks < s.RanTicks;
        }
        start = default;
        elapsedTicks = 0;
        return false;
    }

    /// <summary>A new connection (or none): no view, no lag, no starts.</summary>
    public void Reset()
    {
        _count = 0;
        _newest = -1;
        _hasView = false;
        _lastView64 = 0;
        Lag64 = 0;
    }

    private void Push(in DefenceStart start)
    {
        _newest = (_newest + 1) % Capacity;
        _starts[_newest] = start;
        if (_count < Capacity)
            _count++;
    }
}
