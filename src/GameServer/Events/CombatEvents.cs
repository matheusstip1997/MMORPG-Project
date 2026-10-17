namespace GameServer.Events;

// Combat events (spec 3.1, 3.3, 8.1), emitted by Combat in phases 3, 5 and 8 in entity-ID order. Entity IDs are the
// world's entity IDs (uint, as in snapshots). The envelope's ActorCharacterId is the acting character (0 for monsters).
// Action IDs refer to ActionDef rows.
// TODO(open): the spec does not list payload fields for these events; they hold only what sections 3.1-3.3 and
// R-7.2-02 name. Damage element, crit, and projectile IDs are left out until Combat defines them.

/// <summary>An action started (R-3.1-05): action ID, start tick, the direction fixed at start, and the parry color.</summary>
public readonly record struct ActionStarted(uint EntityId, int ActionId, long StartTick, ushort Facing, ParryColor Color)
    : IGameEvent;

/// <summary>
/// An active hitbox reached a target (spec 3.1 HitResolver).
/// TODO(open): whether a hit absorbed by dodge i-frames still emits HitLanded.
/// </summary>
public readonly record struct HitLanded(uint AttackerId, uint TargetId, int ActionId) : IGameEvent;

/// <summary>A target lost HP (spec 3.4). <see cref="HpAfter"/> is the target's HP once the damage is applied.</summary>
public readonly record struct Damaged(uint SourceId, uint TargetId, int ActionId, int Amount, int HpAfter) : IGameEvent;

/// <summary>
/// The killer's view of a death (spec 8.1: Exp, Demand, Loot, Honor consume it). <see cref="VictimCharacterId"/> is 0 for
/// monsters; <see cref="VictimDefId"/> is the monster type (DemandService counts per type and region), 0 for characters.
/// </summary>
public readonly record struct Killed(uint KillerId, uint VictimId, long VictimCharacterId, int VictimDefId) : IGameEvent;

/// <summary>The victim's view of a death (spec 8.1: loot bag, respawn lock, arrest window). The actor is the victim.</summary>
public readonly record struct Died(uint EntityId, uint KillerId) : IGameEvent;

/// <summary>Poise broke (R-3.3-01); <see cref="SourceId"/> landed the breaking hit.</summary>
public readonly record struct Staggered(uint EntityId, uint SourceId) : IGameEvent;

/// <summary>A parry met an incoming hit (R-3.3-03, R-3.3-04). The actor is the defender.</summary>
public readonly record struct ParryAttempted(uint DefenderId, uint AttackerId, int ActionId, ParryColor Color, ParryResult Result)
    : IGameEvent;

/// <summary>Attack colors (R-3.3-04). Stored in telemetry: append only, never renumber.</summary>
public enum ParryColor : byte
{
    None = 0,
    Red = 1,
    Yellow = 2,
    Blue = 3,
}

/// <summary>Parry outcomes (spec 3.3 ParrySystem). Stored in telemetry: append only, never renumber.</summary>
public enum ParryResult : byte
{
    Success = 1,
    /// <summary>Uncolored attack: damage reduced by the parry value.</summary>
    Reduced = 2,
    FailedRed = 3,
    FailedArmor = 4,
    FailedTiming = 5,
    NoCharge = 6,
}
