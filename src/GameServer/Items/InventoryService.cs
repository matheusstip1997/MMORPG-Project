using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>
/// The outcome of an add: how many units went in, and for an instance category the id of the first instance made (the
/// forge's output, WP-M3-6; 0 otherwise). A repeated operation key returns the first count and no instance id.
/// </summary>
public readonly record struct AddResult(int Added, bool Repeated, long FirstInstanceId = 0);

/// <summary>
/// The outcome of a move between containers, an equip or an unequip (WP-M2-1): what happened, or the reason code of a
/// refusal (nothing changed and nothing was recorded). A repeated operation key returns the first result.
/// </summary>
public readonly record struct InventoryOpResult(BagMoveResult Result, InventoryMoveReason Reason, bool Repeated)
{
    public bool Applied => Result != BagMoveResult.Refused;
}

/// <summary>
/// InventoryService (backlog G, spec 4.1): <b>the only writer of items</b> (architecture rule 4). Runs on the world thread
/// with an in-memory copy of every online character's containers (spec 4.1: "online characters' inventories are cached in
/// memory"): the Normal bag, the Safe bag areas, the Quest bag and Equipped (R-4.1-02, spec 4.1 M2 item model), as the shared
/// <see cref="Inventory"/>. Stackables are stacks; equipment, molds and tools are instances with their own ids
/// (<see cref="ItemInstanceIds"/>). Add, remove, move, stack, equip and unequip (this file and
/// <c>InventoryService.Equipment.cs</c>).
/// <para>
/// Each operation carries an idempotency key and produces audit rows (rule 5). It changes the cached containers at once (so
/// the next check sees it), marks the character class 2 dirty, and waits for the next character save, which writes the
/// operation keys, the touched slots and instances and the audit rows in its own transaction (R-7.1-01: loot pickup and
/// moves between own bags are class 2, saved in a batch every second). Only when that save commits does the player hear
/// about it (<see cref="ContentsCommitted"/>) and the item and equipment events go out (spec 4.1 flow: commit, update, emit,
/// confirm). A crash loses at most the last batch and never duplicates (R-7.1-02).
/// </para>
/// The single-bag methods of M1 (<c>TryAdd</c> by category, <c>TryRemove(slot)</c>, <c>TryMove(fromSlot, toSlot)</c>,
/// <see cref="Current"/>, <see cref="Committed(long)"/>) keep their behavior on the Normal bag.
/// Gold and the class 1 path (WP-M2-4) are in <c>InventoryService.Gold.cs</c>; reservations (WP-M3-2) in
/// <c>InventoryService.Reservations.cs</c>. Instance properties are typed (<see cref="ItemProperties"/>), parsed at login and
/// written in the batch. TODO(M4): Transfer, Escrow.
/// </summary>
public sealed partial class InventoryService : IInventoryBatchSource
{
    private readonly ItemRules _rules;
    private readonly IItemRuleLookup _lookup;
    private readonly EventBus? _events;
    private readonly Dictionary<long, CharacterInventory> _characters = new();
    private readonly List<SlotChange> _changes = new();
    private readonly List<InventorySlotChange> _inventoryChanges = new();
    private readonly int _normalIndex;
    private readonly int _equippedIndex;

    public InventoryService(ItemRules rules, EventBus? events)
    {
        _rules = rules;
        _lookup = rules;
        _events = events;
        _normalIndex = IndexOf(rules.NormalBag.Code);
        _equippedIndex = rules.Equipped == null ? -1 : IndexOf(rules.Equipped.Code);
    }

    public ItemRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>A save committed changes of this character: tell the player (set by the world).</summary>
    public Action<long>? ContentsCommitted { get; set; }

    /// <summary>
    /// A save committed this operation (character, operation key, reason), once per operation, in batch order, before
    /// <see cref="ContentsCommitted"/>: the owner of the operation announces it (harvesting publishes <c>Harvested</c>, WP-M3-5),
    /// whether or not the player is still connected. Set by the world.
    /// </summary>
    public Action<long, string, InventoryReason>? OpCommitted { get; set; }

