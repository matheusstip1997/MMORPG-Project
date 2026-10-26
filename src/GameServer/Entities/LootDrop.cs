using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Entities;

/// <summary>
/// A monster drop lying on the ground (R-4.3-01, spec 4.3 <c>LootDrop</c>, backlog G): one item stack, spawned by
/// <c>LootService</c> where the monster died and seen through interest and <c>IVisibilityQuery</c> like any entity
/// (architecture rule 9). Its spawn event carries <c>ItemDef.netId</c>. Volatile: it lives only in world memory and is
/// gone after a restart (user decision 2026-10-24); only the items a player picked up are persisted.
/// <para>
/// <see cref="OwnerCharacterId"/> alone may take it until <see cref="ExclusiveUntilMs"/> (R-4.3-02 party lock, 30 s; with
/// no parties in M1 the party is the killer, user decision 2026-10-24); then anyone may. Nobody: it expires at
/// <see cref="ExpiresAtMs"/>. Times are world simulation ms (<c>GameWorld.NowMs</c>).
/// </para>
/// </summary>
public sealed class LootDrop : Entity
{
    public LootDrop(uint id, ItemDef item, byte rarity, int count, MovementState movement, int heightCm, string dropKey,
        long ownerCharacterId, long exclusiveUntilMs, long expiresAtMs)
        : base(id, EntityKind.LootDrop, movement, heightCm)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Item = item;
        Rarity = rarity;
        Count = count;
        DropKey = dropKey;
        OwnerCharacterId = ownerCharacterId;
        ExclusiveUntilMs = exclusiveUntilMs;
        ExpiresAtMs = expiresAtMs;
        DefId = (ushort)item.NetId;
    }

    public ItemDef Item { get; }

    public byte Rarity { get; }

    /// <summary>Units still on the ground; a pickup that does not take them all leaves the rest here.</summary>
    public int Count { get; internal set; }

    /// <summary>Unique for every drop ever made (world run, kill event, index); pickup operation keys start with it.</summary>
    public string DropKey { get; }

    /// <summary>Pickups so far: numbers each pickup's operation key.</summary>
    public int Pickups { get; internal set; }

    /// <summary>The character that may take it first (the killer); 0 when nobody (a monster killed by a monster).</summary>
    public long OwnerCharacterId { get; }

    public long ExclusiveUntilMs { get; }

    public long ExpiresAtMs { get; }

    /// <summary>R-4.3-02: locked to its owner for <paramref name="characterId"/> at <paramref name="nowMs"/>.</summary>
    public bool IsLockedFor(long characterId, long nowMs) =>
        OwnerCharacterId != 0 && characterId != OwnerCharacterId && nowMs < ExclusiveUntilMs;
}
