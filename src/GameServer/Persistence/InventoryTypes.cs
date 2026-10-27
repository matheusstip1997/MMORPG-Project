namespace GameServer.Persistence;

/// <summary>One occupied slot as stored in <c>item_stacks</c>.</summary>
public sealed record ItemStackRow(int Container, int Slot, string DefId, int Rarity, bool Stolen, int Count);

/// <summary>
/// One item instance as stored in <c>item_instances</c> (WP-M2-1, spec 4.1 M2 item model). <see cref="PropertiesJson"/> is the
/// versioned properties field as stored (<c>{"v":1,...}</c>), carried through untouched.
/// </summary>
public sealed record ItemInstanceRow(long InstanceId, int Container, int Slot, string DefId, int Rarity, bool Stolen,
    bool NonTradable, string PropertiesJson);

/// <summary>
/// Everything a login reads of a character's items: the stacks, the instances, the instance counter
/// (<c>characters.item_instance_seq</c>), and whether the class's starter weapon was ever granted (its operation key is in
/// <c>inventory_ops</c>).
/// </summary>
public sealed record InventoryLoad(IReadOnlyList<ItemStackRow> Stacks, IReadOnlyList<ItemInstanceRow> Instances, long InstanceSeq,
    bool StarterGranted)
{
    public static readonly InventoryLoad Empty = new(Array.Empty<ItemStackRow>(), Array.Empty<ItemInstanceRow>(), 0, true);
}

/// <summary>
/// An instance as an inventory batch leaves it: where it lies now, or <see cref="Removed"/> (destroyed or gone from the
/// character). Like <see cref="SlotState"/>, a final state, not a delta.
/// </summary>
public readonly record struct InstanceState(long InstanceId, int Container, int Slot, string DefId, int Rarity, bool Stolen,
    bool NonTradable, string PropertiesJson, bool Removed);

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
    /// <summary>Into the Equipped container (WP-M2-1).</summary>
    Equip = 3,
    /// <summary>Out of the Equipped container (WP-M2-1).</summary>
    Unequip = 4,
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
    int CountAfter,
    long InstanceId = 0);

/// <summary>
/// A character's inventory changes since its last save (save class 2, R-7.1-01): the operations with their keys, their
/// audit rows, and the final state of every slot they touched. Written in the same transaction as the character row.
/// With instances (WP-M2-1): the final state of every touched instance, and the character's instance counter when it
/// created any (<see cref="InstanceSeq"/>, -1 when unchanged).
/// </summary>
public sealed record InventoryBatch(
    IReadOnlyList<InventoryOpRecord> Ops,
    IReadOnlyList<AuditEntry> Audit,
    IReadOnlyList<SlotState> Slots,
    IReadOnlyList<InstanceState>? Instances = null,
    long InstanceSeq = -1)
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

/// <summary>Operation keys the persistence layer must know (WP-M2-1).</summary>
public static class InventoryOpKeys
{
    /// <summary>
    /// The grant of a character's starter weapon (spec 4.1 M2 item model): one key per character, so it is applied once
    /// ever; a login reads whether it exists.
    /// </summary>
    public static string Starter(long characterId) => $"starter:{characterId}";
}
