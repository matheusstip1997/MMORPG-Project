using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Simulation.Items;

namespace GameServer.Items;

/// <summary>One side of a transfer between two characters: the units its reservation holds and the gold it gives.</summary>
/// <param name="ReservationKey">The reservation holding the side's offered units (a trade's offer); null when it gives none.</param>
public readonly record struct TransferSide(long CharacterId, string? ReservationKey, long Gold);

/// <summary>Why a transfer was not applied (nothing changed); <see cref="None"/> when it was.</summary>
public enum TransferRefusal : byte
{
    None = 0,
    /// <summary>An offered unit is no longer where its reservation said (or the reservation ended), or an unknown item.</summary>
    Changed = 1,
    /// <summary>A side gives more gold than it holds.</summary>
    GoldShort = 2,
    /// <summary>A side would hold more than <see cref="InventoryService.MaxGold"/>.</summary>
    GoldCap = 3,
    /// <summary>A side's containers cannot take everything it receives (stack merges counted).</summary>
    BagFull = 4,
    /// <summary>Nothing would move.</summary>
    Empty = 5,
}

/// <summary>One operation key per side of an applied transfer: the side's changes are committed once its key is (rule 5).</summary>
public readonly record struct TransferKeys(string KeyA, string KeyB);

/// <summary>
/// Transfers to another owner (spec 4.5, 7.1; WP-M4-4): <b>this service is still the only writer of items and gold</b> (rule 4).
/// Units move, never created or destroyed: the giver's operation has audit kind <see cref="AuditKind.TransferOut"/>, the
/// receiver's <see cref="AuditKind.TransferIn"/>, gold entries flow <c>move</c>; an instance keeps its id and its properties
/// (with the giver appended to its owner history, spec 4.1). <c>ItemTransferred</c> goes out after the commit instead of
/// <c>ItemCreated</c>/<c>ItemDestroyed</c>.
/// <list type="bullet">
/// <item><see cref="TryTransfer"/>: two online characters at once (a trade). Each side's changes ride its own character save,
/// and the caller saves both in <b>one</b> job (<c>SaveScheduler.RequestTransferSave</c>), so they commit together.</item>
/// <item><see cref="TryHoldOut"/> and <see cref="TryReceive"/>: one character and escrow (<c>EscrowService</c>, the only
/// holder of assets outside a character).</item>
/// </list>
/// </summary>
public sealed partial class InventoryService
{
    private readonly List<InventorySlotChange> _transferChanges = new();
    private readonly Dictionary<string, TransferKeys> _transfers = new(StringComparer.Ordinal);

    /// <summary>
    /// The caller is done with transfer <paramref name="opKey"/> (committed, or never saved): its repeat guard is dropped. A
    /// repeat after this is refused by the operation keys it recorded (<see cref="WasApplied"/>, the database keys).
    /// </summary>
    public void ForgetTransfer(string opKey) => _transfers.Remove(opKey);

    /// <summary>What <see cref="TryTransfer"/> would answer now, changing nothing.</summary>
    public TransferRefusal CheckTransfer(TransferSide a, TransferSide b) =>
        Plan(a, b, out _, out _);

