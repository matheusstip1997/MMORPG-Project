namespace GameServer.Persistence;

/// <summary>One occupied slot as stored in <c>item_stacks</c>.</summary>
public sealed record ItemStackRow(int Container, int Slot, string DefId, int Rarity, bool Stolen, int Count);

/// <summary>
/// A slot as an inventory batch leaves it: <see cref="DefId"/> null (and <see cref="Count"/> 0) is an empty slot.
/// The batch writes final slot states, not deltas, so writing it is the same whatever the database held before.
/// </summary>
public readonly record struct SlotState(int Container, int Slot, string? DefId, int Rarity, bool Stolen, int Count)
{
    public bool IsEmpty => DefId == null;
}

/// <summary>An applied inventory operation: its idempotency key (rule 5) and why it happened.</summary>
public readonly record struct InventoryOpRecord(string OpKey, string Reason, string Source);

/// <summary>Kind of an <c>audit_log</c> row. Stored in the database: append only, never renumber.</summary>
public enum AuditKind : short
{
    Add = 0,
    Remove = 1,
    Move = 2,
}

/// <summary>One <c>audit_log</c> row: one slot one operation changed.</summary>
public readonly record struct AuditEntry(
    string OpKey,
    int Seq,
    AuditKind Kind,
    int Container,
    int Slot,
    string DefId,
    int Rarity,
    bool Stolen,
    int CountDelta,
    int CountAfter);

/// <summary>
/// A character's inventory changes since its last save (save class 2, R-7.1-01): the operations with their keys, their
/// audit rows, and the final state of every slot they touched. Written in the same transaction as the character row.
/// </summary>
public sealed record InventoryBatch(
    IReadOnlyList<InventoryOpRecord> Ops,
    IReadOnlyList<AuditEntry> Audit,
    IReadOnlyList<SlotState> Slots)
{
    public bool IsEmpty => Ops.Count == 0;
}

/// <summary>
/// Hands a character's pending inventory batch to the save that is being issued, and learns how that save ended.
/// Implemented by <c>InventoryService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IInventoryBatchSource
{
    /// <summary>The changes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    InventoryBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its batch is in the database.</summary>
    void Committed(long characterId, Guid saveId);
}
