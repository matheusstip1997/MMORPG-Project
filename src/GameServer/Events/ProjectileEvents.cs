namespace GameServer.Events;

// Projectile events (spec 3.2 "emits ProjectileSpawned, ProjectileHit"), published by the ProjectileSystem in phases 3 and
// 4 in projectile-ID order. Projectile IDs are the world's projectile IDs (uint, as in the projectile hot messages), not
// entity IDs. The envelope's ActorCharacterId is the owner's character (0 for monsters).
// TODO(open): spec 3.2 lists no payload fields; these hold what the spawn event and the hit name. Damage, kills and parries
// are the HitResolver's events (Damaged, Killed, ParryAttempted).

/// <summary>A projectile left its owner (spec 3.2): its <c>ProjectileDef.netId</c> and the tick it was spawned in.</summary>
public readonly record struct ProjectileSpawned(uint ProjectileId, uint OwnerId, int DefId, long StartTick) : IGameEvent;

/// <summary>A projectile's hit on a target counted toward its <c>maxHits</c> (R-3.2-01): it was damaged, killed or parried.</summary>
public readonly record struct ProjectileHit(uint ProjectileId, uint OwnerId, uint TargetId, int DefId) : IGameEvent;
