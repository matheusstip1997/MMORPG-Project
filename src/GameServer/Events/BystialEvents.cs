using Shared.Data.Tables;

namespace GameServer.Events;

// Bystial book events (spec 14.2, 8.1; WP-M2-8), published by BystialService once the save that carries the change
// committed (rule 5). The envelope's actor is the character. EntityId is its player entity (0 when it has none in this
// world). MonsterDefId is the monster's MonsterDef.netId.

/// <summary>A character's first card of a monster type (R-14.2-02), at <see cref="Kills"/> kills of that type.</summary>
public readonly record struct CardEarned(long CharacterId, uint EntityId, int MonsterDefId, BystialTier Tier, long Kills) : IGameEvent;

/// <summary>A card went from <see cref="From"/> to <see cref="To"/> (several tiers in one step are one event).</summary>
public readonly record struct CardEvolved(long CharacterId, uint EntityId, int MonsterDefId, BystialTier From, BystialTier To, long Kills)
    : IGameEvent;

/// <summary>
/// What the book gives changed: a card placed, swapped or taken out, or a placed card evolved. Stats rebuild the character's
/// <c>bystialCard</c> source on it (idempotent: the whole source again). <see cref="Placed"/> is the number of placed cards.
/// </summary>
public readonly record struct BookChanged(long CharacterId, uint EntityId, int Version, int Placed) : IGameEvent;
