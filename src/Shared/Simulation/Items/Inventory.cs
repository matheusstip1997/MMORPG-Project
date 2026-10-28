using System;
using System.Collections.Generic;
using Shared.Data.Tables;

namespace Shared.Simulation.Items
{
    /// <summary>A slot of a container: <see cref="Container"/> is <c>ContainerDef.code</c>.</summary>
    public readonly struct InventoryLocation : IEquatable<InventoryLocation>
    {
        public InventoryLocation(byte container, int slot)
        {
            Container = container;
            Slot = slot;
        }

        public byte Container { get; }
        public int Slot { get; }

        public bool Equals(InventoryLocation other) => Container == other.Container && Slot == other.Slot;

        public override bool Equals(object? obj) => obj is InventoryLocation other && Equals(other);

        public override int GetHashCode() => (Container << 16) ^ Slot;

        public override string ToString() => $"{Container}:{Slot}";
    }

    /// <summary>One container's rules (a <c>ContainerDef</c> row in runtime form).</summary>
    public readonly struct ContainerRule
    {
        public ContainerRule(byte code, int slots, IReadOnlyList<ItemCategory> accepts, bool isEquipped)
        {
            Code = code;
            Slots = slots;
            IsEquipped = isEquipped;
            uint mask = 0;
            foreach (var category in accepts)
                mask |= 1u << (int)category;
            AcceptMask = mask;
        }

        public byte Code { get; }
        public int Slots { get; }
        public uint AcceptMask { get; }

        /// <summary>The Equipped container: slot index = <c>EquipSlotDef.code</c>.</summary>
        public bool IsEquipped { get; }

        public bool Accepts(ItemCategory category) => (AcceptMask & (1u << (int)category)) != 0;
    }

    /// <summary>What the inventory rules need of an item definition.</summary>
    public readonly struct ItemRuleInfo
    {
        public ItemRuleInfo(ItemCategory category, int maxStack, ushort equipSlotMask)
        {
            Category = category;
            MaxStack = maxStack;
            EquipSlotMask = equipSlotMask;
        }

        public ItemCategory Category { get; }
        public int MaxStack { get; }

        /// <summary>Bit n set: the item fills equipment slot n when equipped (a dual weapon both weapon slots, R-3.5-07).</summary>
        public ushort EquipSlotMask { get; }

        /// <summary>Where an equipped item sits in the Equipped container: its lowest slot.</summary>
        public int PrimarySlot => LowestBit(EquipSlotMask);

        internal static int LowestBit(int mask)
        {
            for (int i = 0; i < 16; i++)
            {
                if ((mask & (1 << i)) != 0)
                    return i;
            }
            return -1;
        }
    }

    /// <summary>Item definitions by <c>ItemDef.netId</c>, for the rules.</summary>
    public interface IItemRuleLookup
    {
        bool TryGetItem(ushort itemNetId, out ItemRuleInfo info);
    }

    /// <summary>One slot of one container as it was before and after an inventory operation.</summary>
    public readonly struct InventorySlotChange
    {
        public InventorySlotChange(byte container, in SlotChange change)
        {
            Container = container;
            Change = change;
        }

        public byte Container { get; }
        public SlotChange Change { get; }
    }

    /// <summary>Why the rules refused a move (the server maps it to its reason code).</summary>
    public enum InventoryCheck : byte
    {
        Ok = 0,
        /// <summary>Same slot, an unknown container, a slot out of range, or Equipped to Equipped.</summary>
        SlotInvalid,
        SlotEmpty,
        /// <summary>A full stack of the same key.</summary>
        StackFull,
        /// <summary>The target container does not take the category, or the item does not fit that equipment slot.</summary>
        NotAccepted,
        /// <summary>Equipping displaces more items than the source container has free slots for.</summary>
        NoRoom,
    }

    /// <summary>The result of <see cref="Inventory.TryMove"/>: what happened, or why nothing did.</summary>
    public readonly struct InventoryMove
    {
        public InventoryMove(BagMoveResult result, InventoryCheck check)
        {
            Result = result;
            Check = check;
        }

        public BagMoveResult Result { get; }
        public InventoryCheck Check { get; }
        public bool Applied => Result != BagMoveResult.Refused;

        public static InventoryMove Refused(InventoryCheck check) => new InventoryMove(BagMoveResult.Refused, check);
    }

