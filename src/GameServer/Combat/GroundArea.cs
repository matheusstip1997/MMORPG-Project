using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Combat;

/// <summary>
/// One ground area (spec 3.1 <c>GroundArea</c> effect, R-3.1-07): its owner, effect, shapes placed at the owner's position
/// and locked facing when it fired, and its ticks. Pooled by the <see cref="GroundAreaSystem"/>: instances are made once
/// and reused, so their lists keep their capacity. Simulation thread only.
/// <para>
/// Life: placed in phase 3 of <see cref="StartTick"/>, it covers ticks <see cref="StartTick"/> to
/// <c>StartTick + DurationTicks - 1</c> and ticks at <c>StartTick + k x IntervalTicks</c> inside that span (the first on the
/// tick it was placed). It ends in phase 5 of <c>StartTick + DurationTicks</c> (<c>Expired</c>), or earlier when its owner
/// dies or leaves the world; it then stays <c>GroundAreaSettings.eventRepeatTicks</c> ticks for its despawn and goes back
/// to the pool.
/// </para>
/// </summary>
internal sealed class GroundArea
{
    /// <summary>The area's own id (not an entity id); ids only grow, so the active list is in id order.</summary>
    public uint Id { get; private set; }

    public Entity Owner { get; private set; } = null!;

    /// <summary>The effect row: damage, poise, element and heal of every tick, and the targets filter.</summary>
    public ActionEffectDef Effect { get; private set; } = null!;

    public EffectShapes Shapes { get; private set; } = null!;

    /// <summary>The action's <c>ActionDef.netId</c> and the effect's index in it (the spawn message names the effect so).</summary>
    public int ActionNetId { get; private set; }

    public int EffectIndex { get; private set; }

    /// <summary>Where the shapes are placed: the owner's position on whole cm when it fired.</summary>
    public FixVec2 Origin { get; private set; }

    /// <summary>The owner's locked facing at the wire's resolution, so clients place the same shapes.</summary>
    public ushort Facing { get; private set; }

    public int HeightCm { get; private set; }

    public uint StartTick { get; private set; }

    /// <summary><c>durationMs</c> rounded up to whole ticks.</summary>
    public int DurationTicks { get; private set; }

    /// <summary><c>tickIntervalMs</c> rounded up to whole ticks (at least one).</summary>
    public int IntervalTicks { get; private set; }

    /// <summary>A per-tick heal: <c>healPercent</c> of the owner's Matk read when the area was placed.</summary>
    public int HealAmount { get; private set; }

    /// <summary>Players that were sent its spawn, with the peer they were sent to (a reconnect is a new client).</summary>
    public List<GroundAreaObserver> Observers { get; } = new();

    public bool IsLive => Reason == GroundAreaDespawnReason.None;

    public GroundAreaDespawnReason Reason { get; private set; }

    /// <summary>The tick it ended in, the first tick it is not drawn; meaningless while live.</summary>
    public uint EndTick { get; private set; }

    /// <summary>Ticks it pulsed (damage or heal) so far.</summary>
    public int Pulses { get; set; }

    public void Start(uint id, Entity owner, ActionEffectDef effect, EffectShapes shapes, int actionNetId, int effectIndex,
        FixVec2 origin, ushort facing, int heightCm, uint startTick, int durationTicks, int intervalTicks, int healAmount)
    {
        Id = id;
        Owner = owner;
        Effect = effect;
        Shapes = shapes;
        ActionNetId = actionNetId;
        EffectIndex = effectIndex;
        Origin = origin;
        Facing = facing;
        HeightCm = heightCm;
        StartTick = startTick;
        DurationTicks = durationTicks;
        IntervalTicks = intervalTicks;
        HealAmount = healAmount;
        Pulses = 0;
        Observers.Clear();
        Reason = GroundAreaDespawnReason.None;
        EndTick = 0;
    }

    /// <summary>True when <paramref name="tick"/> is past the area's span (it expires in this tick's phase 5).</summary>
    public bool ExpiredAt(uint tick) => (int)(tick - StartTick) >= DurationTicks;

    /// <summary>True when the area pulses on <paramref name="tick"/> (inside its span, on its interval from the start).</summary>
    public bool PulsesAt(uint tick)
    {
        int age = (int)(tick - StartTick);
        return age >= 0 && age < DurationTicks && age % IntervalTicks == 0;
    }

    public void End(GroundAreaDespawnReason reason, uint tick)
    {
        if (!IsLive)
            return;
        Reason = reason;
        EndTick = tick;
    }

    /// <summary>Drops the references so a pooled area keeps no entity alive.</summary>
    public void Release()
    {
        Owner = null!;
        Effect = null!;
        Shapes = null!;
        Observers.Clear();
    }

    /// <summary>The spawn message entry for this area.</summary>
    public GroundAreaSpawn ToSpawn() => new()
    {
        AreaId = Id,
        ActionId = (ushort)ActionNetId,
        EffectIndex = (byte)EffectIndex,
        XCm = FixedPoint.ToCmFloor(Origin.X),
        YCm = FixedPoint.ToCmFloor(Origin.Y),
        HeightCm = HeightCm,
        Facing = Facing,
        StartTick = StartTick,
    };
}

/// <summary>A player a ground area's spawn was sent to, and how many ticks it was sent.</summary>
internal struct GroundAreaObserver
{
    public Player Player;
    public int PeerId;
    public int SpawnSends;
}
