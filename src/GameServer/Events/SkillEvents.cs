namespace GameServer.Events;

// Skill-tree events (spec 3.5, 8.1; WP-M2-5), published by the world once the save of the operation committed (rule 5).
// The envelope's actor is the character. EntityId is its player entity (0 when it has none in this world).

/// <summary>A node was learned (level 1) or upgraded to <see cref="Level"/> (R-3.5-04). Stats rebuild the passives on it.</summary>
public readonly record struct SkillLearned(long CharacterId, uint EntityId, string NodeId, int Level, int PointsSpent) : IGameEvent;

/// <summary>An evolution replaced <see cref="FromNodeId"/> (its levels and points lost, R-3.5-04) with <see cref="NodeId"/> at level 1.</summary>
public readonly record struct SkillEvolved(long CharacterId, uint EntityId, string FromNodeId, int FromLevel, string NodeId,
    int PointsSpent) : IGameEvent;

/// <summary>Every node was forgotten and <see cref="PointsRefunded"/> points came back (R-3.5-04).</summary>
public readonly record struct Respecced(long CharacterId, uint EntityId, int NodesCleared, int PointsRefunded) : IGameEvent;

/// <summary>
/// The character advanced from <see cref="FromClassId"/> into <see cref="ToClassId"/> (R-3.5-01, permanent). Stats rebuild
/// the class bases and attributes; the world rebuilt the loadout and the usable weapons (spec 3.5 class advance flow).
/// </summary>
public readonly record struct ClassAdvanced(long CharacterId, uint EntityId, string FromClassId, string ToClassId, int Tier) : IGameEvent;
