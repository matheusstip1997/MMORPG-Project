namespace GameServer.Persistence;

/// <summary>What holds an escrow (<c>escrow.holder_kind</c>). Stored in the database: append only, never renumber.</summary>
public enum EscrowHolderKind : short
{
    /// <summary>Tests and dev tools.</summary>
    Test = 1,
    /// <summary>An auction listing (WP-M4-7).</summary>
    Listing = 2,
    /// <summary>A shop stall listing (WP-M4-8).</summary>
    Stall = 3,
    /// <summary>A contract (WP-M4-10).</summary>
    Contract = 4,
}

/// <summary>An escrow's state (<c>escrow.state</c>). Stored in the database: append only.</summary>
public enum EscrowState : short
{
    Held = 1,
    Released = 2,
}

/// <summary>Where a release went (<c>escrow.released_to_kind</c>). Stored in the database: append only.</summary>
public enum EscrowTargetKind : short
{
    Character = 1,
    Mail = 2,
}

/// <summary>
/// One unit line an escrow holds (<c>escrow_items</c>): a stack (<see cref="InstanceId"/> 0) or one instance with its
/// properties as stored in <c>item_instances</c>.
/// </summary>
public readonly record struct EscrowItemRow(string DefId, int Rarity, bool Stolen, int Count, long InstanceId, bool NonTradable,
    string? PropertiesJson);

/// <summary>A new escrow row, written in the owner's character save (the hold).</summary>
public sealed record EscrowHoldRow(string EscrowId, long OwnerCharacterId, EscrowHolderKind HolderKind, string HolderId, long Gold,
    long CreatedAtUtcMs, IReadOnlyList<EscrowItemRow> Items);

/// <summary>An escrow released to a character, written in that character's save (guarded: the row must still be held).</summary>
public sealed record EscrowReleaseRow(string EscrowId, string ReleaseOpKey, EscrowTargetKind ToKind, long ToId, long AtUtcMs);

/// <summary>
/// The escrow part of a character save (WP-M4-4): holds made and releases received by this character since its last
/// save, written in the same transaction as the items that left or entered its bag.
/// </summary>
public sealed record EscrowBatch(IReadOnlyList<EscrowHoldRow> Holds, IReadOnlyList<EscrowReleaseRow> Releases)
{
    public bool IsEmpty => Holds.Count == 0 && Releases.Count == 0;
}

/// <summary>An escrow as loaded at world start (still held).</summary>
public sealed record EscrowRecord(string EscrowId, long OwnerCharacterId, EscrowHolderKind HolderKind, string HolderId, long Gold,
    IReadOnlyList<EscrowItemRow> Items);

/// <summary>
/// Hands a character's pending escrow rows to the save being issued, and learns how that save ended. Implemented by
/// <c>EscrowService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IEscrowBatchSource
{
    EscrowBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}
