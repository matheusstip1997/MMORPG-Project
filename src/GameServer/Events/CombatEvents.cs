using Shared.Data.Tables;

namespace GameServer.Events;

// Combat events (spec 3.1, 3.3, 8.1), emitted by Combat in phases 3, 5 and 8 in entity-ID order. Entity IDs are the
// world's entity IDs (uint, as in snapshots). The envelope's ActorCharacterId is the acting character (0 for monsters).
// Action IDs refer to ActionDef rows.
// TODO(open): the spec does not list payload fields for these events; they hold only what sections 3.1-3.3 and
// R-7.2-02 name, plus the crit flag HitResolver rolls. Damage element and projectile IDs are left out until Combat defines them.

/// <summary>An action started (R-3.1-05): action ID, start tick, the direction fixed at start, and the parry color.</summary>
public readonly record struct ActionStarted(uint EntityId, int ActionId, long StartTick, ushort Facing, ParryColor Color)
    : IGameEvent;

/// <summary>
/// An active hitbox reached a target (spec 3.1 HitResolver).
/// A hit absorbed by dodge i-frames does not emit it for now. TODO(open): whether it should.
/// </summary>
public readonly record struct HitLanded(uint AttackerId, uint TargetId, int ActionId) : IGameEvent;

/// <summary>
/// A target lost HP (spec 3.4). <see cref="Amount"/> is the HP it lost (the hit's damage, at most the HP it had);
/// <see cref="HpAfter"/> is its HP once the damage is applied; <see cref="Crit"/> whether the crit roll succeeded (R-3.4-04).
/// </summary>
public readonly record struct Damaged(uint SourceId, uint TargetId, int ActionId, int Amount, int HpAfter, bool Crit) : IGameEvent;

/// <summary>
/// The killer's view of a death (spec 8.1: Exp, Demand, Loot, Honor consume it). <see cref="VictimCharacterId"/> is 0 for
/// monsters; <see cref="VictimDefId"/> is the monster type (DemandService counts per type and region), 0 for characters.
/// </summary>
public readonly record struct Killed(uint KillerId, uint VictimId, long VictimCharacterId, int VictimDefId) : IGameEvent;

/// <summary>The victim's view of a death (spec 8.1: loot bag, respawn lock, arrest window). The actor is the victim.</summary>
public readonly record struct Died(uint EntityId, uint KillerId) : IGameEvent;

/// <summary>
/// A dead player came back (spec 3.1, user decision 2026-10-22: <c>playerRespawnDelayMs</c> after death, at the city spawn,
/// full meters). The actor is the character. Published in phase 8.
/// </summary>
public readonly record struct Respawned(uint EntityId) : IGameEvent;

/// <summary>Poise broke (R-3.3-01); <see cref="SourceId"/> landed the breaking hit.</summary>
public readonly record struct Staggered(uint EntityId, uint SourceId) : IGameEvent;

/// <summary>A parry met an incoming hit (R-3.3-03, R-3.3-04). The actor is the defender.</summary>
public readonly record struct ParryAttempted(uint DefenderId, uint AttackerId, int ActionId, ParryColor Color, ParryResult Result)
    : IGameEvent;

/// <summary>Parry outcomes (spec 3.3 ParrySystem). Stored in telemetry: append only, never renumber.</summary>
public enum ParryResult : byte
{
    /// <summary>Blue with medium or heavy armor, yellow with heavy armor, or green: the hit is blocked.</summary>
    Success = 1,
    /// <summary>Uncolored attack: damage and poise damage reduced by the parry value, no flinch.</summary>
    Reduced = 2,
    /// <summary>Red attacks cannot be parried: full hit.</summary>
    FailedRed = 3,
    /// <summary>Yellow without heavy armor, or blue with light armor (R-3.3-05): full hit.</summary>
    FailedArmor = 4,
    /// <summary>The hit landed in the parry's startup or recovery, outside its window: full hit.</summary>
    FailedTiming = 5,
    /// <summary>The parry press was refused for want of a charge (R-3.3-03), and the hit landed where it would have parried.</summary>
    NoCharge = 6,
}

/// <summary>
/// A hit flinched its target (R-3.3-07): a Startup or Channel action was cancelled, or an idle entity was caught.
/// <see cref="DurationMs"/> is the scaled flinch length (<c>flinchMs x flinchDurationPercent / 100</c>).
/// </summary>
public readonly record struct Flinched(uint EntityId, uint SourceId, int DurationMs) : IGameEvent;
