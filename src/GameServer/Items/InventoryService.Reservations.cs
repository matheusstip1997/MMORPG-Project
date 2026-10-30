using GameServer.Events;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>The outcome of <see cref="InventoryService.Reserve"/>; nothing is reserved unless it is <see cref="Ok"/>.</summary>
public enum ReserveResult : byte
{
    Ok = 0,
    /// <summary>Other reservations already hold units the request needs (two reservations cannot take the same units).</summary>
    ItemReserved = 1,
    /// <summary>A slot is unknown, empty, or holds fewer units than asked.</summary>
    NotEnough = 2,
    /// <summary>The reservation key is already held.</summary>
    KeyInUse = 3,
}

/// <summary>Units of one slot to reserve: <see cref="Count"/> of a stack, or 1 for an instance.</summary>
public readonly record struct ReserveItem(InventoryLocation At, int Count);

/// <summary>
/// Reservations (spec 4.1 <c>Reserve</c>/<c>Release</c>, R-5.3-03 "materials are reserved when the forge session starts",
/// R-2.1-10 lost sessions; WP-M3-2). A reservation holds units of stacks, or instances, under a key (the forge session's
/// operation key): a reserved unit cannot be moved, split, merged away, swapped, equipped, unequipped, displaced, sold,
/// delivered, removed or used by any other path (<c>ItemReserved</c>). It ends when its owner releases it (a cancel), when
/// the character's session ends (disconnect, kick, lost ownership, shutdown: <see cref="ReleaseAll"/>), when the character
/// leaves the world (<see cref="Detach"/>), or when a later remove consumes it (<see cref="TryRemoveReserved"/>).
/// <para>
/// Memory only: nothing is written for a reservation, so a crash loses nothing and duplicates nothing (the inputs are
/// still in the bag after a restart and no output exists). A reserved slot is locked in place, so the reservation keeps
/// pointing at its units; new units of the same key may still merge onto it (a pickup), which moves no reserved unit.
/// </para>
/// </summary>
public sealed partial class InventoryService
{
    private readonly List<InventorySlotChange> _reservedChanges = new();

    /// <summary>A character's reservations changed (the client's dimmed slots; set by the world).</summary>
    public Action<long>? ReservationsChanged { get; set; }

    /// <summary>
    /// Reserves all of <paramref name="items"/> under <paramref name="key"/>, or nothing. A slot may be named several times
    /// (the counts add up) and several reservations may share a stack while their total stays within its count.
    /// </summary>
    public ReserveResult Reserve(long characterId, string key, IReadOnlyList<ReserveItem> items)
    {
        if (items.Count == 0)
            throw new ArgumentException("a reservation needs at least one slot", nameof(items));
        var inventory = Get(characterId);
        if (inventory.Reservations.ContainsKey(key))
            return ReserveResult.KeyInUse;

        var wanted = new SortedDictionary<int, int>(); // slot key -> units, in container and slot order (rule 6)
        foreach (var item in items)
        {
            if (item.Count <= 0)
                throw new ArgumentOutOfRangeException(nameof(items), $"{item.At}: a reservation takes at least one unit");
            int slotKey = SlotKey(item.At);
            wanted[slotKey] = (wanted.TryGetValue(slotKey, out int sum) ? sum : 0) + item.Count;
        }
        var reservation = new List<ReservedSlot>(wanted.Count);
        foreach (var (slotKey, count) in wanted)
        {
            var at = new InventoryLocation((byte)(slotKey >> 16), slotKey & 0xFFFF);
            var stack = inventory.Items.At(at);
            if (stack.IsEmpty || stack.Count < count)
                return ReserveResult.NotEnough;
            if (stack.Count - Reserved(inventory, slotKey) < count)
                return ReserveResult.ItemReserved;
            reservation.Add(new ReservedSlot(at, count, stack.ItemNetId, stack.InstanceId));
        }
        inventory.Reservations.Add(key, reservation);
        foreach (var slot in reservation)
            inventory.ReservedUnits[SlotKey(slot.At)] = Reserved(inventory, SlotKey(slot.At)) + slot.Count;
        ReservationsChanged?.Invoke(characterId);
        return ReserveResult.Ok;
    }

