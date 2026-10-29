using Shared.Data.Tables;

namespace GameServer.Persistence;

/// <summary>What a <c>bystial_book_ops</c> row records (spec 14.2). Stored as smallint; append only.</summary>
public enum BystialOpKind : short
{
    /// <summary>A first card of a monster type (R-14.2-02).</summary>
    Earn = 1,
    /// <summary>A card reached a higher tier.</summary>
    Evolve = 2,
    /// <summary>A card went into an empty slot.</summary>
    Place = 3,
    /// <summary>A card went into an occupied slot; the other card went back to the collection.</summary>
    Swap = 4,
    /// <summary>A card went back to the collection.</summary>
    TakeOut = 5,
}

/// <summary>A character's card of one monster type as stored in <c>bystial_book</c>: absolute, not a delta. Slot null: in the collection.</summary>
public readonly record struct BystialCardRow(string MonsterDefId, BystialTier Tier, int? Slot);

/// <summary>One book operation to write with the save: its idempotency key and audit fields.</summary>
public sealed record BystialOpRecord(string OpKey, BystialOpKind Kind, string MonsterDefId, string? OtherMonsterDefId,
    BystialTier FromTier, BystialTier ToTier, int? Slot, long? KillCount);

/// <summary>
/// A character's book changes since its last save (save class 2, R-7.1-01; WP-M2-8): the cards that changed (absolute rows)
/// and the operations with their audit rows. Written in the same transaction as the character row, so a retried save
/// writes the same thing and a crash loses all of it or nothing.
/// </summary>
public sealed record BystialBatch(IReadOnlyList<BystialCardRow> Cards, IReadOnlyList<BystialOpRecord> Ops)
{
    public bool IsEmpty => Cards.Count == 0 && Ops.Count == 0;
}

/// <summary>The book a login reads: every card of the character.</summary>
public sealed record BystialRecord(IReadOnlyList<BystialCardRow> Cards)
{
    public static readonly BystialRecord Empty = new(Array.Empty<BystialCardRow>());
}

/// <summary>
/// Hands a character's pending book batch to the save that is being issued, and learns how that save ended. Implemented
/// by <c>BystialService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IBystialBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    BystialBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
