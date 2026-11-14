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
/// Until <see cref="ExclusiveUntilMs"/> it is locked (R-4.3-02, <c>PartyLootParams.lockMs</c>): to the members of
/// <see cref="OwnerPartyId"/> when the killer was in a party (WP-M4-6; to the killer alone once that party dissolved), else to
/// <see cref="OwnerCharacterId"/>; then anyone may take it, and a pickup by a member of its party still rolls (user answer
/// 2026-11-13). <c>LootService</c> decides who the lock lets in. Nobody: it expires at <see cref="ExpiresAtMs"/>. Times are
/// world simulation ms (<c>GameWorld.NowMs</c>).
/// </para>
/// </summary>
public sealed class LootDrop : Entity
{
    public LootDrop(uint id, ItemDef item, byte rarity, int count, MovementState movement, int heightCm, string dropKey,
        long ownerCharacterId, long exclusiveUntilMs, long expiresAtMs, long ownerPartyId = 0, IReadOnlyList<long>? eligibleAtKill = null)
        : base(id, EntityKind.LootDrop, movement, heightCm)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        Item = item;
        Rarity = rarity;
        Count = count;
        DropKey = dropKey;
        OwnerCharacterId = ownerCharacterId;
        OwnerPartyId = ownerPartyId;
        EligibleAtKill = eligibleAtKill ?? Array.Empty<long>();
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

    /// <summary>The killer, who may take it first when it was in no party; 0 when nobody (a monster killed by a monster).</summary>
    public long OwnerCharacterId { get; }

    /// <summary>The killer's party at the kill (WP-M4-6); 0 when the killer was in none.</summary>
    public long OwnerPartyId { get; }

    /// <summary>
    /// The members of <see cref="OwnerPartyId"/> eligible for its rolls, fixed at the kill in join order: a hit on the monster or
    /// within <c>PartyLootParams.killRangeCm</c> of the kill, the killer always (M4 decision 2). Shared by the drops of one kill;
    /// empty without a party.
    /// </summary>
    public IReadOnlyList<long> EligibleAtKill { get; }

    public long ExclusiveUntilMs { get; }

    public long ExpiresAtMs { get; }

    /// <summary>R-4.3-02: inside the lock window at <paramref name="nowMs"/> (whom it lets in is <c>LootService</c>'s call).</summary>
    public bool IsLocked(long nowMs) => OwnerCharacterId != 0 && nowMs < ExclusiveUntilMs;
}