    /// <summary>
    /// A character's containers and the pure rules between them (R-4.1-02, spec 4.1 M2 item model, R-3.5-07): which
    /// categories each container takes, moves inside one container (<see cref="Bag.TryMove"/>), moves between containers,
    /// equipping (a move into the Equipped container: the item sits at its lowest slot and displaces every equipped item
    /// that shares a slot with it; the first goes back where the new one came from, the others to the first free slots of
    /// that container) and unequipping (a move out of it). Shared by <c>InventoryService</c> on the server, the only writer
    /// (architecture rule 4), and the client's preview. Gear rules that need the character (class weapon list, level,
    /// combat) are the server's, on top of these.
    /// </summary>
    public sealed class Inventory
    {
        private readonly ContainerRule[] _rules;
        private readonly Bag[] _bags;
        private readonly List<int> _occupants = new List<int>(4);
        private readonly List<SlotChange> _slotChanges = new List<SlotChange>(2);

        public Inventory(IReadOnlyList<ContainerRule> rules)
        {
            _rules = new ContainerRule[rules.Count];
            _bags = new Bag[rules.Count];
            EquippedIndex = -1;
            for (int i = 0; i < rules.Count; i++)
            {
                _rules[i] = rules[i];
                _bags[i] = new Bag(rules[i].Slots);
                if (rules[i].IsEquipped)
                    EquippedIndex = i;
            }
        }

        public int ContainerCount => _bags.Length;

        /// <summary>Index of the Equipped container, or -1.</summary>
        public int EquippedIndex { get; }

        public ContainerRule Rule(int index) => _rules[index];

        public Bag BagAt(int index) => _bags[index];

