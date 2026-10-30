#nullable enable
using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace Game.UI.ViewModels.Inventory
{
    /// <summary>One slot as the window shows it: the confirmed stack, or the preview of a move not yet confirmed.</summary>
    public readonly struct InventorySlotView : IEquatable<InventorySlotView>
    {
        public InventorySlotView(in ItemStack stack, bool preview, bool covered = false)
        {
            ItemNetId = stack.ItemNetId;
            Rarity = stack.Rarity;
            Stolen = stack.Stolen;
            Count = stack.Count;
            InstanceId = stack.InstanceId;
            Preview = preview;
            Covered = covered;
        }

        /// <summary><c>ItemDef.netId</c>; 0 for an empty slot.</summary>
        public ushort ItemNetId { get; }

        /// <summary><c>RarityDef.tier</c>.</summary>
        public byte Rarity { get; }

        public bool Stolen { get; }

        public int Count { get; }

        /// <summary>The item instance's id; 0 for a stackable (WP-M2-1).</summary>
        public long InstanceId { get; }

        /// <summary>A move the server has not confirmed yet changed this slot.</summary>
        public bool Preview { get; }

        /// <summary>An empty equipment slot an item in another slot fills (a dual weapon's second slot, R-3.5-07).</summary>
        public bool Covered { get; }

        public bool IsEmpty => Count == 0;

        public bool Equals(InventorySlotView other) =>
            ItemNetId == other.ItemNetId && Rarity == other.Rarity && Stolen == other.Stolen && Count == other.Count
            && InstanceId == other.InstanceId && Preview == other.Preview && Covered == other.Covered;

        public override bool Equals(object? obj) => obj is InventorySlotView other && Equals(other);

        public override int GetHashCode() => (ItemNetId * 397) ^ (Count << 8) ^ (Preview ? 1 : 0) ^ Rarity ^ InstanceId.GetHashCode();
    }

    /// <summary>A move the client sent and shows as a preview until the server answers.</summary>
    public readonly struct PendingMove
    {
        public PendingMove(uint requestId, in MoveIntent move, double sentAtMs, bool confirmed)
        {
            RequestId = requestId;
            Move = move;
            SentAtMs = sentAtMs;
            Confirmed = confirmed;
        }

        public uint RequestId { get; }
        public MoveIntent Move { get; }
        public byte FromSlot => Move.FromSlot;
        public byte ToSlot => Move.ToSlot;
        public double SentAtMs { get; }

        /// <summary>The server accepted it; the contents that show it come next (the server sends them right after).</summary>
        public bool Confirmed { get; }

        public PendingMove AsConfirmed() => new PendingMove(RequestId, Move, SentAtMs, true);
    }

    /// <summary>
    /// A move the window wants sent: from one container slot onto another (move, merge, swap, equip or unequip is the
    /// server's call). Container 0 is the Normal bag (the M1 move, as on the wire).
    /// </summary>
    public readonly struct MoveIntent
    {
        public MoveIntent(byte fromSlot, byte toSlot) : this(0, fromSlot, 0, toSlot)
        {
        }

        public MoveIntent(byte fromContainer, byte fromSlot, byte toContainer, byte toSlot, ushort count = 0)
        {
            FromContainer = fromContainer;
            FromSlot = fromSlot;
            ToContainer = toContainer;
            ToSlot = toSlot;
            Count = count;
        }

        /// <summary><c>ContainerDef.code</c>; 0 for the Normal bag.</summary>
        public byte FromContainer { get; }
        public byte FromSlot { get; }
        public byte ToContainer { get; }
        public byte ToSlot { get; }

        /// <summary>Units of a split (WP-M2-11, protocol 24); 0 moves the whole stack.</summary>
        public ushort Count { get; }
    }

    /// <summary>
    /// The inventory window (spec 9.13; backlog G client part, WP16; containers and equipment WP-M2-1): the containers as the
    /// server last confirmed them (<see cref="InventoryContents"/>, as committed, rule 5), with the moves the player made and
    /// the server has not answered yet shown on top as a preview. The grid shows one bag container at a time (the tabs, in
    /// code order: Normal bag, Safe bag areas, Quest bag); the equipment strip shows the Equipped container. Slots are
    /// numbered flat for the presenter: <c>0..SlotCount-1</c> the active tab's grid, then <see cref="EquipSlotCount"/>
    /// equipment slots. The selection keeps its container across tab switches, so click, tab, click moves between
    /// containers. The preview runs the shared <see cref="Inventory"/> rules (the same code the server applies; the gear
    /// rules that need the character, class and level, and the combat rule are the server's), but it is never the truth: a
    /// refusal drops the move and the slots show the confirmed state again (revert); an accepted move stays until the
    /// contents that include it arrive; a move with no answer at all is dropped after
    /// <c>InventoryWindowSettings.movePreviewTimeoutMs</c>. Raises <see cref="ViewModel.Changed"/> only on a real change.
    /// </summary>
    public sealed class InventoryWindowViewModel : ViewModel
    {
        public const int None = -1;

        private readonly IReadOnlyList<ContainerRule> _rules;
        private readonly IItemRuleLookup _items;
        private readonly byte _normalCode;
        private readonly int _equippedIndex = None;
        private readonly List<byte> _tabs = new List<byte>();
        private readonly List<PendingMove> _pending = new List<PendingMove>();
        private readonly List<InventorySlotChange> _changes = new List<InventorySlotChange>();
        private InventoryContents? _confirmed;
        private InventorySlotView[][] _containers;
        private InventoryLocation? _selected;

        /// <summary>The M1 window: the Normal bag only (any category), stack sizes from <paramref name="maxStack"/>.</summary>
        /// <param name="slotCount">The bag's slots before the server says (<c>ContainerDef.slots</c> of the Normal bag).</param>
        /// <param name="columns">Slots per grid row (<c>InventoryWindowSettings.columns</c>).</param>
        /// <param name="maxStack">Most units one stack of an item holds (<c>ItemDef.maxStack</c> by net id), for merge previews.</param>
        public InventoryWindowViewModel(int slotCount, int columns, Func<ushort, int> maxStack)
            : this(new[] { new ContainerRule(1, slotCount, AllCategories(), false) }, 1, columns, new StackLookup(maxStack))
        {
        }

        /// <param name="containers">Every container in code order (<c>ContainerDef</c> rows).</param>
        /// <param name="normalCode">The Normal bag's code: the tab shown first.</param>
        /// <param name="columns">Slots per grid row (<c>InventoryWindowSettings.columns</c>).</param>
        /// <param name="items">Category, stack size and equipment slots by net id, for the preview.</param>
        public InventoryWindowViewModel(IReadOnlyList<ContainerRule> containers, byte normalCode, int columns, IItemRuleLookup items)
        {
            if (columns <= 0)
                throw new ArgumentOutOfRangeException(nameof(columns));
            _rules = containers;
            _items = items;
            _normalCode = normalCode;
            Columns = columns;
            _containers = new InventorySlotView[containers.Count][];
            for (int i = 0; i < containers.Count; i++)
            {
                if (containers[i].Slots <= 0)
                    throw new ArgumentOutOfRangeException(nameof(containers));
                _containers[i] = new InventorySlotView[containers[i].Slots];
                if (containers[i].IsEquipped)
                    _equippedIndex = i;
                else
                    _tabs.Add(containers[i].Code);
            }
            ActiveContainer = normalCode;
        }

        public bool Open { get; private set; }

        public int Columns { get; }

        /// <summary>The bag containers the window has tabs for (codes), in code order.</summary>
        public IReadOnlyList<byte> Tabs => _tabs;

        /// <summary>The bag container shown in the grid.</summary>
        public byte ActiveContainer { get; private set; }

        /// <summary>The Equipped container's code, or 0 without one.</summary>
        public byte EquippedContainer => _equippedIndex >= 0 ? _rules[_equippedIndex].Code : (byte)0;

        /// <summary>Slots of the grid (the active tab).</summary>
        public int SlotCount => Grid.Length;

        public int Rows => (SlotCount + Columns - 1) / Columns;

        /// <summary>Slots of the equipment strip (<c>EquipSlotDef</c> rows), after the grid in the flat numbering.</summary>
        public int EquipSlotCount => _equippedIndex >= 0 ? _containers[_equippedIndex].Length : 0;

        /// <summary>The server sent contents on this connection.</summary>
        public bool HasContents => _confirmed != null;

        /// <summary>The flat slot picked for click-to-move, or <see cref="None"/> (also when it is on another tab).</summary>
        public int Selected => _selected is { } at ? FlatOf(at) : None;

        /// <summary>The slot under the pointer (tooltip), or <see cref="None"/>.</summary>
        public int Hovered { get; private set; } = None;

        /// <summary>
        /// Units the next click moves off the selected stack (WP-M2-11 stack splitting, spec 9.13): 0 when the selection moves
        /// the whole stack; 1 to the stack's count less one while a split is set up.
        /// </summary>
        public int SplitCount { get; private set; }

        /// <summary>The units of the stack being split (the upper bound of <see cref="SplitCount"/> plus one), 0 without a split.</summary>
        public int SplitStack => SplitCount > 0 && _selected is { } at && IndexOfKnown(at.Container) is int index && index >= 0
            && at.Slot < _containers[index].Length ? _containers[index][at.Slot].Count : 0;

        public IReadOnlyList<PendingMove> Pending => _pending;

        /// <summary>A grid slot (<c>0..SlotCount-1</c>) or an equipment slot (<c>SlotCount + EquipSlotDef.code</c>).</summary>
        public InventorySlotView Slot(int slot) => slot < SlotCount ? Grid[slot] : _containers[_equippedIndex][slot - SlotCount];

        /// <summary>
        /// The stack in a flat slot is bound to the character (non-tradable, R-4.1-01/04; WP-M2-11 tooltip), as the confirmed
        /// contents say for that slot and item; false for an empty slot or a preview the server has not confirmed.
        /// </summary>
        public bool IsBound(int slot) => Entry(slot)?.NonTradable == true;

        /// <summary>
        /// Units in a flat slot are reserved by a craft (spec 4.1 reservations, WP-M3-2): the window dims it and the server
        /// refuses moving, selling or using them. As the confirmed contents say for that slot and item.
        /// </summary>
        public bool IsReserved(int slot) => Entry(slot)?.Reserved == true;

        /// <summary>
        /// The confirmed contents' entry for the stack in a flat slot (its flags and an instance's typed properties, protocol
        /// 27), or null for an empty slot, a preview the server has not confirmed, or another item there.
        /// </summary>
        public InventorySlotEntry? Entry(int slot)
        {
            if (_confirmed == null || slot < 0 || slot >= TotalSlots || Slot(slot).IsEmpty)
                return null;
            var at = LocationOf(slot);
            ushort item = Slot(slot).ItemNetId;
            var entries = at.Container == _normalCode ? _confirmed.Slots : null;
            if (entries == null)
            {
                foreach (var container in _confirmed.Containers)
                {
                    if (container.Code == at.Container)
                        entries = container.Slots;
                }
            }
            if (entries == null)
                return null;
            foreach (var entry in entries)
            {
                if (entry.Slot == at.Slot && entry.ItemNetId == item)
                    return entry;
            }
            return null;
        }

        /// <summary>Where a flat slot is: its container code and slot.</summary>
        public InventoryLocation LocationOf(int slot) => slot < SlotCount
            ? new InventoryLocation(ActiveContainer, slot)
            : new InventoryLocation(EquippedContainer, slot - SlotCount);

        private InventorySlotView[] Grid => _containers[IndexOf(ActiveContainer)];

        private int TotalSlots => SlotCount + EquipSlotCount;

        public void Toggle() => SetOpen(!Open);

        public void SetOpen(bool open)
        {
            if (open == Open)
                return;
            Open = open;
            if (!open)
            {
                _selected = null;
                SplitCount = 0;
                Hovered = None;
            }
            NotifyChanged();
        }

        /// <summary>
        /// Shift+click (WP-M2-11): selects a stack of at least two units for a split of half of it (rounded down); the next
        /// click on an empty slot or a stack of the same item sends that many. A single item, gear (an instance) or an empty
        /// slot does nothing.
        /// </summary>
        public void BeginSplit(int slot)
        {
            if (slot < 0 || slot >= TotalSlots)
                return;
            var view = Slot(slot);
            if (view.Count < 2 || view.InstanceId != 0)
                return;
            _selected = LocationOf(slot);
            SplitCount = view.Count / 2;
            NotifyChanged();
        }

        /// <summary>The split bar's minus and plus: between 1 and the stack's units less one.</summary>
        public void AdjustSplit(int delta)
        {
            int stack = SplitStack;
            if (stack < 2)
                return;
            int count = Math.Max(1, Math.Min(stack - 1, SplitCount + delta));
            if (count == SplitCount)
                return;
            SplitCount = count;
            NotifyChanged();
        }

        /// <summary>Drops the selection and any split set up on it.</summary>
        public void CancelSelection()
        {
            if (_selected == null && SplitCount == 0)
                return;
            _selected = null;
            SplitCount = 0;
            NotifyChanged();
        }

        /// <summary>Shows another bag container in the grid; the selection stays where it is.</summary>
        public void SetActiveContainer(byte code)
        {
            if (code == ActiveContainer || !_tabs.Contains(code))
                return;
            ActiveContainer = code;
            Hovered = None;
            NotifyChanged();
        }

        /// <summary>The committed containers on the server: the new base of the preview; accepted moves are in it now.</summary>
        public void SetContents(InventoryContents contents)
        {
            _confirmed = contents;
            _pending.RemoveAll(move => move.Confirmed);
            Rebuild(forceNotify: false);
        }

        public void SetHovered(int slot)
        {
            if (slot < 0 || slot >= TotalSlots)
                slot = None;
            if (slot == Hovered)
                return;
            Hovered = slot;
            NotifyChanged();
        }

        /// <summary>
        /// Click-to-move: the first click on a stack selects it, a second click on it deselects it, and a click on another
        /// slot (of any tab or the equipment strip) asks to move the selection there. Clicking an empty slot with nothing
        /// selected does nothing.
        /// </summary>
        public MoveIntent? Click(int slot)
        {
            if (slot < 0 || slot >= TotalSlots)
                return null;
            var at = LocationOf(slot);
            if (_selected == null)
            {
                if (Slot(slot).IsEmpty)
                    return null;
                _selected = at;
                NotifyChanged();
                return null;
            }
            var from = _selected.Value;
            int split = SplitCount;
            _selected = null;
            SplitCount = 0;
            NotifyChanged();
            return from.Equals(at) ? (MoveIntent?)null : Move(from, at, split);
        }

        /// <summary>A stack dragged from <paramref name="from"/> and released on <paramref name="to"/> (flat slots).</summary>
        public MoveIntent? Drop(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= TotalSlots || to >= TotalSlots || Slot(from).IsEmpty)
                return null;
            if (_selected != null || SplitCount != 0)
            {
                _selected = null;
                SplitCount = 0; // a drag moves the whole stack
                NotifyChanged();
            }
            return Move(LocationOf(from), LocationOf(to));
        }

        /// <summary>
        /// A stack dragged out of the window: dropping items on the ground is not in M1 (loot bags, M4), so nothing is sent
        /// and the reason code for the toast comes back.
        /// </summary>
        public string DropOutside(int from)
        {
            if (_selected != null || SplitCount != 0)
            {
                _selected = null;
                SplitCount = 0;
                NotifyChanged();
            }
            return ClientReasonCode.ItemDropUnavailable.ToString();
        }

        /// <summary>
        /// Right-click or double-click on a stack of the grid (spec 3.4 consumable use, 9.13; WP-M3-3): the slot to use and
        /// its item, so the binder can ask the server; drops any selection or split. False for an empty slot, a slot whose
        /// move is not answered yet (the bag the server holds may differ) and the equipment strip.
        /// </summary>
        public bool TryUse(int slot, out InventoryLocation at, out ushort itemNetId)
        {
            at = default;
            itemNetId = 0;
            if (_selected != null || SplitCount != 0)
            {
                _selected = null;
                SplitCount = 0;
                NotifyChanged();
            }
            if (slot < 0 || slot >= SlotCount)
                return false;
            var view = Slot(slot);
            if (view.IsEmpty || view.Preview)
                return false;
            at = LocationOf(slot);
            itemNetId = view.ItemNetId;
            return true;
        }

        /// <summary>The move <paramref name="requestId"/> went out: show it until the server answers.</summary>
        public void AddPending(uint requestId, in MoveIntent move, double nowMs)
        {
            _pending.Add(new PendingMove(requestId, move, nowMs, confirmed: false));
            Rebuild(forceNotify: false);
        }

        /// <summary>
        /// The server's answer. Accepted: the move stays shown until the contents that include it (sent right after).
        /// Refused: the preview drops it and the reason code for the toast comes back. An answer to no pending move (a
        /// timed-out one, or another window) only returns its reason.
        /// </summary>
        public string? OnMoveResult(InventoryMoveResult result)
        {
            int index = _pending.FindIndex(move => move.RequestId == result.RequestId && !move.Confirmed);
            if (result.Reason == InventoryMoveReason.None)
            {
                if (index >= 0)
                    _pending[index] = _pending[index].AsConfirmed();
                return null;
            }
            if (index >= 0)
            {
                _pending.RemoveAt(index);
                Rebuild(forceNotify: false);
            }
            return result.Reason.ToString();
        }

        /// <summary>Drops moves with no answer after <paramref name="timeoutMs"/>; the slots show the confirmed state again.</summary>
        public void Expire(double nowMs, double timeoutMs)
        {
            if (_pending.RemoveAll(move => nowMs - move.SentAtMs >= timeoutMs) > 0)
                Rebuild(forceNotify: false);
        }

        /// <summary>The connection ended: nothing shown is current any more.</summary>
        public void Clear()
        {
            _confirmed = null;
            _pending.Clear();
            _selected = null;
            SplitCount = 0;
            Hovered = None;
            Rebuild(forceNotify: true);
        }

        private MoveIntent Move(InventoryLocation from, InventoryLocation to, int count = 0) =>
            new MoveIntent(from.Container, (byte)from.Slot, to.Container, (byte)to.Slot, (ushort)count);

        private int FlatOf(InventoryLocation at)
        {
            if (at.Container == ActiveContainer)
                return at.Slot;
            if (_equippedIndex >= 0 && at.Container == EquippedContainer)
                return SlotCount + at.Slot;
            return None;
        }

        private int IndexOf(byte code)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Code == code)
                    return i;
            }
            return 0;
        }

        private byte CodeOf(byte container) => container == 0 ? _normalCode : container;

        private void Rebuild(bool forceNotify)
        {
            // Slot counts as the server says (the Normal bag in the M1 fields, the others per container).
            var rules = new ContainerRule[_rules.Count];
            for (int i = 0; i < _rules.Count; i++)
            {
                int slots = SlotsOf(_rules[i]);
                rules[i] = new ContainerRule(_rules[i].Code, slots, CategoriesOf(_rules[i]), _rules[i].IsEquipped);
            }
            var inventory = new Shared.Simulation.Items.Inventory(rules);
            if (_confirmed != null)
            {
                Load(inventory, _normalCode, _confirmed.Slots);
                foreach (var container in _confirmed.Containers)
                    Load(inventory, container.Code, container.Slots);
            }
            var preview = new HashSet<InventoryLocation>();
            foreach (var move in _pending)
            {
                _changes.Clear();
                var m = move.Move;
                inventory.TryMove(new InventoryLocation(CodeOf(m.FromContainer), m.FromSlot),
                    new InventoryLocation(CodeOf(m.ToContainer), m.ToSlot), m.Count, _items, _changes);
                foreach (var change in _changes)
                    preview.Add(new InventoryLocation(change.Container, change.Change.Slot));
            }

            bool changed = forceNotify;
            for (int index = 0; index < rules.Length; index++)
            {
                var bag = inventory.BagAt(index);
                var slots = _containers[index].Length == bag.SlotCount ? _containers[index] : new InventorySlotView[bag.SlotCount];
                changed |= !ReferenceEquals(slots, _containers[index]);
                for (int slot = 0; slot < bag.SlotCount; slot++)
                {
                    bool covered = index == _equippedIndex && bag[slot].IsEmpty && inventory.CoveredBy(slot, _items) >= 0;
                    var view = new InventorySlotView(bag[slot], preview.Contains(new InventoryLocation(rules[index].Code, slot)), covered);
                    if (!changed && !view.Equals(slots[slot]))
                        changed = true;
                    slots[slot] = view;
                }
                _containers[index] = slots;
            }
            if (_selected is { } selected)
            {
                int at = IndexOfKnown(selected.Container);
                if (at < 0 || selected.Slot >= _containers[at].Length || _containers[at][selected.Slot].IsEmpty)
                {
                    _selected = null;
                    SplitCount = 0;
                    changed = true;
                }
                else if (SplitCount > 0 && SplitCount >= _containers[at][selected.Slot].Count)
                {
                    // The stack shrank under the split (a pickup or another move): keep at most its units less one.
                    SplitCount = _containers[at][selected.Slot].Count - 1;
                    changed = true;
                }
            }
            if (Hovered >= TotalSlots)
                Hovered = None;
            if (changed)
                NotifyChanged();
        }

        private int IndexOfKnown(byte code)
        {
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Code == code)
                    return i;
            }
            return None;
        }

        private int SlotsOf(in ContainerRule rule)
        {
            if (_confirmed == null)
                return _containers[IndexOf(rule.Code)].Length;
            if (rule.Code == _normalCode)
                return Math.Max(1, (int)_confirmed.SlotCount);
            foreach (var container in _confirmed.Containers)
            {
                if (container.Code == rule.Code)
                    return Math.Max(1, (int)container.SlotCount);
            }
            return rule.Slots;
        }

        private static ItemCategory[] CategoriesOf(in ContainerRule rule)
        {
            var list = new List<ItemCategory>();
            foreach (ItemCategory category in Enum.GetValues(typeof(ItemCategory)))
            {
                if (rule.Accepts(category))
                    list.Add(category);
            }
            return list.ToArray();
        }

        private static void Load(Shared.Simulation.Items.Inventory inventory, byte code, InventorySlotEntry[] slots)
        {
            if (!inventory.TryIndex(code, out int index))
                return;
            var bag = inventory.BagAt(index);
            foreach (var entry in slots)
            {
                if (entry.Slot < bag.SlotCount)
                    bag.Load(entry.Slot, new ItemStack(entry.ItemNetId, entry.Rarity, entry.Stolen, entry.Count, entry.InstanceId));
            }
        }

        private static ItemCategory[] AllCategories() => (ItemCategory[])Enum.GetValues(typeof(ItemCategory));

        /// <summary>The M1 lookup: every item a stackable with its stack size, no equipment slots.</summary>
        private sealed class StackLookup : IItemRuleLookup
        {
            private readonly Func<ushort, int> _maxStack;

            public StackLookup(Func<ushort, int> maxStack) => _maxStack = maxStack;

            public bool TryGetItem(ushort itemNetId, out ItemRuleInfo info)
            {
                info = new ItemRuleInfo(ItemCategory.Material, Math.Max(1, _maxStack(itemNetId)), 0);
                return true;
            }
        }
    }
}