    /// <summary>Ends a reservation without consuming it (a cancel). False when the key holds nothing.</summary>
    public bool Release(long characterId, string key)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || !inventory.Reservations.TryGetValue(key, out var reservation))
            return false;
        Drop(inventory, key, reservation);
        ReservationsChanged?.Invoke(characterId);
        return true;
    }

    /// <summary>Ends every reservation of the character (its session ended: disconnect, kick, lost session, shutdown).</summary>
    public void ReleaseAll(long characterId)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || inventory.Reservations.Count == 0)
            return;
        inventory.Reservations.Clear();
        inventory.ReservedUnits.Clear();
        ReservationsChanged?.Invoke(characterId);
    }

    public bool HasReservation(long characterId, string key) =>
        _characters.TryGetValue(characterId, out var inventory) && inventory.Reservations.ContainsKey(key);

    /// <summary>Some units in this slot are reserved (it is locked in place).</summary>
    public bool IsReserved(long characterId, InventoryLocation at) =>
        _characters.TryGetValue(characterId, out var inventory) && Reserved(inventory, SlotKey(at)) > 0;

    /// <summary>
    /// Units of the slot that no reservation holds: what any path may move, sell, deliver or use (the check of the use-item
    /// path, WP-M3-3). 0 for an empty or unknown slot.
    /// </summary>
    public int Unreserved(long characterId, InventoryLocation at)
    {
        if (!_characters.TryGetValue(characterId, out var inventory))
            return 0;
        var stack = inventory.Items.At(at);
        return stack.IsEmpty ? 0 : Math.Max(0, stack.Count - Reserved(inventory, SlotKey(at)));
    }

    /// <summary>
    /// Consumes a reservation: removes every unit it holds (destroyed) in one audited, idempotent operation
    /// <paramref name="opKey"/> and ends it (the forge's inputs, R-5.3-03). A repeated key returns the first result. False,
    /// and nothing changed, when the reservation does not exist (released, or its session ended).
    /// </summary>
    public bool TryRemoveReserved(long characterId, string key, string opKey, InventoryReason reason, string source)
    {
        var inventory = Get(characterId);
        if (inventory.SeenOps.TryGetValue(opKey, out var previous))
            return previous > 0;
        if (!inventory.Reservations.TryGetValue(key, out var reservation))
            return false;
        foreach (var slot in reservation)
        {
            var stack = inventory.Items.At(slot.At);
            if (stack.ItemNetId != slot.ItemNetId || stack.InstanceId != slot.InstanceId || stack.Count < slot.Count)
                throw new InvalidOperationException($"reservation {key}: slot {slot.At} changed under it");
        }
        Drop(inventory, key, reservation);
        _reservedChanges.Clear();
        int total = 0;
        foreach (var slot in reservation)
        {
            inventory.Items.TryIndex(slot.At.Container, out int index);
            _changes.Clear();
            inventory.Items.BagAt(index).TryRemove(slot.At.Slot, slot.Count, _changes);
            foreach (var change in _changes)
                _reservedChanges.Add(new InventorySlotChange(slot.At.Container, change));
            total += slot.Count;
        }
        Record(inventory, opKey, AuditKind.Remove, reason, source, total, _reservedChanges);
        ReservationsChanged?.Invoke(characterId);
        return true;
    }

    /// <summary>
    /// R-4.4-03 for a reservation's inputs: true when any reserved unit is stolen-tagged or a tainted instance
    /// (<see cref="ItemRules.TaintsOutput"/>). False for an unknown reservation.
    /// </summary>
    public bool TaintsOutput(long characterId, string key)
    {
        if (!_characters.TryGetValue(characterId, out var inventory) || !inventory.Reservations.TryGetValue(key, out var reservation))
            return false;
        var inputs = new List<(bool Stolen, bool Tainted)>(reservation.Count);
        foreach (var slot in reservation)
        {
            var stack = inventory.Items.At(slot.At);
            bool tainted = stack.IsInstance && inventory.Instances.TryGetValue(stack.InstanceId, out var info) && info.Properties.Tainted;
            inputs.Add((stack.Stolen, tainted));
        }
        return ItemRules.TaintsOutput(inputs);
    }

    /// <summary>
    /// A move would move reserved units: its source or target slot holds some, or an equip (or an unequip onto gear) would
    /// displace a reserved equipped item. The shared rules decide the rest.
    /// </summary>
    private bool MoveTouchesReserved(CharacterInventory inventory, InventoryLocation from, InventoryLocation to)
    {
        if (inventory.ReservedUnits.Count == 0)
            return false;
        if (Reserved(inventory, SlotKey(from)) > 0 || Reserved(inventory, SlotKey(to)) > 0)
            return true;
        if (_equippedIndex < 0 || !IsGearChange(from, to))
            return false;
        var incoming = inventory.Items.IncomingEquip(from, to);
        if (incoming.IsEmpty)
            return false;
        ushort mask = _rules.EquipSlotMask(incoming.ItemNetId);
        var equipped = inventory.Items.BagAt(_equippedIndex);
        byte code = inventory.Items.Rule(_equippedIndex).Code;
        for (int slot = 0; slot < equipped.SlotCount; slot++)
        {
            if (!equipped[slot].IsEmpty && (_rules.EquipSlotMask(equipped[slot].ItemNetId) & mask) != 0
                && Reserved(inventory, SlotKey(new InventoryLocation(code, slot))) > 0)
                return true;
        }
        return false;
    }

    /// <summary>Units a removal of <paramref name="count"/> from a slot may take without touching reserved ones.</summary>
    private static bool LeavesReserved(CharacterInventory inventory, byte container, int slot, int count, in ItemStack stack)
    {
        int reserved = Reserved(inventory, SlotKey(new InventoryLocation(container, slot)));
        return reserved == 0 || stack.Count - count >= reserved;
    }

    private static void Drop(CharacterInventory inventory, string key, List<ReservedSlot> reservation)
    {
        inventory.Reservations.Remove(key);
        foreach (var slot in reservation)
        {
            int slotKey = SlotKey(slot.At);
            int left = Reserved(inventory, slotKey) - slot.Count;
            if (left > 0)
                inventory.ReservedUnits[slotKey] = left;
            else
                inventory.ReservedUnits.Remove(slotKey);
        }
    }

    private static int Reserved(CharacterInventory inventory, int slotKey) =>
        inventory.ReservedUnits.TryGetValue(slotKey, out int units) ? units : 0;

    private static int SlotKey(InventoryLocation at) => (at.Container << 16) | (at.Slot & 0xFFFF);

    /// <summary>Units of one slot a reservation holds, and what lay there when it was made.</summary>
    private readonly record struct ReservedSlot(InventoryLocation At, int Count, ushort ItemNetId, long InstanceId);
}