    /// <summary>
    /// Swaps both sides' reserved units and gold under <paramref name="opKey"/>, all or nothing: each side's reservation is
    /// consumed (<c>{opKey}:{id}:out</c>), the other side receives its units (<c>{opKey}:{id}:in</c>: stacks merge, instances
    /// keep their ids) and gold (<c>{opKey}:{id}:g-</c>/<c>g+</c>). A repeated key returns the first keys and changes nothing.
    /// </summary>
    public TransferRefusal TryTransfer(TransferSide a, TransferSide b, string opKey, InventoryReason reason, int ownerHistoryMax,
        out TransferKeys keys)
    {
        if (_transfers.TryGetValue(opKey, out keys))
            return TransferRefusal.None;
        var refusal = Plan(a, b, out var unitsA, out var unitsB);
        if (refusal != TransferRefusal.None)
            return refusal;

        var inventoryA = Get(a.CharacterId);
        var inventoryB = Get(b.CharacterId);
        string? keyA = null, keyB = null;
        void Note(ref string? first, string key) => first ??= key;
        // Out first, so a side's own outgoing units free the slots its incoming ones may need (the check counted it so).
        if (unitsA.Count > 0)
            Note(ref keyA, TakeOut(inventoryA, a.ReservationKey!, $"{opKey}:{a.CharacterId}:out", reason, $"to:{b.CharacterId}", b.CharacterId));
        if (unitsB.Count > 0)
            Note(ref keyB, TakeOut(inventoryB, b.ReservationKey!, $"{opKey}:{b.CharacterId}:out", reason, $"to:{a.CharacterId}", a.CharacterId));
        if (unitsA.Count > 0)
            Note(ref keyB, PutIn(inventoryB, unitsA, $"{opKey}:{b.CharacterId}:in", reason, $"from:{a.CharacterId}", a.CharacterId,
                ownerHistoryMax, a.CharacterId));
        if (unitsB.Count > 0)
            Note(ref keyA, PutIn(inventoryA, unitsB, $"{opKey}:{a.CharacterId}:in", reason, $"from:{b.CharacterId}", b.CharacterId,
                ownerHistoryMax, b.CharacterId));
        if (a.Gold > 0)
        {
            Note(ref keyA, MoveGold(a.CharacterId, -a.Gold, $"{opKey}:{a.CharacterId}:g-", reason, $"to:{b.CharacterId}"));
            Note(ref keyB, MoveGold(b.CharacterId, a.Gold, $"{opKey}:{b.CharacterId}:g+", reason, $"from:{a.CharacterId}"));
        }
        if (b.Gold > 0)
        {
            Note(ref keyB, MoveGold(b.CharacterId, -b.Gold, $"{opKey}:{b.CharacterId}:g-", reason, $"to:{a.CharacterId}"));
            Note(ref keyA, MoveGold(a.CharacterId, b.Gold, $"{opKey}:{a.CharacterId}:g+", reason, $"from:{b.CharacterId}"));
        }
        keys = new TransferKeys(keyA!, keyB!);
        _transfers.Add(opKey, keys);
        return TransferRefusal.None;
    }

    /// <summary>
    /// Escrow hold (WP-M4-4): removes <paramref name="items"/> (units no reservation holds, never equipped gear) and
    /// <paramref name="gold"/> from the character as one transfer to escrow (<c>{opKey}:out</c>, <c>{opKey}:g-</c>), and fills
    /// <paramref name="held"/> with what left (instances with their properties). Returns the operation key to wait on, or null
    /// with the refusal when nothing changed. A repeated key returns null with <see cref="TransferRefusal.None"/>.
    /// </summary>
    public string? TryHoldOut(long characterId, IReadOnlyList<ReserveItem> items, long gold, string opKey, InventoryReason reason,
        string source, List<EscrowItemRow> held, out TransferRefusal refusal)
    {
        refusal = TransferRefusal.None;
        var inventory = Get(characterId);
        string outKey = $"{opKey}:out", goldKey = $"{opKey}:g-";
        if (inventory.SeenOps.ContainsKey(outKey) || inventory.SeenOps.ContainsKey(goldKey))
            return null;
        if (gold < 0)
            throw new ArgumentOutOfRangeException(nameof(gold));
        if (items.Count == 0 && gold == 0)
        {
            refusal = TransferRefusal.Empty;
            return null;
        }
        if (gold > Account(characterId).Current)
        {
            refusal = TransferRefusal.GoldShort;
            return null;
        }
        var wanted = new SortedDictionary<int, (InventoryLocation At, int Count)>(); // slot order (rule 6)
        foreach (var item in items)
        {
            if (item.Count <= 0)
                throw new ArgumentOutOfRangeException(nameof(items));
            int key = SlotKey(item.At);
            wanted[key] = (item.At, (wanted.TryGetValue(key, out var sum) ? sum.Count : 0) + item.Count);
        }
        var units = new List<(InventoryLocation At, MovedUnit Unit)>(wanted.Count);
        foreach (var (at, count) in wanted.Values)
        {
            if (!inventory.Items.TryIndex(at.Container, out int index) || index == _equippedIndex || at.Slot < 0
                || at.Slot >= inventory.Items.BagAt(index).SlotCount || Unreserved(characterId, at) < count
                || !TryUnit(inventory, inventory.Items.At(at), count, out var unit))
            {
                refusal = TransferRefusal.Changed;
                return null;
            }
            units.Add((at, unit));
        }

        string? key0 = null;
        if (units.Count > 0)
        {
            _transferChanges.Clear();
            int total = 0;
            foreach (var (at, unit) in units)
            {
                inventory.Items.TryIndex(at.Container, out int index);
                _changes.Clear();
                inventory.Items.BagAt(index).TryRemove(at.Slot, unit.Stack.Count, _changes);
                foreach (var change in _changes)
                    _transferChanges.Add(new InventorySlotChange(at.Container, change));
                total += unit.Stack.Count;
                held.Add(new EscrowItemRow(unit.Def.Id, unit.Stack.Rarity, unit.Stack.Stolen, unit.Stack.Count, unit.Stack.InstanceId,
                    unit.NonTradable, unit.Properties?.ToJson()));
            }
            Record(inventory, outKey, AuditKind.TransferOut, reason, source, total, _transferChanges);
            inventory.TransferPeers[outKey] = 0;
            key0 = outKey;
        }
        if (gold > 0)
        {
            string key = MoveGold(characterId, -gold, goldKey, reason, source);
            key0 ??= key;
        }
        return key0;
    }

