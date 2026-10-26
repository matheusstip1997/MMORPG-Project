using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>The outcome of an add: how many units went in. A repeated operation key returns the first result.</summary>
public readonly record struct AddResult(int Added, bool Repeated);

/// <summary>
/// InventoryService v1 (backlog G, spec 4.1): <b>the only writer of items</b> (architecture rule 4). Normal bag only
/// (R-4.1-02); add, remove, move and stack. Runs on the world thread with an in-memory copy of every online character's
/// bag (spec 4.1: "online characters' inventories are cached in memory").
/// <para>
/// Each operation carries an idempotency key and produces audit rows (rule 5). It changes the cached bag at once (so
/// the next check sees it), marks the character class 2 dirty, and waits for the next character save, which writes the
/// operation keys, the touched slots and the audit rows in its own transaction (R-7.1-01: loot pickup and moves between
/// own bags are class 2, saved in a batch every second). Only when that save commits does the player hear about it
/// (<see cref="ContentsCommitted"/>) and the item events go out (spec 4.1 flow: commit, update, emit, confirm). A crash
/// loses at most the last batch and never duplicates (R-7.1-02).
/// </para>
/// TODO(M2): Transfer, Reserve/Release, Escrow and the class 1 path (ordering barrier first, R-7.1-02); gold; the Safe
/// and Quest bags; equipment instances.
/// </summary>
public sealed class InventoryService : IInventoryBatchSource
{
    private readonly ItemRules _rules;
    private readonly EventBus? _events;
    private readonly Dictionary<long, CharacterInventory> _characters = new();
    private readonly List<SlotChange> _changes = new();

    public InventoryService(ItemRules rules, EventBus? events)
    {
        _rules = rules;
        _events = events;
    }

    public ItemRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>A save committed changes of this character: tell the player (set by the world).</summary>
    public Action<long>? ContentsCommitted { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>
    /// Starts holding a character's bag as loaded at login. False (nothing attached) when a row names an item or a slot
    /// the tables do not know: the character cannot be represented, so it is refused like an unknown class.
    /// </summary>
    public bool Attach(long characterId, IReadOnlyList<ItemStackRow> rows)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var bag = new Bag(_rules.NormalBag.Slots);
        foreach (var row in rows)
        {
            if (row.Container != _rules.NormalBag.Code || row.Slot < 0 || row.Slot >= bag.SlotCount
                || !_rules.TryGetItem(row.DefId, out var item) || row.Count <= 0 || row.Count > item.MaxStack
                || row.Rarity is < 0 or > byte.MaxValue || !bag[row.Slot].IsEmpty)
                return false;
            bag.Load(row.Slot, new ItemStack((ushort)item.NetId, (byte)row.Rarity, row.Stolen, row.Count));
        }
        var inventory = new CharacterInventory(characterId, bag);
        bag.CopyTo(inventory.Committed);
        _characters.Add(characterId, inventory);
        return true;
    }

    /// <summary>Drops a character's cached bag (it left the world). Unsaved changes of a faulted character are lost with it.</summary>
    public void Detach(long characterId) => _characters.Remove(characterId);

    /// <summary>The bag as the world sees it now (including changes not yet saved).</summary>
    public Bag Current(long characterId) => Get(characterId).Bag;

    /// <summary>The bag as last committed to the database: what the player is shown (rule 5).</summary>
    public IReadOnlyList<ItemStack> Committed(long characterId) => Get(characterId).Committed;

    /// <summary>Changes made and not yet in a committed save.</summary>
    public bool HasUnsaved(long characterId)
    {
        var inventory = Get(characterId);
        return inventory.PendingOps.Count > 0 || inventory.InFlight != null;
    }

    /// <summary>Units of <paramref name="item"/> that fit in the character's bag now (take what fits, R-4.3-02 M1).</summary>
    public int SpaceFor(long characterId, ItemDef item, byte rarity) =>
        Accepts(item) ? Get(characterId).Bag.SpaceFor(new ItemStack((ushort)item.NetId, rarity, false, 1), item.MaxStack) : 0;

    public bool Accepts(ItemDef item) => _rules.NormalBag.Accept(item.Category);

