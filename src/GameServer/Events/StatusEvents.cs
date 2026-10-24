namespace GameServer.Events;

// Status and heal events (spec 3.7, 8.1), emitted by the StatusSystem in tick phase 6 (and phase 7 for a monster's leash
// reset) in entity-ID order. Status IDs are StatusEffectDef net IDs; action IDs refer to ActionDef rows (0 for a periodic
// tick). The envelope's ActorCharacterId is the character that applied, removed or healed (0 for monsters and expiry).

/// <summary>
/// A status was applied (spec 3.7) or, when <see cref="Refreshed"/>, applied again to a target that had it: the duration
/// starts over and <see cref="Stacks"/> grew below the cap (user decision 2026-10-23). It lasts until <see cref="EndTick"/>.
/// </summary>
public readonly record struct EffectApplied(uint TargetId, uint SourceId, int StatusId, int Stacks, long EndTick, bool Refreshed)
    : IGameEvent;

/// <summary>Why a status ended without a cleanse. Stored in telemetry: append only, never renumber.</summary>
public enum StatusEndReason : byte
{
    /// <summary>It ran out (its end tick came).</summary>
    Expired = 1,
    /// <summary>Its target died (user decision 2026-10-23).</summary>
    Died = 2,
    /// <summary>A monster leashed home and reset (user decision 2026-10-23).</summary>
    Reset = 3,
}

/// <summary>A status ended (spec 3.7): it ran out, or its target died or reset.</summary>
public readonly record struct EffectRemoved(uint TargetId, int StatusId, StatusEndReason Reason) : IGameEvent;

/// <summary>A RemoveStatus effect took a status off (R-3.7-03: a cleanse of debuffs or a strip of buffs).</summary>
public readonly record struct EffectCleansed(uint TargetId, uint SourceId, int StatusId, int ActionId) : IGameEvent;

/// <summary>
/// A target gained HP from a Heal effect (<see cref="ActionId"/>) or a periodic heal (<see cref="StatusId"/>, action 0).
/// <see cref="Amount"/> is the HP gained (capped at the max, so a full target gains 0 and no event is sent).
/// </summary>
public readonly record struct Healed(uint SourceId, uint TargetId, int ActionId, int StatusId, int Amount, int HpAfter) : IGameEvent;
