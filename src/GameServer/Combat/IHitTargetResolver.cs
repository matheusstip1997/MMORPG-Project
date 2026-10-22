using GameServer.Entities;
using Shared.Data.Tables;

namespace GameServer.Combat;

/// <summary>Where a hit comes from (spec 3.1 HitResolver, 3.2 projectiles).</summary>
internal enum HitSource : byte
{
    Strike = 1,
    Projectile = 2,
    GroundArea = 3,
}

/// <summary>What resolving one hit on one target did.</summary>
internal enum HitOutcome : byte
{
    Ignored = 0,
    Damaged = 1,
    Killed = 2,
    Dodged = 3,
    Parried = 4,
}

/// <summary>One hit on one target: the effect row carries the poise damage, flinch and damage multiplier.</summary>
internal readonly record struct HitRequest(Entity Attacker, Entity Target, ActionEffectDef Effect, HitSource Source, uint Tick, int DamagePercent);

/// <summary>The per-target hit pipeline of spec 3.1, shared by strikes (phase 5) and projectiles (phase 4).</summary>
internal interface IHitTargetResolver
{
    bool CanHit(Entity attacker, Entity target);

    HitOutcome Resolve(in HitRequest request);
}