    /// <summary>The entity id of a character in the world, 0 when it is gone (set by the world; the <c>ItemUsed</c> event).</summary>
    public Func<long, uint>? EntityOf { get; set; }

    public int Count => _characters.Count;

    public bool IsAttached(long characterId) => _characters.ContainsKey(characterId);

    /// <summary>Attach with stacks only (M1 callers and tests): no instances, no class (no weapon can be equipped), level 1.</summary>
    public bool Attach(long characterId, IReadOnlyList<ItemStackRow> rows) =>
        Attach(characterId, new InventoryLoad(rows, Array.Empty<ItemInstanceRow>(), 0, true), "", 1);

    /// <summary>
    /// Starts holding a character's items as loaded at login, with its class (the weapon types it may equip, R-3.5-07) and
    /// level (<c>ItemDef.minLevel</c>). False (nothing attached) when a row cannot be represented by the tables (an unknown
    /// item or container, a slot out of range or taken twice, a category its container does not take, equipment overlapping
    /// in Equipped): the character is refused like an unknown class.
    /// </summary>
    public bool Attach(long characterId, InventoryLoad load, string classId, int level)
    {
        if (_characters.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already attached");
        var items = new Inventory(_rules.ContainerRules);
        var instances = new Dictionary<long, InstanceInfo>();
        long seq = load.InstanceSeq;
        foreach (var row in load.Stacks)
        {
            if (!TryLocate(items, row.Container, row.Slot, out int index) || !_rules.TryGetItem(row.DefId, out var item)
                || !ItemCategories.IsStackable(item.Category) || !items.Rule(index).Accepts(item.Category)
                || row.Count <= 0 || row.Count > item.MaxStack || row.Rarity is < 0 or > byte.MaxValue)
                return false;
            items.BagAt(index).Load(row.Slot, new ItemStack((ushort)item.NetId, (byte)row.Rarity, row.Stolen, row.Count));
        }
        foreach (var row in load.Instances)
        {
            // The properties are parsed here, once (v1 and v2, WP-M3-2); a row whose properties the tables cannot represent
            // (malformed, a later version, an unknown stat or gem) refuses the login like an unknown item.
            if (!TryLocate(items, row.Container, row.Slot, out int index) || !_rules.TryGetItem(row.DefId, out var item)
                || !ItemCategories.IsInstance(item.Category) || !items.Rule(index).Accepts(item.Category)
                || row.Rarity is < 0 or > byte.MaxValue || row.InstanceId <= 0
                || !ItemProperties.TryParse(row.PropertiesJson, out var properties, out _) || !_rules.Represents(properties)
                || !instances.TryAdd(row.InstanceId, new InstanceInfo(row.DefId, (byte)row.Rarity, row.Stolen, row.NonTradable, properties)))
                return false;
            if (index == _equippedIndex && !FitsEquipped(items, item, row.Slot))
                return false;
            items.BagAt(index).Load(row.Slot, new ItemStack((ushort)item.NetId, (byte)row.Rarity, row.Stolen, 1, row.InstanceId));
            if (ItemInstanceIds.Creator(row.InstanceId) == characterId)
                seq = Math.Max(seq, ItemInstanceIds.Seq(row.InstanceId)); // never hand out an id that exists
        }
        var inventory = new CharacterInventory(characterId, items, instances, seq, classId, level);
        CopyCommitted(items, inventory.Committed);
        _characters.Add(characterId, inventory);
        RebuildGear(inventory);
        return true;
    }

    /// <summary>
    /// Drops a character's cached items (it left the world), its reservations with them (memory only). Unsaved changes of a
    /// faulted character are lost with it.
    /// </summary>
    public void Detach(long characterId)
    {
        _characters.Remove(characterId);
        DetachGold(characterId);
    }

    /// <summary>
    /// The properties of an instance the character holds (spec 4.1 M3 item model, parsed once). Read-only for callers: a
    /// change goes through this service. False for an unknown character or instance.
    /// </summary>
    public bool TryGetProperties(long characterId, long instanceId, out ItemProperties properties)
    {
        properties = null!;
        if (!_characters.TryGetValue(characterId, out var inventory) || !inventory.Instances.TryGetValue(instanceId, out var info))
            return false;
        properties = info.Properties;
        return true;
    }

    /// <summary>The Normal bag as the world sees it now (including changes not yet saved).</summary>
    public Bag Current(long characterId) => Get(characterId).Items.BagAt(_normalIndex);

    /// <summary>Every container as the world sees it now (including changes not yet saved). Read-only for callers.</summary>
    public Inventory CurrentInventory(long characterId) => Get(characterId).Items;

    /// <summary>The Normal bag as last committed to the database: what the player is shown (rule 5).</summary>
    public IReadOnlyList<ItemStack> Committed(long characterId) => Get(characterId).Committed[_normalIndex];

    /// <summary>Container <paramref name="containerIndex"/> (<see cref="ItemRules.Containers"/> order) as last committed.</summary>
    public IReadOnlyList<ItemStack> CommittedContainer(long characterId, int containerIndex) =>
        Get(characterId).Committed[containerIndex];

    /// <summary>
    /// Non-tradable (R-4.1-01, carried and inert until trade, R-4.1-04): sell-only and quest items by their category, an
    /// instance also by its own flag.
    /// </summary>
    public bool IsNonTradable(long characterId, in ItemStack stack)
    {
        if (_rules.TryGetItem(stack.ItemNetId, out var item) && ItemCategories.IsNonTradable(item.Category))
            return true;
        return stack.IsInstance && _characters.TryGetValue(characterId, out var inventory)
               && inventory.Instances.TryGetValue(stack.InstanceId, out var info) && info.NonTradable;
    }

    /// <summary>The operation <paramref name="opKey"/> was applied (committed or not) since the character was attached.</summary>
    public bool WasApplied(long characterId, string opKey) =>
        _characters.TryGetValue(characterId, out var inventory) && inventory.SeenOps.ContainsKey(opKey);

    /// <summary>Changes made and not yet in a committed save.</summary>
    public bool HasUnsaved(long characterId)
    {
        var inventory = Get(characterId);
        return inventory.PendingOps.Count > 0 || inventory.InFlight != null;
    }

    /// <summary>
    /// Read-only (WP16, user decision 2026-10-26): true once the operation <paramref name="opKey"/> was applied and is in
    /// a committed save, so its caller may confirm it to the player (rule 5). False for an unknown key, a refused
    /// operation (never recorded), or one still waiting for or riding on a save.
    /// </summary>
    public bool IsCommitted(long characterId, string opKey)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || !inventory.SeenOps.ContainsKey(opKey))
            return false;
        foreach (var op in inventory.PendingOps)
        {
            if (op.OpKey == opKey)
                return false;
        }
        if (inventory.InFlight is { } inFlight)
        {
            foreach (var op in inFlight.Batch.Ops)
            {
                if (op.OpKey == opKey)
                    return false;
            }
        }
        return true;
    }

    /// <summary>Units of <paramref name="item"/> that fit now in the container that receives its category (take what fits, R-4.3-02 M1).</summary>
    public int SpaceFor(long characterId, ItemDef item, byte rarity)
    {
        int index = ReceiverIndex(item.Category);
        return index < 0
            ? 0
            : Get(characterId).Items.BagAt(index).SpaceFor(new ItemStack((ushort)item.NetId, rarity, false, 1), item.MaxStack);
    }

    /// <summary>A container takes new items of this category (<c>ContainerDef.receivesNewItems</c>).</summary>
    public bool Accepts(ItemDef item) => ReceiverIndex(item.Category) >= 0;

    /// <summary>
    /// Adds up to <paramref name="count"/> units (as many as fit) under <paramref name="opKey"/>, into the container that
    /// receives the item's category: stackables fill stacks then empty slots, an instance category creates one instance per
    /// unit, each with a new id (spec 4.1 M2 item model). Nothing is added when no container takes the category or it is
    /// full (<see cref="AddResult.Added"/> 0, and no operation is recorded).
    /// </summary>
    public AddResult TryAdd(long characterId, ItemDef item, byte rarity, int count, string opKey, InventoryReason reason, string source) =>
        TryAdd(characterId, item, rarity, count, opKey, reason, source, null);

    /// <summary>
    /// The same, with the properties each new instance starts with (spec 4.1 M3 item model: a crafted item's stats snapshot,
    /// quality, crafter and taint; the forge's output, WP-M3-6). Each instance gets its own copy, with its original owner set
    /// to the character. Null: the defaults. A stackable item takes no properties; <paramref name="stolen"/> marks new stacks
    /// stolen (a forge output from stolen inputs, R-4.4-03, R-5.2-04 "stolen-tag inheritance applies").
    /// </summary>
    public AddResult TryAdd(long characterId, ItemDef item, byte rarity, int count, string opKey, InventoryReason reason, string source,
        ItemProperties? properties, bool stolen = false)
    {
        if (count <= 0)
            throw new ArgumentOutOfRangeException(nameof(count));
        if (properties != null && (!ItemCategories.IsInstance(item.Category) || !_rules.Represents(properties)))
            throw new ArgumentException($"{item.Id}: properties are for instances whose stats and gems the tables know", nameof(properties));
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return new AddResult(previous, true);
        int index = ReceiverIndex(item.Category);
        if (index < 0)
            return new AddResult(0, false);
        var bag = inventory.Items.BagAt(index);
        _changes.Clear();
        int added = 0;
        long firstInstance = 0;
        if (ItemCategories.IsInstance(item.Category))
        {
            int slot = -1;
            while (added < count && (slot = bag.FirstEmpty(slot + 1)) >= 0 && NewInstance(inventory, item, rarity, properties) is { } stack)
            {
                if (added == 0)
                    firstInstance = stack.InstanceId;
                bag.Add(stack, 1, _changes);
                added++;
            }
        }
        else
        {
            added = bag.Add(new ItemStack((ushort)item.NetId, rarity, stolen, count), item.MaxStack, _changes);
        }
        if (added > 0)
            Record(inventory, opKey, AuditKind.Add, reason, source, added, FromBag(index));
        return new AddResult(added, false, firstInstance);
    }

    /// <summary>Removes <paramref name="count"/> units from one slot of the Normal bag (destroyed). False, and nothing recorded, if it holds fewer.</summary>
    public bool TryRemove(long characterId, int slot, int count, string opKey, InventoryReason reason, string source) =>
        TryRemove(characterId, _rules.NormalBag.Code, slot, count, opKey, reason, source);

    /// <summary>
    /// Removes <paramref name="count"/> units from one slot of a container (an instance: count 1). False, and nothing
    /// recorded, if it holds fewer, or fewer that no reservation holds (<see cref="Unreserved"/>; a reservation is consumed
    /// by <see cref="TryRemoveReserved"/>).
    /// </summary>
    public bool TryRemove(long characterId, int container, int slot, int count, string opKey, InventoryReason reason, string source)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return previous > 0;
        if (container is < 0 or > byte.MaxValue || !inventory.Items.TryIndex((byte)container, out int index))
            return false;
        var bag = inventory.Items.BagAt(index);
        if (slot >= 0 && slot < bag.SlotCount && !LeavesReserved(inventory, (byte)container, slot, count, bag[slot]))
            return false;
        _changes.Clear();
        if (!bag.TryRemove(slot, count, _changes))
            return false;
        Record(inventory, opKey, AuditKind.Remove, reason, source, count, FromBag(index));
        return true;
    }

    /// <summary>Moves a stack onto another slot of the Normal bag: into an empty slot, onto the same key (merge), or a swap.</summary>
    public BagMoveResult TryMove(long characterId, int fromSlot, int toSlot, string opKey, InventoryReason reason)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return (BagMoveResult)previous;
        byte normal = (byte)_rules.NormalBag.Code;
        if (MoveTouchesReserved(inventory, new InventoryLocation(normal, fromSlot), new InventoryLocation(normal, toSlot)))
            return BagMoveResult.Refused; // reserved units stay where they are (WP-M3-2)
        _changes.Clear();
        var bag = inventory.Items.BagAt(_normalIndex);
        int maxStack = fromSlot >= 0 && fromSlot < bag.SlotCount && !bag[fromSlot].IsEmpty
                       && _rules.TryGetItem(bag[fromSlot].ItemNetId, out var item) ? item.MaxStack : 1;
        var result = bag.TryMove(fromSlot, toSlot, maxStack, _changes);
        if (result != BagMoveResult.Refused)
            Record(inventory, opKey, AuditKind.Move, reason, $"slot:{fromSlot}->{toSlot}", (int)result, FromBag(_normalIndex));
        return result;
    }

    public InventoryBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || inventory.PendingOps.Count == 0)
            return null;
        if (inventory.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a batch in flight");

        var items = inventory.Items;
        var slots = new List<SlotState>(inventory.PendingSlots.Count);
        foreach (int key in inventory.PendingSlots) // sorted set: containers and slots in order (rule 6)
        {
            byte code = (byte)(key >> 16);
            int slot = key & 0xFFFF;
            items.TryIndex(code, out int index);
            var stack = items.BagAt(index)[slot];
            // A slot an instance now holds has no stack row (its row is in item_instances).
            slots.Add(stack.IsEmpty || stack.IsInstance
                ? new SlotState(code, slot, null, 0, false, 0)
                : new SlotState(code, slot, DefIdOf(stack.ItemNetId), stack.Rarity, stack.Stolen, stack.Count));
        }

        List<InstanceState>? instances = null;
        if (inventory.PendingInstances.Count > 0)
        {
            instances = new List<InstanceState>(inventory.PendingInstances.Count);
            var found = new Dictionary<long, InventoryLocation>(inventory.PendingInstances.Count);
            for (int index = 0; index < items.ContainerCount; index++)
            {
                var bag = items.BagAt(index);
                for (int slot = 0; slot < bag.SlotCount; slot++)
                {
                    if (bag[slot].IsInstance && inventory.PendingInstances.Contains(bag[slot].InstanceId))
                        found[bag[slot].InstanceId] = new InventoryLocation(items.Rule(index).Code, slot);
                }
            }
            foreach (long id in inventory.PendingInstances) // id order (rule 6)
            {
                var info = inventory.Instances[id];
                bool removed = !found.TryGetValue(id, out var at);
                // Serialized once per change, here (always v2; a v1 row is rewritten when it is next touched, WP-M3-2).
                instances.Add(new InstanceState(id, at.Container, at.Slot, info.DefId, info.Rarity, info.Stolen, info.NonTradable,
                    info.Properties.ToJson(), removed));
                if (removed)
                    inventory.Instances.Remove(id);
            }
        }

        var batch = new InventoryBatch(inventory.PendingOps.ToList(), inventory.PendingAudit.ToList(), slots, instances,
            inventory.SeqChanged ? inventory.InstanceSeq : -1, TakeGold(characterId, saveId));
        var snapshot = new ItemStack[items.ContainerCount][];
        for (int index = 0; index < items.ContainerCount; index++)
            snapshot[index] = new ItemStack[items.BagAt(index).SlotCount];
        CopyCommitted(items, snapshot);
        inventory.InFlight = new InFlightBatch(saveId, snapshot, batch);
        inventory.PendingOps.Clear();
        inventory.PendingAudit.Clear();
        inventory.PendingSlots.Clear();
        inventory.PendingInstances.Clear();
        inventory.SeqChanged = false;
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || inventory.InFlight is not { } inFlight
            || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        inventory.InFlight = null;
        _changedSlots.Clear();
        if (_equippedIndex >= 0)
        {
            var before = inventory.Committed[_equippedIndex];
            var after = inFlight.Snapshot[_equippedIndex];
            for (int slot = 0; slot < before.Length; slot++)
            {
                if (!before[slot].Equals(after[slot]))
                    _changedSlots.Add((byte)slot);
            }
        }
        for (int index = 0; index < inventory.Committed.Length; index++)
            Array.Copy(inFlight.Snapshot[index], inventory.Committed[index], inventory.Committed[index].Length);
        if (_changedSlots.Count > 0)
            RebuildGear(inventory);
        PublishCommitted(characterId, inFlight.Batch);
        if (OpCommitted != null)
        {
            foreach (var op in inFlight.Batch.Ops)
                OpCommitted(characterId, op.OpKey, Enum.TryParse<InventoryReason>(op.Reason, out var reason) ? reason : InventoryReason.None);
        }
        foreach (byte slot in _changedSlots)
            _events?.Publish(new EquipmentChanged(characterId, slot), characterId);
        CommitGold(characterId, saveId);
        ContentsCommitted?.Invoke(characterId);
    }

    /// <summary>Records an applied operation from the changes it made (<paramref name="changes"/>: container code and slot).</summary>
    private void Record(CharacterInventory inventory, string opKey, AuditKind kind, InventoryReason reason, string source, int result,
        List<InventorySlotChange> changes)
    {
        inventory.SeenOps.Add(opKey, result);
        inventory.PendingOps.Add(new InventoryOpRecord(opKey, reason.ToString(), source));
        int seq = 0;
        foreach (var slotChange in changes)
        {
            byte code = slotChange.Container;
            var change = slotChange.Change;
            inventory.PendingSlots.Add((code << 16) | change.Slot);
            var before = change.Before;
            var after = change.After;
            if (before.IsInstance)
                inventory.PendingInstances.Add(before.InstanceId);
            if (after.IsInstance)
                inventory.PendingInstances.Add(after.InstanceId);
            if (!before.IsEmpty && !after.IsEmpty && before.SameKey(after))
            {
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, code, change.Slot, after, after.Count - before.Count, after.Count));
                continue;
            }
            // A slot that changed item (filled, emptied or swapped): what left and what came, each with its own row.
            if (!before.IsEmpty)
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, code, change.Slot, before, -before.Count, 0));
            if (!after.IsEmpty)
                inventory.PendingAudit.Add(Audit(opKey, seq++, kind, code, change.Slot, after, after.Count, after.Count));
        }
        MarkClass2?.Invoke(inventory.CharacterId);
    }

    /// <summary>The bag changes in <see cref="_changes"/> as changes of container <paramref name="index"/>.</summary>
    private List<InventorySlotChange> FromBag(int index)
    {
        _inventoryChanges.Clear();
        byte code = _rules.ContainerRules[index].Code;
        foreach (var change in _changes)
            _inventoryChanges.Add(new InventorySlotChange(code, change));
        return _inventoryChanges;
    }

    private AuditEntry Audit(string opKey, int seq, AuditKind kind, int container, int slot, in ItemStack stack, int delta, int after) =>
        new(opKey, seq, kind, container, slot, DefIdOf(stack.ItemNetId), stack.Rarity, stack.Stolen, delta, after, stack.InstanceId);

    private string DefIdOf(ushort netId) =>
        _rules.TryGetItem(netId, out var item) ? item.Id : throw new InvalidOperationException($"no ItemDef with netId {netId}");

    private void PublishCommitted(long characterId, InventoryBatch batch)
    {
        if (_events == null)
            return;
        // One event per add or remove operation and item (an operation consuming a reservation may remove several items,
        // WP-M3-2), in audit order; moves, equips and unequips create and destroy nothing.
        var perItem = new List<(string DefId, int Rarity, AuditKind Kind, int Net)>();
        foreach (var op in batch.Ops)
        {
            var reason = Enum.TryParse<InventoryReason>(op.Reason, out var parsed) ? parsed : InventoryReason.None;
            perItem.Clear();
            foreach (var audit in batch.Audit)
            {
                if (audit.OpKey != op.OpKey || audit.Kind is not (AuditKind.Add or AuditKind.Remove))
                    continue;
                int at = perItem.FindIndex(p => p.DefId == audit.DefId && p.Rarity == audit.Rarity && p.Kind == audit.Kind);
                if (at < 0)
                    perItem.Add((audit.DefId, audit.Rarity, audit.Kind, audit.CountDelta));
                else
                    perItem[at] = perItem[at] with { Net = perItem[at].Net + audit.CountDelta };
            }
            foreach (var (defId, rarity, kind, net) in perItem)
            {
                if (!_rules.TryGetItem(defId, out var item))
                    continue;
                if (kind == AuditKind.Add)
                    _events.Publish(new ItemCreated(characterId, (ushort)item.NetId, (byte)rarity, net, reason), characterId);
                else
                {
                    _events.Publish(new ItemDestroyed(characterId, (ushort)item.NetId, (byte)rarity, -net, reason), characterId);
                    // Spec 3.4 consumable use (WP-M3-3): the activity event, once per operation, after its commit.
                    if (reason == InventoryReason.ItemUsed)
                        _events.Publish(new ItemUsed(characterId, EntityOf?.Invoke(characterId) ?? 0, "", 0, item.NetId, -net, op.OpKey),
                            characterId);
                }
            }
        }
    }

    /// <summary>
    /// A new instance of <paramref name="item"/> with the character's next id, or null when its ids ran out. Its properties
    /// are a copy of <paramref name="properties"/> (the defaults when null) owned by the character.
    /// </summary>
    private static ItemStack? NewInstance(CharacterInventory inventory, ItemDef item, byte rarity, ItemProperties? properties = null)
    {
        if (inventory.InstanceSeq >= ItemInstanceIds.MaxSeq || inventory.CharacterId > ItemInstanceIds.MaxCharacterId)
            return null;
        long id = ItemInstanceIds.Make(inventory.CharacterId, ++inventory.InstanceSeq);
        inventory.SeqChanged = true;
        var own = properties?.Clone() ?? ItemInstanceIds.NewPropertiesOf(inventory.CharacterId);
        own.OriginalOwner = inventory.CharacterId;
        inventory.Instances.Add(id, new InstanceInfo(item.Id, rarity, false, false, own));
        return new ItemStack((ushort)item.NetId, rarity, false, 1, id);
    }

    private int ReceiverIndex(ItemCategory category) => _rules.ReceiverOf(category) is { } container ? IndexOf(container.Code) : -1;

    private int IndexOf(int code)
    {
        for (int i = 0; i < _rules.ContainerRules.Count; i++)
        {
            if (_rules.ContainerRules[i].Code == code)
                return i;
        }
        return -1;
    }

    private static bool TryLocate(Inventory items, int container, int slot, out int index)
    {
        index = -1;
        return container is > 0 and <= byte.MaxValue && items.TryIndex((byte)container, out index) && slot >= 0
               && slot < items.BagAt(index).SlotCount && items.BagAt(index)[slot].IsEmpty;
    }

    /// <summary>An equipped instance sits at its lowest slot and shares no slot with another equipped item.</summary>
    private bool FitsEquipped(Inventory items, ItemDef item, int slot)
    {
        ushort mask = _rules.EquipSlotMask(item.NetId);
        if (item.Category != ItemCategory.Equipment || mask == 0)
            return false;
        int lowest = 0;
        while ((mask & (1 << lowest)) == 0)
            lowest++;
        if (lowest != slot)
            return false;
        for (int other = 0; other < 16; other++)
        {
            if ((mask & (1 << other)) != 0 && items.CoveredBy(other, _lookup) >= 0)
                return false;
        }
        return true;
    }

    private static void CopyCommitted(Inventory items, ItemStack[][] target)
    {
        for (int index = 0; index < items.ContainerCount; index++)
            items.BagAt(index).CopyTo(target[index]);
    }

    private CharacterInventory Get(long characterId) =>
        _characters.TryGetValue(characterId, out var inventory)
            ? inventory
            : throw new KeyNotFoundException($"character {characterId} has no inventory attached");

    private readonly List<byte> _changedSlots = new();

    private sealed record InFlightBatch(Guid SaveId, ItemStack[][] Snapshot, InventoryBatch Batch);

    /// <summary>What an instance carries besides its stack (spec 4.1 M2 item model; the properties parsed, M3 item model).</summary>
    private sealed record InstanceInfo(string DefId, byte Rarity, bool Stolen, bool NonTradable, ItemProperties Properties);

    private sealed class CharacterInventory
    {
        public CharacterInventory(long characterId, Inventory items, Dictionary<long, InstanceInfo> instances, long instanceSeq,
            string classId, int level)
        {
            CharacterId = characterId;
            Items = items;
            Instances = instances;
            InstanceSeq = instanceSeq;
            ClassId = classId;
            Level = level;
            Committed = new ItemStack[items.ContainerCount][];
            for (int i = 0; i < items.ContainerCount; i++)
                Committed[i] = new ItemStack[items.BagAt(i).SlotCount];
        }

        public long CharacterId { get; }
        public Inventory Items { get; }
        public ItemStack[][] Committed { get; }

        /// <summary>Every instance the character holds now, plus those removed and not yet in a batch.</summary>
        public Dictionary<long, InstanceInfo> Instances { get; }

        /// <summary>The last instance sequence number used (<c>characters.item_instance_seq</c>).</summary>
        public long InstanceSeq { get; set; }

        public bool SeqChanged { get; set; }
        public string ClassId { get; }
        public int Level { get; set; }

        /// <summary>
        /// R-3.5-07 (WP-M2-5): the weapon types the character may equip, from its class line and learned weapon unlocks
        /// (<c>SkillTreeRules.UsableWeapons</c>, a <c>WeaponMasks</c> mask); null without the skill tree: the class row's list.
        /// </summary>
        public uint? UsableWeapons { get; set; }

        /// <summary>
        /// Keys of every operation applied since the character was attached, with their result (rule 5: a repeated key
        /// returns the first result). Across logins the database key is the backstop.
        /// </summary>
        public Dictionary<string, int> SeenOps { get; } = new(StringComparer.Ordinal);

        public List<InventoryOpRecord> PendingOps { get; } = new();
        public List<AuditEntry> PendingAudit { get; } = new();

        /// <summary>Touched slots as <c>(container code &lt;&lt; 16) | slot</c>, in order.</summary>
        public SortedSet<int> PendingSlots { get; } = new();

        public SortedSet<long> PendingInstances { get; } = new();
        public InFlightBatch? InFlight { get; set; }

        /// <summary>The committed equipped set as bonuses and weapon types (<see cref="IEquipmentStatSource"/>).</summary>
        public List<ItemStatBonus> Bonuses { get; } = new();

        public List<WeaponType> WeaponTypes { get; } = new();

        /// <summary>Reservations by key (memory only, WP-M3-2).</summary>
        public Dictionary<string, List<ReservedSlot>> Reservations { get; } = new(StringComparer.Ordinal);

        /// <summary>Reserved units per slot, as <c>(container code &lt;&lt; 16) | slot</c>: the O(1) check of every path.</summary>
        public Dictionary<int, int> ReservedUnits { get; } = new();
    }
}
