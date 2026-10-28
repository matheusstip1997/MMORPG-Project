using GameServer.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Combat;

namespace GameServer.Combat;

/// <summary>
/// One projectile of spec 3.2 (owner, origin, direction, speed, range, hit radius, maxHits, stopsOnWorld, falloff, the
/// targets it already hit). Pooled by the <see cref="ProjectileSystem"/>: the instances are made once and reused, so their
/// lists keep their capacity. Simulation thread only.
/// <para>
/// Life: live from its spawn in phase 3 until a hit, its range, a wall or its owner leaving ends it at
/// <see cref="EndTick"/>; it then stays in the system for <c>ProjectileSettings.eventRepeatTicks</c> ticks so its
/// despawn reaches every player that got its spawn, and goes back to the pool.
/// </para>
/// </summary>
internal sealed class Projectile
{
    /// <summary>The projectile's own id (not an entity id); ids only grow, so the active list is in id order.</summary>
    public uint Id { get; private set; }

    public Entity Owner { get; private set; } = null!;

    public ProjectileDef Def { get; private set; } = null!;

    /// <summary>The effect row that fired it: its poise and flinch go with every hit (R-3.1-08).</summary>
    public ActionEffectDef Effect { get; private set; } = null!;

    public ProjectilePath Path { get; private set; }

    /// <summary>The owner's height at the spawn, sent with the origin so the client draws it at the right level.</summary>
    public int HeightCm { get; private set; }

    /// <summary>Hits that counted toward <c>maxHits</c>.</summary>
    public int Hits { get; set; }

    /// <summary>Entity ids it may not hit again (no double hits, spec 3.2).</summary>
    public List<uint> HitTargets { get; } = new();

    /// <summary>Players that were sent its spawn, with the peer they were sent to (a reconnect is a new client).</summary>
    public List<ProjectileObserver> Observers { get; } = new();

    public bool IsLive => Reason == ProjectileDespawnReason.None;

    public ProjectileDespawnReason Reason { get; private set; }

    /// <summary>The tick it ended in; meaningless while live.</summary>
    public uint EndTick { get; private set; }

    /// <summary>Where the part of the path still to draw starts: its position before the last phase 4 it flew.</summary>
    public FixVec2 SegmentStart { get; set; }

    /// <summary>Where it is now (after the last phase 4); its path end once it has flown all of it.</summary>
    public FixVec2 Position { get; set; }

    /// <summary>Fired while its owner was blinded (WP-M2-9): it hits nothing and does not explode.</summary>
    public bool Blinded { get; set; }

    /// <summary>
    /// WP-M2-9: the Impact effect placed where it ends (its effect's <c>impactEffectId</c>), with its action's net id and index
    /// for the strike trace; null for none.
    /// </summary>
    public ActionEffectDef? Impact { get; set; }

    public int ImpactActionNetId { get; set; }

    public int ImpactEffectIndex { get; set; }

    public void Start(uint id, Entity owner, ProjectileDef def, ActionEffectDef effect, in ProjectilePath path, int heightCm)
    {
        Id = id;
        Owner = owner;
        Def = def;
        Effect = effect;
        Path = path;
        HeightCm = heightCm;
        Hits = 0;
        HitTargets.Clear();
        Observers.Clear();
        Reason = ProjectileDespawnReason.None;
        EndTick = 0;
        SegmentStart = path.Origin;
        Position = path.Origin;
        Blinded = false;
        Impact = null;
        ImpactActionNetId = 0;
        ImpactEffectIndex = PendingStrike.NoEffectIndex;
    }

    public void End(ProjectileDespawnReason reason, uint tick)
    {
        if (!IsLive)
            return;
        Reason = reason;
        EndTick = tick;
    }

    /// <summary>Drops the references so a pooled projectile keeps no entity alive.</summary>
    public void Release()
    {
        Owner = null!;
        Def = null!;
        Effect = null!;
        Impact = null;
        HitTargets.Clear();
        Observers.Clear();
    }

    public bool HasHit(uint entityId)
    {
        var targets = HitTargets;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] == entityId)
                return true;
        }
        return false;
    }

    /// <summary>The spawn message entry for this projectile.</summary>
    public ProjectileSpawn ToSpawn() => new()
    {
        ProjectileId = Id,
        DefId = (ushort)Def.NetId,
        XCm = FixedPoint.ToCmFloor(Path.Origin.X),
        YCm = FixedPoint.ToCmFloor(Path.Origin.Y),
        HeightCm = HeightCm,
        Facing = Path.Facing,
        SpeedCmPerSec = Path.SpeedCmPerSec,
        StartTick = Path.StartTick,
    };
}

/// <summary>A player a projectile's spawn was sent to, and how many ticks it was sent.</summary>
internal struct ProjectileObserver
{
    public Player Player;
    public int PeerId;
    public int SpawnSends;
}