    /// <summary>
    /// Escrow release to an online character (WP-M4-4): adds the held units (instances recreated with their ids and properties,
    /// <paramref name="previousOwner"/> appended to their owner history when it is another character) and gold
    /// (<c>{opKey}:in</c>, <c>{opKey}:g+</c>). All or nothing: refused <see cref="TransferRefusal.BagFull"/> or
    /// <see cref="TransferRefusal.GoldCap"/>. Returns the operation key to wait on, or null.
    /// </summary>
    public string? TryReceive(long characterId, IReadOnlyList<EscrowItemRow> items, long gold, string opKey, InventoryReason reason,
        string source, long previousOwner, int ownerHistoryMax, out TransferRefusal refusal)
    {
        refusal = TransferRefusal.None;
        var inventory = Get(characterId);
        string inKey = $"{opKey}:in", goldKey = $"{opKey}:g+";
        if (inventory.SeenOps.ContainsKey(inKey) || inventory.SeenOps.ContainsKey(goldKey))
            return null;
        if (gold < 0)
            throw new ArgumentOutOfRangeException(nameof(gold));
        if (items.Count == 0 && gold == 0)
        {
            refusal = TransferRefusal.Empty;
            return null;
        }
        var units = new List<MovedUnit>(items.Count);
        foreach (var row in items)
        {
            if (!_rules.TryGetItem(row.DefId, out var def) || row.Count <= 0 || row.Rarity is < 0 or > byte.MaxValue)
            {
                refusal = TransferRefusal.Changed;
                return null;
            }
            ItemProperties? properties = null;
            if (row.InstanceId != 0)
            {
                if (!ItemCategories.IsInstance(def.Category) || row.Count != 1
                    || !ItemProperties.TryParse(row.PropertiesJson ?? "", out var parsed, out _) || !_rules.Represents(parsed))
                {
                    refusal = TransferRefusal.Changed;
                    return null;
                }
                properties = parsed;
            }
            units.Add(new MovedUnit(def, new ItemStack((ushort)def.NetId, (byte)row.Rarity, row.Stolen, row.Count, row.InstanceId),
                row.NonTradable, properties));
        }
        if (gold > MaxGold - Account(characterId).Current)
        {
            refusal = TransferRefusal.GoldCap;
            return null;
        }
        if (!Fits(inventory, null, units))
        {
            refusal = TransferRefusal.BagFull;
            return null;
        }
        string? key0 = null;
        if (units.Count > 0)
            key0 = PutIn(inventory, units, inKey, reason, source, 0, ownerHistoryMax, previousOwner == characterId ? 0 : previousOwner);
        if (gold > 0)
        {
            string key = MoveGold(characterId, gold, goldKey, reason, source);
            key0 ??= key;
        }
        return key0;
    }

    /// <summary>The units a transfer moves: the item, the units (an instance with its id), its flags and latest properties.</summary>
    private readonly record struct MovedUnit(ItemDef Def, ItemStack Stack, bool NonTradable, ItemProperties? Properties);

