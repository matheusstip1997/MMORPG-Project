using GameServer.Entities;
using Shared.Data.Tables;

namespace GameServer.Combat;

/// <summary>Where a hit comes from (spec 3.1 HitResolver, 3.2 projectiles).</summary>
internal enum HitSource : byte
{
    Strike = 1,
    Projectile = 2,
    GroundArea = 3,
    /// <summary>A status's periodic damage (spec 3.7): not a hit, no dodge, parry, poise or flinch.</summary>
    Status = 4,
}

/// <summary>What resolving one hit on one target did.</summary>
internal enum HitOutcome : byte
{
    Ignored = 0,
    Damaged = 1,
    Killed = 2,
    Dodged = 3,
    Parried = 4,
    /// <summary>
    /// A monster's hit on a player waits for the defender's dodge or parry input (R-3.1-03 pending hit); it is resolved
    /// in a later phase 5. Projectiles count it as a hit (spec 3.2).
    /// </summary>
    Pending = 5,
}

/// <summary>
/// One hit on one target: the effect row carries the poise damage, flinch and damage multiplier. <see cref="Tick"/> is the
/// server tick the hit happened on; a pending hit is judged against the defender's view of that tick.
/// </summary>
internal readonly record struct HitRequest(Entity Attacker, Entity Target, ActionEffectDef Effect, HitSource Source, uint Tick, int DamagePercent);

/// <summary>The per-target hit pipeline of spec 3.1, shared by strikes (phase 5) and projectiles (phase 4).</summary>
internal interface IHitTargetResolver
{
    bool CanHit(Entity attacker, Entity target);

    HitOutcome Resolve(in HitRequest request);
}
