using Shared.Simulation.Stats;

namespace GameServer.Events;

// Progression events (spec 3.6, 8.1; WP-M2-3), published by ExpService and RankService. The envelope's actor is the
// character. EntityId is its player entity (0 when it has none in this world).

/// <summary>
/// A character gained exp from a kill (R-3.6-03): <see cref="Amount"/> after the modifiers (0 is possible: no exp floor),
/// <see cref="VictimDefId"/> the monster's <c>MonsterDef.netId</c>, the rank modifier in percent and the band modifier in
/// parts per million. Telemetry only.
/// </summary>
public readonly record struct ExpGained(long CharacterId, uint EntityId, long Amount, int VictimDefId, int RankPercent, long BandPpm)
    : IGameEvent;

/// <summary>
/// A character went from <see cref="FromLevel"/> to <see cref="ToLevel"/> (several levels in one gain are one event) and
/// gained <c>skillPointsPerLevel</c> points per level (R-3.5-04). Stats recompute their level part on it (WP-M2-2).
/// </summary>
public readonly record struct LevelUp(long CharacterId, uint EntityId, int FromLevel, int ToLevel) : IGameEvent;

/// <summary>A rank-up at the outpost committed (R-3.6-04); published only after its save (rule 5).</summary>
public readonly record struct RankChanged(long CharacterId, uint EntityId, Rank From, Rank To) : IGameEvent;

/// <summary>
/// A character's committed free attribute points changed (R-3.4-08, WP-M2-13; published only after the save, rule 5): one
/// point placed on <see cref="Attribute"/> (<c>Attributes.Str</c> ... <c>Int</c>), or, with <see cref="Attribute"/> -1, every
/// placed point given back by a skill respec (<see cref="Points"/> of them). <see cref="Unspent"/> is the committed unspent
/// count after it. <c>StatsService</c> rebuilds the <c>Attribute</c> stat source on it (idempotent).
/// </summary>
public readonly record struct AttributesChanged(long CharacterId, uint EntityId, int Attribute, int Points, int Unspent) : IGameEvent;
