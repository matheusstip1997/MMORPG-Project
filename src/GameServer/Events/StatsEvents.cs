namespace GameServer.Events;

/// <summary>
/// A character's final stats changed (spec 3.4, 8.1; WP-M2-2): a status, its gear or its level changed a value, or the
/// character entered the world. Published by <c>StatsService</c> in tick phase 10, at most once per character per tick.
/// <see cref="Version"/> is its <c>StatBlock.Version</c>; consumers (the own-stats message, later skill arts and the
/// Bystial book) read the values from the stat block. The actor is the character.
/// </summary>
public readonly record struct StatsChanged(long CharacterId, uint EntityId, int Version) : IGameEvent;
