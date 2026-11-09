using GameServer.Events;

namespace GameServer.Economy;

/// <summary>Gold created and destroyed and items destroyed for one <see cref="InventoryReason"/> within a snapshot's period.</summary>
public readonly record struct EconomyReasonDelta(InventoryReason Reason, long GoldCreated, long GoldDestroyed, long ItemsDestroyed);

/// <summary>
/// One economy snapshot (R-7.2-02, spec 7.1 <c>faucet_sink_snapshots</c>; WP-M4-5): the gold created and destroyed between
/// <see cref="PeriodStartMs"/> and <see cref="TakenAtMs"/> (UTC ms, <c>IClock</c>), by reason (only the reasons that moved),
/// the gold the online characters held and the active players at the control step.
/// </summary>
public sealed record EconomySnapshot(long TakenAtMs, long PeriodStartMs, long GoldCreated, long GoldDestroyed, long GoldOnline,
    int ActivePlayers, IReadOnlyList<EconomyReasonDelta> ByReason);

/// <summary>
/// Turns the <see cref="EconomyLedger"/>'s running totals into snapshots of what changed since the previous one (WP-M4-5).
/// The world asks after each demand control step (phase 7); a snapshot is due at the first step and then at the first step
/// at least <c>EconomySettings.snapshotIntervalMs</c> after the previous snapshot. World thread only; the snapshot goes to a
/// background writer (<c>Persistence/EconomySnapshotWorker</c>), never to the database from here.
/// </summary>
public sealed class EconomySnapshots
{
    private readonly EconomyLedger _ledger;
    private readonly long _intervalMs;
    private readonly long[] _created = new long[EconomyLedger.ReasonCount];
    private readonly long[] _destroyed = new long[EconomyLedger.ReasonCount];
    private readonly long[] _items = new long[EconomyLedger.ReasonCount];
    private long _lastStep;
    private long _lastTakenMs;
    private long _goldCreated;
    private long _goldDestroyed;
    private bool _taken;

    /// <param name="startedMs">The world's start, the first snapshot's period start.</param>
    public EconomySnapshots(EconomyLedger ledger, long intervalMs, long startedMs)
    {
        if (intervalMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _ledger = ledger;
        _intervalMs = intervalMs;
        _lastTakenMs = startedMs;
    }

    /// <summary>
    /// A snapshot when the demand loop ran a control step since the last call (<paramref name="controlSteps"/>, from
    /// <c>DemandService.Steps</c>) and the interval passed since the previous snapshot; otherwise null.
    /// </summary>
    public EconomySnapshot? TryTake(long controlSteps, long nowMs, long goldOnline, int activePlayers) =>
        Due(controlSteps, nowMs) ? Take(nowMs, goldOnline, activePlayers) : null;

    /// <summary>
    /// True when a snapshot is due: the demand loop ran a control step since the last call (<paramref name="controlSteps"/>)
    /// and the interval passed since the previous snapshot. The world asks this every tick and gathers the gold online (a
    /// sum over the attached characters) only when it is true, then calls <see cref="Take"/>.
    /// </summary>
    public bool Due(long controlSteps, long nowMs)
    {
        if (controlSteps == _lastStep)
            return false;
        _lastStep = controlSteps;
        return !_taken || nowMs - _lastTakenMs >= _intervalMs;
    }

    /// <summary>
    /// The last snapshot when the world stops: what changed since the previous one, or null when nothing did. Its time is
    /// strictly after the previous snapshot's, so it never collides with that row (unique per world and time) and is never
    /// dropped as a repeat.
    /// </summary>
    public EconomySnapshot? TakeFinal(long nowMs, long goldOnline, int activePlayers)
    {
        if (_ledger.GoldCreated == _goldCreated && _ledger.GoldDestroyed == _goldDestroyed && !ItemsChanged())
            return null;
        return Take(Math.Max(nowMs, _taken ? _lastTakenMs + 1 : _lastTakenMs), goldOnline, activePlayers);
    }

    private bool ItemsChanged()
    {
        for (int reason = 1; reason < _items.Length; reason++)
        {
            if (_ledger.ItemsDestroyedBy((InventoryReason)reason) != _items[reason])
                return true;
        }
        return false;
    }

    /// <summary>The snapshot of what changed since the previous one, taken at <paramref name="nowMs"/> (after <see cref="Due"/>).</summary>
    public EconomySnapshot Take(long nowMs, long goldOnline, int activePlayers)
    {
        var byReason = new List<EconomyReasonDelta>();
        for (int value = 1; value < _created.Length; value++)
        {
            var reason = (InventoryReason)value;
            long created = _ledger.GoldCreatedBy(reason), destroyed = _ledger.GoldDestroyedBy(reason), items = _ledger.ItemsDestroyedBy(reason);
            var delta = new EconomyReasonDelta(reason, created - _created[value], destroyed - _destroyed[value], items - _items[value]);
            if (delta.GoldCreated != 0 || delta.GoldDestroyed != 0 || delta.ItemsDestroyed != 0)
                byReason.Add(delta);
            _created[value] = created;
            _destroyed[value] = destroyed;
            _items[value] = items;
        }
        var snapshot = new EconomySnapshot(nowMs, _lastTakenMs, _ledger.GoldCreated - _goldCreated, _ledger.GoldDestroyed - _goldDestroyed,
            goldOnline, activePlayers, byReason);
        _goldCreated = _ledger.GoldCreated;
        _goldDestroyed = _ledger.GoldDestroyed;
        _lastTakenMs = nowMs;
        _taken = true;
        return snapshot;
    }
}