    private TransferRefusal Plan(TransferSide a, TransferSide b, out List<MovedUnit> unitsA, out List<MovedUnit> unitsB)
    {
        unitsA = new List<MovedUnit>();
        unitsB = new List<MovedUnit>();
        if (a.CharacterId == b.CharacterId)
            throw new ArgumentException("a transfer needs two characters", nameof(b));
        if (a.Gold < 0 || b.Gold < 0)
            throw new ArgumentOutOfRangeException(nameof(a), "gold given cannot be negative");
        var inventoryA = Get(a.CharacterId);
        var inventoryB = Get(b.CharacterId);
        if (!Collect(inventoryA, a.ReservationKey, unitsA) || !Collect(inventoryB, b.ReservationKey, unitsB))
            return TransferRefusal.Changed;
        if (unitsA.Count == 0 && unitsB.Count == 0 && a.Gold == 0 && b.Gold == 0)
            return TransferRefusal.Empty;
        long goldA = Account(a.CharacterId).Current, goldB = Account(b.CharacterId).Current;
        if (a.Gold > goldA || b.Gold > goldB)
            return TransferRefusal.GoldShort;
        if (b.Gold > MaxGold - (goldA - a.Gold) || a.Gold > MaxGold - (goldB - b.Gold))
            return TransferRefusal.GoldCap;
        if (!Fits(inventoryB, unitsB.Count > 0 ? Reservation(inventoryB, b.ReservationKey!) : null, unitsA)
            || !Fits(inventoryA, unitsA.Count > 0 ? Reservation(inventoryA, a.ReservationKey!) : null, unitsB))
            return TransferRefusal.BagFull;
        return TransferRefusal.None;
    }

    /// <summary>The units a side's reservation holds, as they lie now; false when it ended or a slot changed under it.</summary>
    private bool Collect(CharacterInventory inventory, string? key, List<MovedUnit> units)
    {
        if (key == null)
            return true;
        if (!inventory.Reservations.TryGetValue(key, out var reservation))
            return false;
        foreach (var slot in reservation)
        {
            var stack = inventory.Items.At(slot.At);
            if (stack.ItemNetId != slot.ItemNetId || stack.InstanceId != slot.InstanceId || stack.Count < slot.Count
                || !TryUnit(inventory, stack, slot.Count, out var unit))
                return false;
            units.Add(unit);
        }
        return true;
    }

    private bool TryUnit(CharacterInventory inventory, in ItemStack stack, int count, out MovedUnit unit)
    {
        unit = default;
        if (stack.IsEmpty || stack.Count < count || !_rules.TryGetItem(stack.ItemNetId, out var def))
            return false;
        if (!stack.IsInstance)
        {
            unit = new MovedUnit(def, stack.WithCount(count), false, null);
            return true;
        }
        if (count != 1 || !inventory.Instances.TryGetValue(stack.InstanceId, out var info))
            return false;
        unit = new MovedUnit(def, stack, info.NonTradable, LatestProperties(inventory, stack.InstanceId).Clone());
        return true;
    }

    private static List<ReservedSlot> Reservation(CharacterInventory inventory, string key) => inventory.Reservations[key];

    /// <summary>
    /// True when every unit of <paramref name="incoming"/> fits the receiver's containers after its own <paramref name="outgoing"/>
    /// units left (scratch copies; the live bags are untouched).
    /// </summary>
    private bool Fits(CharacterInventory receiver, List<ReservedSlot>? outgoing, List<MovedUnit> incoming)
    {
        if (incoming.Count == 0)
            return true;
        var scratch = new Dictionary<int, Bag>();
        Bag Scratch(int index)
        {
            if (!scratch.TryGetValue(index, out var bag))
                scratch.Add(index, bag = TradeRules.Copy(receiver.Items.BagAt(index)));
            return bag;
        }
        if (outgoing != null)
        {
            foreach (var slot in outgoing)
            {
                receiver.Items.TryIndex(slot.At.Container, out int index);
                Scratch(index).TryRemove(slot.At.Slot, slot.Count, null);
            }
        }
        foreach (var unit in incoming)
        {
            int index = ReceiverIndex(unit.Def.Category);
            if (index < 0 || Scratch(index).Add(unit.Stack, unit.Stack.IsInstance ? 1 : unit.Def.MaxStack, null) < unit.Stack.Count)
                return false;
        }
        return true;
    }

    /// <summary>Consumes the reservation as one <see cref="AuditKind.TransferOut"/> operation; returns its key.</summary>
    private string TakeOut(CharacterInventory inventory, string reservationKey, string opKey, InventoryReason reason, string source, long peer)
    {
        var reservation = inventory.Reservations[reservationKey];
        Drop(inventory, reservationKey, reservation);
        _transferChanges.Clear();
        int total = 0;
        foreach (var slot in reservation)
        {
            inventory.Items.TryIndex(slot.At.Container, out int index);
            _changes.Clear();
            inventory.Items.BagAt(index).TryRemove(slot.At.Slot, slot.Count, _changes);
            foreach (var change in _changes)
                _transferChanges.Add(new InventorySlotChange(slot.At.Container, change));
            total += slot.Count;
        }
        Record(inventory, opKey, AuditKind.TransferOut, reason, source, total, _transferChanges);
        inventory.TransferPeers[opKey] = peer;
        ReservationsChanged?.Invoke(inventory.CharacterId);
        return opKey;
    }

