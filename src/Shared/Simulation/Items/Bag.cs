using System;
using System.Collections.Generic;

namespace Shared.Simulation.Items
{
    /// <summary>
    /// One stack in a bag slot (spec 4.1 implementation: stackables are <c>(defId, rarity, stolenFlag) x count</c>).
    /// <see cref="ItemNetId"/> is <c>ItemDef.netId</c>; a slot with <see cref="Count"/> 0 is empty.
    /// </summary>
    public readonly struct ItemStack : IEquatable<ItemStack>
    {
        public static readonly ItemStack Empty = default;

        public ItemStack(ushort itemNetId, byte rarity, bool stolen, int count)
        {
            ItemNetId = itemNetId;
            Rarity = rarity;
            Stolen = stolen;
            Count = count;
        }

        public ushort ItemNetId { get; }
        public byte Rarity { get; }
        public bool Stolen { get; }
        public int Count { get; }

        public bool IsEmpty => Count == 0;

        /// <summary>Same stack key: these two may share a slot.</summary>
        public bool SameKey(in ItemStack other) => ItemNetId == other.ItemNetId && Rarity == other.Rarity && Stolen == other.Stolen;

        public ItemStack WithCount(int count) => count == 0 ? Empty : new ItemStack(ItemNetId, Rarity, Stolen, count);

        public bool Equals(ItemStack other) => (IsEmpty && other.IsEmpty) || (SameKey(other) && Count == other.Count);

        public override bool Equals(object? obj) => obj is ItemStack other && Equals(other);

        public override int GetHashCode() => IsEmpty ? 0 : (ItemNetId * 397) ^ (Rarity * 31) ^ (Stolen ? 1 : 0) ^ (Count << 8);

        public override string ToString() => IsEmpty ? "empty" : $"{ItemNetId}/{Rarity}{(Stolen ? "/stolen" : "")} x{Count}";
    }

    /// <summary>One slot as it was before and after a bag operation.</summary>
    public readonly struct SlotChange
    {
        public SlotChange(int slot, ItemStack before, ItemStack after)
        {
            Slot = slot;
            Before = before;
            After = after;
        }

        public int Slot { get; }
        public ItemStack Before { get; }
        public ItemStack After { get; }
    }

    public enum BagMoveResult : byte
    {
        /// <summary>Into an empty slot.</summary>
        Moved,
        /// <summary>Into a stack with the same key; what does not fit stays in the source slot.</summary>
        Merged,
        /// <summary>Two different stacks traded places.</summary>
        Swapped,
        /// <summary>Same slot, an empty source, a slot out of range, or a full stack of the same key: nothing changed.</summary>
        Refused,
    }

    /// <summary>
    /// The slots of one container and the pure stacking rules (backlog G, R-4.1-02). Deterministic: adds fill stacks of
    /// the same key in slot order, then empty slots in slot order. Holds no ids or persistence; <c>InventoryService</c>
    /// on the server is the only code that changes a character's bag (architecture rule 4), through this class.
    /// </summary>
    public sealed class Bag
    {
        private readonly ItemStack[] _slots;

        public Bag(int slotCount)
        {
            if (slotCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(slotCount));
            _slots = new ItemStack[slotCount];
        }

        public int SlotCount => _slots.Length;

        public ItemStack this[int slot] => _slots[slot];

        /// <summary>Puts a stack into a slot as loaded from the database (no rules applied).</summary>
        public void Load(int slot, in ItemStack stack) => _slots[slot] = stack;

        public void CopyTo(ItemStack[] target) => Array.Copy(_slots, target, _slots.Length);

        /// <summary>Units of <paramref name="item"/> (its count ignored) that would fit.</summary>
        public int SpaceFor(in ItemStack item, int maxStack)
        {
            int space = 0;
            foreach (var slot in _slots)
            {
                if (slot.IsEmpty)
                    space += maxStack;
                else if (slot.SameKey(item) && slot.Count < maxStack)
                    space += maxStack - slot.Count;
            }
            return space;
        }

        /// <summary>
        /// Adds up to <paramref name="item"/>.Count units and returns how many went in (take what fits, R-4.3-02 M1).
        /// Each touched slot is appended to <paramref name="changes"/>.
        /// </summary>
        public int Add(in ItemStack item, int maxStack, List<SlotChange>? changes)
        {
            if (maxStack <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxStack));
            int left = item.Count;
            for (int i = 0; i < _slots.Length && left > 0; i++)
            {
                var slot = _slots[i];
                if (!slot.IsEmpty && slot.SameKey(item) && slot.Count < maxStack)
                    left -= Put(i, slot.WithCount(slot.Count + Math.Min(left, maxStack - slot.Count)), changes, slot);
            }
            for (int i = 0; i < _slots.Length && left > 0; i++)
            {
                if (_slots[i].IsEmpty)
                    left -= Put(i, item.WithCount(Math.Min(left, maxStack)), changes, ItemStack.Empty);
            }
            return item.Count - left;
        }

        /// <summary>Takes <paramref name="count"/> units out of one slot; false (nothing changed) if it holds fewer.</summary>
        public bool TryRemove(int slot, int count, List<SlotChange>? changes)
        {
            if (slot < 0 || slot >= _slots.Length || count <= 0)
                return false;
            var stack = _slots[slot];
            if (stack.Count < count)
                return false;
            Put(slot, stack.WithCount(stack.Count - count), changes, stack);
            return true;
        }

        /// <summary>Moves the stack in <paramref name="from"/> onto <paramref name="to"/>: move, merge or swap.</summary>
        public BagMoveResult TryMove(int from, int to, int maxStack, List<SlotChange>? changes)
        {
            if (from == to || from < 0 || to < 0 || from >= _slots.Length || to >= _slots.Length)
                return BagMoveResult.Refused;
            var source = _slots[from];
            var target = _slots[to];
            if (source.IsEmpty)
                return BagMoveResult.Refused;
            if (target.IsEmpty)
            {
                Put(to, source, changes, target);
                Put(from, ItemStack.Empty, changes, source);
                return BagMoveResult.Moved;
            }
            if (target.SameKey(source))
            {
                int moved = Math.Min(source.Count, maxStack - target.Count);
                if (moved <= 0)
                    return BagMoveResult.Refused;
                Put(to, target.WithCount(target.Count + moved), changes, target);
                Put(from, source.WithCount(source.Count - moved), changes, source);
                return BagMoveResult.Merged;
            }
            Put(to, source, changes, target);
            Put(from, target, changes, source);
            return BagMoveResult.Swapped;
        }

        /// <summary>Total units of one stack key in the bag.</summary>
        public int CountOf(in ItemStack key)
        {
            int total = 0;
            foreach (var slot in _slots)
            {
                if (!slot.IsEmpty && slot.SameKey(key))
                    total += slot.Count;
            }
            return total;
        }

        private int Put(int slot, in ItemStack after, List<SlotChange>? changes, in ItemStack before)
        {
            _slots[slot] = after;
            changes?.Add(new SlotChange(slot, before, after));
            return after.Count - (before.SameKey(after) ? before.Count : 0);
        }
    }
}