    /// <summary>
    /// Adds up to <paramref name="count"/> units (as many as fit) under <paramref name="opKey"/>. Nothing is added when
    /// the bag does not take the category or is full (<see cref="AddResult.Added"/> 0, and no operation is recorded).
    /// </summary>
    public AddResult TryAdd(long characterId, ItemDef item, byte rarity, int count, string opKey, InventoryReason reason, string source)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return new AddResult(previous, true);
        if (!Accepts(item))
            return new AddResult(0, false);
        _changes.Clear();
        int added = inventory.Bag.Add(new ItemStack((ushort)item.NetId, rarity, false, count), item.MaxStack, _changes);
        if (added > 0)
            Record(inventory, opKey, AuditKind.Add, reason, source, added);
        return new AddResult(added, false);
    }

    /// <summary>Removes <paramref name="count"/> units from one slot (destroyed). False, and nothing recorded, if it holds fewer.</summary>
    public bool TryRemove(long characterId, int slot, int count, string opKey, InventoryReason reason, string source)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return previous > 0;
        _changes.Clear();
        if (!inventory.Bag.TryRemove(slot, count, _changes))
            return false;
        Record(inventory, opKey, AuditKind.Remove, reason, source, count);
        return true;
    }

    /// <summary>Moves a stack onto another slot of the bag: into an empty slot, onto the same key (merge), or a swap.</summary>
    public BagMoveResult TryMove(long characterId, int fromSlot, int toSlot, string opKey, InventoryReason reason)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return (BagMoveResult)previous;
        _changes.Clear();
        var bag = inventory.Bag;
        int maxStack = fromSlot >= 0 && fromSlot < bag.SlotCount && !bag[fromSlot].IsEmpty
                       && _rules.TryGetItem(bag[fromSlot].ItemNetId, out var item) ? item.MaxStack : 1;
        var result = bag.TryMove(fromSlot, toSlot, maxStack, _changes);
        if (result != BagMoveResult.Refused)
            Record(inventory, opKey, AuditKind.Move, reason, $"slot:{fromSlot}->{toSlot}", (int)result);
        return result;
    }

    public InventoryBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || inventory.PendingOps.Count == 0)
            return null;
        if (inventory.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a batch in flight");

        var slots = new List<SlotState>(inventory.PendingSlots.Count);
        foreach (var slot in inventory.PendingSlots) // sorted set: slots in order (rule 6)
            slots.Add(StateOf(inventory.Bag[slot], slot));
        var batch = new InventoryBatch(inventory.PendingOps.ToList(), inventory.PendingAudit.ToList(), slots);
        var snapshot = new ItemStack[inventory.Bag.SlotCount];
        inventory.Bag.CopyTo(snapshot);
        inventory.InFlight = new InFlightBatch(saveId, snapshot, batch);
        inventory.PendingOps.Clear();
        inventory.PendingAudit.Clear();
        inventory.PendingSlots.Clear();
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || inventory.InFlight is not { } inFlight
            || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        inventory.InFlight = null;
        Array.Copy(inFlight.Snapshot, inventory.Committed, inventory.Committed.Length);
        PublishCommitted(characterId, inFlight.Batch);
        ContentsCommitted?.Invoke(characterId);
    }

    private void Record(CharacterInventory inventory, string opKey, AuditKind kind, InventoryReason reason, string source, int result)
    {
        inventory.SeenOps.Add(opKey, result);
        inventory.PendingOps.Add(new InventoryOpRecord(opKey, reason.ToString(), source));
        int seq = 0;
        foreach (var change in _changes)
        {
            inventory.PendingSlots.Add(change.Slot);
            var before = change.Before;
            var after = change.After;
            if (!before.IsEmpty && !after.IsEmpty && before.SameKey(after))
            {
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, change.Slot, after, after.Count - before.Count, after.Count));
                continue;
            }
            // A slot that changed item (filled, emptied or swapped): what left and what came, each with its own row.
            if (!before.IsEmpty)
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, change.Slot, before, -before.Count, 0));
            if (!after.IsEmpty)
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, change.Slot, after, after.Count, after.Count));
        }
        MarkClass2?.Invoke(inventory.CharacterId);
    }

    private AuditEntry Audit(string opKey, int seq, AuditKind kind, int slot, in ItemStack stack, int delta, int after) =>
        new(opKey, seq, kind, _rules.NormalBag.Code, slot, DefIdOf(stack.ItemNetId), stack.Rarity, stack.Stolen, delta, after);

    private SlotState StateOf(in ItemStack stack, int slot) => stack.IsEmpty
        ? new SlotState(_rules.NormalBag.Code, slot, null, 0, false, 0)
        : new SlotState(_rules.NormalBag.Code, slot, DefIdOf(stack.ItemNetId), stack.Rarity, stack.Stolen, stack.Count);

    private string DefIdOf(ushort netId) =>
        _rules.TryGetItem(netId, out var item) ? item.Id : throw new InvalidOperationException($"no ItemDef with netId {netId}");

    private void PublishCommitted(long characterId, InventoryBatch batch)
    {
        if (_events == null)
            return;
        // One event per add or remove operation; moves create and destroy nothing.
        foreach (var op in batch.Ops)
        {
            var reason = Enum.TryParse<InventoryReason>(op.Reason, out var parsed) ? parsed : InventoryReason.None;
            int net = 0;
            AuditEntry first = default;
            bool any = false;
            foreach (var audit in batch.Audit)
            {
                if (audit.OpKey != op.OpKey || audit.Kind == AuditKind.Move)
                    continue;
                net += audit.CountDelta;
                if (!any)
                    first = audit;
                any = true;
            }
            if (!any || !_rules.TryGetItem(first.DefId, out var item))
                continue;
            if (first.Kind == AuditKind.Add)
                _events.Publish(new ItemCreated(characterId, (ushort)item.NetId, (byte)first.Rarity, net, reason), characterId);
            else
                _events.Publish(new ItemDestroyed(characterId, (ushort)item.NetId, (byte)first.Rarity, -net, reason), characterId);
        }
    }

    private CharacterInventory Get(long characterId) =>
        _characters.TryGetValue(characterId, out var inventory)
            ? inventory
            : throw new KeyNotFoundException($"character {characterId} has no inventory attached");

    private sealed record InFlightBatch(Guid SaveId, ItemStack[] Snapshot, InventoryBatch Batch);

    private sealed class CharacterInventory(long characterId, Bag bag)
    {
        public long CharacterId { get; } = characterId;
        public Bag Bag { get; } = bag;
        public ItemStack[] Committed { get; } = new ItemStack[bag.SlotCount];

        /// <summary>
        /// Keys of every operation applied since the character was attached, with their result (rule 5: a repeated key
        /// returns the first result). Across logins the database key is the backstop.
        /// </summary>
        public Dictionary<string, int> SeenOps { get; } = new(StringComparer.Ordinal);

        public List<InventoryOpRecord> PendingOps { get; } = new();
        public List<AuditEntry> PendingAudit { get; } = new();
        public SortedSet<int> PendingSlots { get; } = new();
        public InFlightBatch? InFlight { get; set; }
    }
}