    /// <summary>Adds the units as one <see cref="AuditKind.TransferIn"/> operation (checked to fit before); returns its key.</summary>
    private string PutIn(CharacterInventory inventory, List<MovedUnit> units, string opKey, InventoryReason reason, string source, long peer,
        int ownerHistoryMax, long previousOwner)
    {
        _transferChanges.Clear();
        int total = 0;
        foreach (var unit in units)
        {
            int index = ReceiverIndex(unit.Def.Category);
            var bag = inventory.Items.BagAt(index);
            byte code = _rules.ContainerRules[index].Code;
            _changes.Clear();
            int added;
            if (unit.Stack.IsInstance)
            {
                var properties = unit.Properties!;
                TradeRules.AppendOwner(properties.OwnerHistory, previousOwner, ownerHistoryMax);
                // The receiver now holds it (an id it held before and gave away is replaced: the latest owner wins).
                inventory.Instances[unit.Stack.InstanceId] = new InstanceInfo(unit.Def.Id, unit.Stack.Rarity, unit.Stack.Stolen,
                    unit.NonTradable, properties);
                inventory.PendingProperties.Remove(unit.Stack.InstanceId);
                added = bag.Add(unit.Stack, 1, _changes);
            }
            else
            {
                added = bag.Add(unit.Stack, unit.Def.MaxStack, _changes);
            }
            if (added != unit.Stack.Count)
                throw new InvalidOperationException($"transfer {opKey}: {unit.Def.Id} did not fit after the check");
            foreach (var change in _changes)
                _transferChanges.Add(new InventorySlotChange(code, change));
            total += added;
        }
        Record(inventory, opKey, AuditKind.TransferIn, reason, source, total, _transferChanges);
        inventory.TransferPeers[opKey] = peer;
        return opKey;
    }

    private string MoveGold(long characterId, long delta, string opKey, InventoryReason reason, string source)
    {
        RecordGold(characterId, Account(characterId), opKey, delta, reason, source, null);
        return opKey;
    }

    /// <summary>
    /// After a commit: <c>ItemTransferred</c> per item of each transfer operation of the batch, once per pair (the receiving side
    /// publishes a move between characters; the giving side publishes only a move into escrow).
    /// </summary>
    private void PublishTransfers(CharacterInventory inventory, InventoryBatch batch)
    {
        if (inventory.TransferPeers.Count == 0)
            return;
        long me = inventory.CharacterId;
        foreach (var op in batch.Ops)
        {
            if (!inventory.TransferPeers.Remove(op.OpKey, out long peer) || _events == null)
                continue;
            var reason = Enum.TryParse<InventoryReason>(op.Reason, out var parsed) ? parsed : InventoryReason.None;
            var moved = new List<(string DefId, int Rarity, long InstanceId, int Count, bool In)>();
            foreach (var audit in batch.Audit)
            {
                if (audit.OpKey != op.OpKey || audit.Kind is not (AuditKind.TransferIn or AuditKind.TransferOut))
                    continue;
                bool isIn = audit.Kind == AuditKind.TransferIn;
                int units = isIn ? Math.Max(0, audit.CountDelta) : Math.Max(0, -audit.CountDelta);
                if (units == 0)
                    continue;
                int at = moved.FindIndex(m => m.DefId == audit.DefId && m.Rarity == audit.Rarity && m.InstanceId == audit.InstanceId);
                if (at < 0)
                    moved.Add((audit.DefId, audit.Rarity, audit.InstanceId, units, isIn));
                else
                    moved[at] = moved[at] with { Count = moved[at].Count + units };
            }
            foreach (var (defId, rarity, instanceId, count, isIn) in moved)
            {
                if (!_rules.TryGetItem(defId, out var item))
                    continue;
                if (isIn)
                    _events.Publish(new ItemTransferred(peer, me, (ushort)item.NetId, (byte)rarity, count, instanceId, reason, op.OpKey), me);
                else if (peer == 0)
                    _events.Publish(new ItemTransferred(me, 0, (ushort)item.NetId, (byte)rarity, count, instanceId, reason, op.OpKey), me);
            }
        }
    }
}
