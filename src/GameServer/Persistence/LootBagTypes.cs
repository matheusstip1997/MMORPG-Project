namespace GameServer.Persistence;

/// <summary>A loot bag's state as stored (<c>loot_bags.state</c>, realm V0021): append only, never renumber.</summary>
public enum LootBagState : short
{
    Open = 1,
    /// <summary>The last row was taken.</summary>
    Emptied = 2,
    /// <summary>The expiry job destroyed what was left (a sink, R-4.3-03).</summary>
    Expired = 3,
}

/// <summary>Who a take was for (<c>loot_bag_ops.outcome</c>; M4 decision 8): append only, never renumber.</summary>
public enum LootBagTakeOutcome : short
{
    None = 0,
    /// <summary>The owner took their own items back (into the Normal bag, what does not fit by mail).</summary>
    Owner = 1,
    /// <summary>A party member collected for the owner inside the owner window: everything by mail to the owner, no mark.</summary>
    ForOwner = 2,
    /// <summary>A non-owner took after the window: the units carry the stolen mark (R-4.4-01).</summary>
    Stolen = 3,
}

/// <summary>Kinds of <c>loot_bag_ops</c> rows: append only, never renumber.</summary>
public enum LootBagOpKind : short
{
    Drop = 1,
    Take = 2,
    Expire = 3,
}

/// <summary>
/// One stack a bag holds (<c>loot_bag_items</c>): its place in the bag (<paramref name="Index"/>, the Normal bag's slot order at
/// the death), the units still in it, and its stolen flag and mark as it had them in the bag it fell from.
/// </summary>
public sealed record LootBagItemRow(int Index, string DefId, int Rarity, int Count, bool Stolen, long StolenFrom = 0, long StolenAtMs = 0);

/// <summary>An open loot bag as stored (<c>loot_bags</c> and its rows), times in UTC milliseconds (rule 6).</summary>
public sealed record LootBagRecord(string BagId, long OwnerCharacterId, string RegionId, int XCm, int YCm, int HeightCm,
    long CreatedUtcMs, long OwnerWindowUntilUtcMs, long ExpiresUtcMs, IReadOnlyList<LootBagItemRow> Items);

/// <summary>One row a take moved: the units taken out of <see cref="CountSeen"/> (the guard: the database must hold that many).</summary>
public readonly record struct LootBagTakenRow(int Index, string DefId, int Rarity, int CountSeen, int CountTaken);

/// <summary>
/// A take, written in the taker's save: its operation key (unique for ever) and the rows and units it moved (into the taker's bag
/// or by mail). The items it gave the taker and the mails to the owner are in the same save's inventory and mail batches. The
/// take that leaves no row closes the bag (state emptied), whichever take commits last.
/// </summary>
public sealed record LootBagTakeWrite(string OpKey, string BagId, long CharacterId, LootBagTakeOutcome Outcome,
    IReadOnlyList<LootBagTakenRow> Rows, long AtUtcMs);

/// <summary>
/// A character save's loot bag part (WP-M4-9): the bags its death created (the death transaction: one transaction with the
/// inventory batch that removed the units, so both or neither) and the takes it made, written by
/// <see cref="LootBagRepository.WriteAsync"/> after the inventory and mail batches.
/// </summary>
public sealed record LootBagBatch(IReadOnlyList<LootBagRecord> Creates, IReadOnlyList<LootBagTakeWrite> Takes)
{
    public bool IsEmpty => Creates.Count == 0 && Takes.Count == 0;
}

/// <summary>Hands a character's pending loot bag writes to a save and learns how it ended (world thread; the death loot service).</summary>
public interface ILootBagBatchSource
{
    LootBagBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}

/// <summary>A take's guarded update found the bag or a row changed (taken or expired elsewhere): the save rolls back.</summary>
public sealed class LootBagConflictException(string bagId) : Exception($"loot bag {bagId} changed under a take")
{
    public string BagId { get; } = bagId;
}

/// <summary>How an expiry ended (<see cref="LootBagRepository.ExpireAsync"/>).</summary>
public enum LootBagExpireStatus : byte
{
    /// <summary>This call expired it: <see cref="LootBagExpireResult.Items"/> were destroyed.</summary>
    Expired,
    /// <summary>It was expired before (a retry after an unknown outcome): the destroyed items are read back from its op row.</summary>
    AlreadyExpired,
    /// <summary>It was emptied by takes, or never existed: nothing to destroy.</summary>
    Gone,
}

/// <summary>An expiry's outcome: the units it destroyed (the bag's rows as they were).</summary>
public sealed record LootBagExpireResult(LootBagExpireStatus Status, IReadOnlyList<LootBagItemRow> Items);

/// <summary>The expiry job's store (<see cref="LootBagRepository"/>; a test double in unit tests). Called off the world thread.</summary>
public interface ILootBagExpirer
{
    Task<LootBagExpireResult> ExpireAsync(string bagId, long atUtcMs, CancellationToken ct = default);
}