        public bool TryIndex(byte code, out int index)
        {
            for (int i = 0; i < _rules.Length; i++)
            {
                if (_rules[i].Code == code)
                {
                    index = i;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        /// <summary>The stack at a location; empty for an unknown container or slot.</summary>
        public ItemStack At(InventoryLocation location) =>
            TryIndex(location.Container, out int index) && location.Slot >= 0 && location.Slot < _bags[index].SlotCount
                ? _bags[index][location.Slot]
                : ItemStack.Empty;

        /// <summary>
        /// The equipped item that fills equipment slot <paramref name="slot"/> (it sits there, or a dual weapon in the other
        /// weapon slot covers it), or -1.
        /// </summary>
        public int CoveredBy(int slot, IItemRuleLookup items)
        {
            if (EquippedIndex < 0)
                return -1;
            var equipped = _bags[EquippedIndex];
            for (int i = 0; i < equipped.SlotCount; i++)
            {
                if (!equipped[i].IsEmpty && items.TryGetItem(equipped[i].ItemNetId, out var info)
                    && (info.EquipSlotMask & (1 << slot)) != 0)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// The stack a move would put into the Equipped container (what the server's gear rules must allow), or empty when
        /// it equips nothing. Checks nothing else.
        /// </summary>
        public ItemStack IncomingEquip(InventoryLocation from, InventoryLocation to)
        {
            if (!TryIndex(from.Container, out int fromIndex) || !TryIndex(to.Container, out int toIndex))
                return ItemStack.Empty;
            if (toIndex == EquippedIndex && fromIndex != EquippedIndex)
                return At(from);
            if (fromIndex == EquippedIndex && toIndex != EquippedIndex)
                return At(to); // a swap equips what lies in the target slot
            return ItemStack.Empty;
        }

        /// <summary>
        /// Puts a new item into an empty slot (a grant: the caller checked what may go there). False, nothing changed, when
        /// the location is unknown or taken.
        /// </summary>
        public bool TryPlace(InventoryLocation at, in ItemStack stack, List<InventorySlotChange>? changes)
        {
            if (stack.IsEmpty || !TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= _bags[index].SlotCount
                || !_bags[index][at.Slot].IsEmpty)
                return false;
            Set(index, at.Slot, stack, changes);
            return true;
        }

        /// <summary>
        /// Applies a move of <paramref name="count"/> units if the rules allow it (WP-M2-11, spec 9.13 stack splitting): 0 or
        /// the whole stack is the plain move. Fewer units split the stack: they go into an empty slot as a new stack
        /// (<see cref="BagMoveResult.Moved"/>) or onto the same stack key when all of them fit (<see cref="BagMoveResult.Merged"/>),
        /// and the rest stays. Refused: more units than the stack holds or a different item there
        /// (<see cref="InventoryCheck.SlotInvalid"/>), a same-key stack without room for all of them
        /// (<see cref="InventoryCheck.StackFull"/>), a container that does not take the category
        /// (<see cref="InventoryCheck.NotAccepted"/>), and the Equipped container (gear is one instance per stack).
        /// </summary>
        public InventoryMove TryMove(InventoryLocation from, InventoryLocation to, int count, IItemRuleLookup items,
            List<InventorySlotChange>? changes)
        {
            if (count < 0)
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            if (count == 0 || !TryIndex(from.Container, out int fromIndex) || from.Slot < 0 || from.Slot >= _bags[fromIndex].SlotCount
                || count == _bags[fromIndex][from.Slot].Count)
                return TryMove(from, to, items, changes);
            if (!TryIndex(to.Container, out int toIndex) || to.Slot < 0 || to.Slot >= _bags[toIndex].SlotCount || from.Equals(to)
                || fromIndex == EquippedIndex || toIndex == EquippedIndex)
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            var source = _bags[fromIndex][from.Slot];
            if (source.IsEmpty)
                return InventoryMove.Refused(InventoryCheck.SlotEmpty);
            if (count > source.Count || !items.TryGetItem(source.ItemNetId, out var sourceInfo))
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            if (fromIndex != toIndex && !_rules[toIndex].Accepts(sourceInfo.Category))
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            var target = _bags[toIndex][to.Slot];
            if (!target.IsEmpty && !target.SameKey(source))
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            if (!target.IsEmpty && target.Count + count > sourceInfo.MaxStack)
                return InventoryMove.Refused(InventoryCheck.StackFull);
            Set(toIndex, to.Slot, target.IsEmpty ? source.WithCount(count) : target.WithCount(target.Count + count), changes);
            Set(fromIndex, from.Slot, source.WithCount(source.Count - count), changes);
            return new InventoryMove(target.IsEmpty ? BagMoveResult.Moved : BagMoveResult.Merged, InventoryCheck.Ok);
        }

        /// <summary>Applies a move if the rules allow it; each touched slot goes to <paramref name="changes"/>.</summary>
        public InventoryMove TryMove(InventoryLocation from, InventoryLocation to, IItemRuleLookup items,
            List<InventorySlotChange>? changes)
        {
            if (!TryIndex(from.Container, out int fromIndex) || !TryIndex(to.Container, out int toIndex)
                || from.Slot < 0 || from.Slot >= _bags[fromIndex].SlotCount || to.Slot < 0 || to.Slot >= _bags[toIndex].SlotCount
                || from.Equals(to))
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            var source = _bags[fromIndex][from.Slot];
            if (source.IsEmpty)
                return InventoryMove.Refused(InventoryCheck.SlotEmpty);
            if (!items.TryGetItem(source.ItemNetId, out var sourceInfo))
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);

            bool fromEquipped = fromIndex == EquippedIndex, toEquipped = toIndex == EquippedIndex;
            if (fromEquipped && toEquipped)
                return InventoryMove.Refused(InventoryCheck.SlotInvalid);
            if (toEquipped)
                return Equip(fromIndex, from.Slot, to.Slot, source, sourceInfo, items, changes, -1);
            if (fromEquipped)
                return Unequip(from.Slot, toIndex, to.Slot, source, sourceInfo, items, changes);
            if (fromIndex == toIndex)
                return SameBag(fromIndex, from.Slot, to.Slot, sourceInfo.MaxStack, changes);
            return BetweenBags(fromIndex, from.Slot, toIndex, to.Slot, source, sourceInfo, items, changes);
        }

        private InventoryMove SameBag(int index, int from, int to, int maxStack, List<InventorySlotChange>? changes)
        {
            var bag = _bags[index];
            bool sameKey = bag[to].SameKey(bag[from]);
            _slotChanges.Clear();
            var result = bag.TryMove(from, to, maxStack, _slotChanges);
            if (result == BagMoveResult.Refused)
                return InventoryMove.Refused(sameKey ? InventoryCheck.StackFull : InventoryCheck.SlotInvalid);
            if (changes != null)
            {
                foreach (var change in _slotChanges)
                    changes.Add(new InventorySlotChange(_rules[index].Code, change));
            }
            return new InventoryMove(result, InventoryCheck.Ok);
        }

        private InventoryMove BetweenBags(int fromIndex, int from, int toIndex, int to, in ItemStack source, in ItemRuleInfo sourceInfo,
            IItemRuleLookup items, List<InventorySlotChange>? changes)
        {
            if (!_rules[toIndex].Accepts(sourceInfo.Category))
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            var target = _bags[toIndex][to];
            if (target.IsEmpty)
            {
                Set(toIndex, to, source, changes);
                Set(fromIndex, from, ItemStack.Empty, changes);
                return new InventoryMove(BagMoveResult.Moved, InventoryCheck.Ok);
            }
            if (target.SameKey(source))
            {
                int moved = Math.Min(source.Count, sourceInfo.MaxStack - target.Count);
                if (moved <= 0)
                    return InventoryMove.Refused(InventoryCheck.StackFull);
                Set(toIndex, to, target.WithCount(target.Count + moved), changes);
                Set(fromIndex, from, source.WithCount(source.Count - moved), changes);
                return new InventoryMove(BagMoveResult.Merged, InventoryCheck.Ok);
            }
            // A swap: the source container must take what comes back.
            if (!items.TryGetItem(target.ItemNetId, out var targetInfo) || !_rules[fromIndex].Accepts(targetInfo.Category))
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            Set(toIndex, to, source, changes);
            Set(fromIndex, from, target, changes);
            return new InventoryMove(BagMoveResult.Swapped, InventoryCheck.Ok);
        }

        /// <param name="firstOut">An equipped slot whose item, if displaced, goes back to the source slot first (an unequip swap).</param>
        private InventoryMove Equip(int fromIndex, int from, int equipSlot, in ItemStack source, in ItemRuleInfo sourceInfo,
            IItemRuleLookup items, List<InventorySlotChange>? changes, int firstOut)
        {
            if (sourceInfo.Category != ItemCategory.Equipment || (sourceInfo.EquipSlotMask & (1 << equipSlot)) == 0)
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            var equipped = _bags[EquippedIndex];
            int primary = sourceInfo.PrimarySlot;
            if (primary < 0 || primary >= equipped.SlotCount)
                return InventoryMove.Refused(InventoryCheck.NotAccepted);

            // Every equipped item sharing a slot with the new one leaves (slot order).
            _occupants.Clear();
            for (int i = 0; i < equipped.SlotCount; i++)
            {
                if (!equipped[i].IsEmpty && items.TryGetItem(equipped[i].ItemNetId, out var info)
                    && (info.EquipSlotMask & sourceInfo.EquipSlotMask) != 0)
                    _occupants.Add(i);
            }
            int preferred = _occupants.IndexOf(firstOut);
            if (preferred > 0)
            {
                _occupants.RemoveAt(preferred);
                _occupants.Insert(0, firstOut);
            }
            var bag = _bags[fromIndex];
            int free = 0;
            for (int i = 0; i < bag.SlotCount; i++)
            {
                if (bag[i].IsEmpty)
                    free++;
            }
            if (_occupants.Count > 1 + free) // the first takes the source slot
                return InventoryMove.Refused(InventoryCheck.NoRoom);
            foreach (int slot in _occupants)
            {
                if (!items.TryGetItem(equipped[slot].ItemNetId, out var info) || !_rules[fromIndex].Accepts(info.Category))
                    return InventoryMove.Refused(InventoryCheck.NotAccepted);
            }

            Set(fromIndex, from, ItemStack.Empty, changes);
            int nextFree = 0;
            for (int n = 0; n < _occupants.Count; n++)
            {
                var occupant = equipped[_occupants[n]];
                Set(EquippedIndex, _occupants[n], ItemStack.Empty, changes);
                int target = n == 0 ? from : bag.FirstEmpty(nextFree);
                if (n > 0)
                    nextFree = target + 1;
                Set(fromIndex, target, occupant, changes);
            }
            Set(EquippedIndex, primary, source, changes);
            return new InventoryMove(_occupants.Count > 0 ? BagMoveResult.Swapped : BagMoveResult.Moved, InventoryCheck.Ok);
        }

        private InventoryMove Unequip(int equipSlot, int toIndex, int to, in ItemStack source, in ItemRuleInfo sourceInfo,
            IItemRuleLookup items, List<InventorySlotChange>? changes)
        {
            if (!_rules[toIndex].Accepts(sourceInfo.Category))
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            var target = _bags[toIndex][to];
            if (target.IsEmpty)
            {
                Set(toIndex, to, source, changes);
                Set(EquippedIndex, equipSlot, ItemStack.Empty, changes);
                return new InventoryMove(BagMoveResult.Moved, InventoryCheck.Ok);
            }
            // Onto gear that fits the slot being emptied: that gear is equipped in its place (the unequipped item leaves
            // as one of the displaced, into the target slot).
            if (!items.TryGetItem(target.ItemNetId, out var targetInfo) || targetInfo.Category != ItemCategory.Equipment
                || (targetInfo.EquipSlotMask & sourceInfo.EquipSlotMask) == 0)
                return InventoryMove.Refused(InventoryCheck.NotAccepted);
            int fitting = (targetInfo.EquipSlotMask & (1 << equipSlot)) != 0 ? equipSlot : targetInfo.PrimarySlot;
            return Equip(toIndex, to, fitting, target, targetInfo, items, changes, equipSlot);
        }

        private void Set(int index, int slot, in ItemStack stack, List<InventorySlotChange>? changes)
        {
            if (changes == null)
            {
                _bags[index].Set(slot, stack, null);
                return;
            }
            var before = _bags[index][slot];
            _bags[index].Set(slot, stack, null);
            changes.Add(new InventorySlotChange(_rules[index].Code, new SlotChange(slot, before, stack)));
        }
    }
}
