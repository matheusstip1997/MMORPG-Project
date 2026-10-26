#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace Game.UI.ViewModels.Inventory
{
    /// <summary>One bag slot as the window shows it: the confirmed stack, or the preview of a move not yet confirmed.</summary>
    public readonly struct InventorySlotView : IEquatable<InventorySlotView>
    {
        public InventorySlotView(in ItemStack stack, bool preview)
        {
            ItemNetId = stack.ItemNetId;
            Rarity = stack.Rarity;
            Stolen = stack.Stolen;
            Count = stack.Count;
            Preview = preview;
        }

        /// <summary><c>ItemDef.netId</c>; 0 for an empty slot.</summary>
        public ushort ItemNetId { get; }

        /// <summary><c>RarityDef.tier</c>.</summary>
        public byte Rarity { get; }

        public bool Stolen { get; }

        public int Count { get; }

        /// <summary>A move the server has not confirmed yet changed this slot.</summary>
        public bool Preview { get; }

        public bool IsEmpty => Count == 0;

        public bool Equals(InventorySlotView other) =>
            ItemNetId == other.ItemNetId && Rarity == other.Rarity && Stolen == other.Stolen && Count == other.Count
            && Preview == other.Preview;

        public override bool Equals(object? obj) => obj is InventorySlotView other && Equals(other);

        public override int GetHashCode() => (ItemNetId * 397) ^ (Count << 8) ^ (Preview ? 1 : 0) ^ Rarity;
    }

    /// <summary>A move the client sent and shows as a preview until the server answers.</summary>
    public readonly struct PendingMove
    {
        public PendingMove(uint requestId, byte fromSlot, byte toSlot, double sentAtMs, bool confirmed)
        {
            RequestId = requestId;
            FromSlot = fromSlot;
            ToSlot = toSlot;
            SentAtMs = sentAtMs;
            Confirmed = confirmed;
        }

        public uint RequestId { get; }
        public byte FromSlot { get; }
        public byte ToSlot { get; }
        public double SentAtMs { get; }

        /// <summary>The server accepted it; the contents that show it come next (the server sends them right after).</summary>
        public bool Confirmed { get; }

        public PendingMove AsConfirmed() => new PendingMove(RequestId, FromSlot, ToSlot, SentAtMs, true);
    }

    /// <summary>A move the window wants sent: from one slot onto another (move, merge or swap is the server's call).</summary>
    public readonly struct MoveIntent
    {
        public MoveIntent(byte fromSlot, byte toSlot)
        {
            FromSlot = fromSlot;
            ToSlot = toSlot;
        }

        public byte FromSlot { get; }
        public byte ToSlot { get; }
    }

    /// <summary>
    /// The inventory window (spec 9.13, backlog G client part, WP16): the Normal bag's slots as the server last confirmed
    /// them (<see cref="InventoryContents"/>, the bag as committed, rule 5), with the moves the player made and the server
    /// has not answered yet shown on top as a preview. The preview runs the shared <see cref="Bag.TryMove"/> rules, the same
    /// code the server applies, but it is never the truth: a refusal drops the move and the slots show the confirmed bag
    /// again (revert); an accepted move stays until the contents that include it arrive; a move with no answer at all is
    /// dropped after <c>InventoryWindowSettings.movePreviewTimeoutMs</c>. Also holds what the window needs to draw:
    /// open or closed, the selected slot (click-to-move) and the hovered one (tooltip). No game rules: the server decides
    /// every move. Raises <see cref="ViewModel.Changed"/> only on a real change.
    /// </summary>
    public sealed class InventoryWindowViewModel : ViewModel
    {
        public const int None = -1;

        private readonly Func<ushort, int> _maxStack;
        private readonly List<PendingMove> _pending = new List<PendingMove>();
        private readonly List<SlotChange> _changes = new List<SlotChange>();
        private InventoryContents? _confirmed;
        private InventorySlotView[] _slots;

        /// <param name="slotCount">The bag's slots before the server says (<c>ContainerDef.slots</c> of the Normal bag).</param>
        /// <param name="columns">Slots per grid row (<c>InventoryWindowSettings.columns</c>).</param>
        /// <param name="maxStack">Most units one stack of an item holds (<c>ItemDef.maxStack</c> by net id), for merge previews.</param>
        public InventoryWindowViewModel(int slotCount, int columns, Func<ushort, int> maxStack)
        {
            if (slotCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(slotCount));
            if (columns <= 0)
                throw new ArgumentOutOfRangeException(nameof(columns));
            Columns = columns;
            _maxStack = maxStack;
            _slots = new InventorySlotView[slotCount];
        }

        public bool Open { get; private set; }

        public int Columns { get; }

        public int SlotCount => _slots.Length;

        public int Rows => (SlotCount + Columns - 1) / Columns;

        /// <summary>The server sent a bag on this connection.</summary>
        public bool HasContents => _confirmed != null;

        /// <summary>The slot picked for click-to-move, or <see cref="None"/>.</summary>
        public int Selected { get; private set; } = None;

        /// <summary>The slot under the pointer (tooltip), or <see cref="None"/>.</summary>
        public int Hovered { get; private set; } = None;

        public IReadOnlyList<PendingMove> Pending => _pending;

        public InventorySlotView Slot(int slot) => _slots[slot];

        public void Toggle() => SetOpen(!Open);

        public void SetOpen(bool open)
        {
            if (open == Open)
                return;
            Open = open;
            if (!open)
            {
                Selected = None;
                Hovered = None;
            }
            NotifyChanged();
        }

        /// <summary>The bag as committed on the server: the new base of the preview; accepted moves are in it now.</summary>
        public void SetContents(InventoryContents contents)
        {
            _confirmed = contents;
            _pending.RemoveAll(move => move.Confirmed);
            Rebuild(forceNotify: _slots.Length != Math.Max(1, (int)contents.SlotCount));
        }

        public void SetHovered(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
                slot = None;
            if (slot == Hovered)
                return;
            Hovered = slot;
            NotifyChanged();
        }

        /// <summary>
        /// Click-to-move: the first click on a stack selects it, a second click on it deselects it, and a click on another
        /// slot asks to move the selection there. Clicking an empty slot with nothing selected does nothing.
        /// </summary>
        public MoveIntent? Click(int slot)
        {
            if (slot < 0 || slot >= SlotCount)
                return null;
            if (Selected == None)
            {
                if (_slots[slot].IsEmpty)
                    return null;
                Selected = slot;
                NotifyChanged();
                return null;
            }
            int from = Selected;
            Selected = None;
            NotifyChanged();
            return from == slot ? (MoveIntent?)null : Drop(from, slot);
        }

        /// <summary>A stack dragged from <paramref name="from"/> and released on <paramref name="to"/>.</summary>
        public MoveIntent? Drop(int from, int to)
        {
            if (from == to || from < 0 || to < 0 || from >= SlotCount || to >= SlotCount || _slots[from].IsEmpty)
                return null;
            if (Selected != None)
            {
                Selected = None;
                NotifyChanged();
            }
            return new MoveIntent((byte)from, (byte)to);
        }

        /// <summary>
        /// A stack dragged out of the window: dropping items on the ground is not in M1 (loot bags, M4), so nothing is sent
        /// and the reason code for the toast comes back.
        /// </summary>
        public string DropOutside(int from)
        {
            if (Selected != None)
            {
                Selected = None;
                NotifyChanged();
            }
            return ClientReasonCode.ItemDropUnavailable.ToString();
        }

        /// <summary>The move <paramref name="requestId"/> went out: show it until the server answers.</summary>
        public void AddPending(uint requestId, in MoveIntent move, double nowMs)
        {
            _pending.Add(new PendingMove(requestId, move.FromSlot, move.ToSlot, nowMs, confirmed: false));
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

        /// <summary>Drops moves with no answer after <paramref name="timeoutMs"/>; the slots show the confirmed bag again.</summary>
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
            Selected = None;
            Hovered = None;
            Rebuild(forceNotify: true);
        }

        private void Rebuild(bool forceNotify)
        {
            int count = _confirmed != null ? Math.Max(1, (int)_confirmed.SlotCount) : _slots.Length;
            var bag = new Bag(count);
            if (_confirmed != null)
            {
                foreach (var entry in _confirmed.Slots)
                    bag.Load(entry.Slot, new ItemStack(entry.ItemNetId, entry.Rarity, entry.Stolen, entry.Count));
            }
            var preview = new bool[count];
            foreach (var move in _pending)
            {
                _changes.Clear();
                int from = move.FromSlot, to = move.ToSlot;
                int maxStack = from < count && !bag[from].IsEmpty ? Math.Max(1, _maxStack(bag[from].ItemNetId)) : 1;
                bag.TryMove(from, to, maxStack, _changes);
                foreach (var change in _changes)
                    preview[change.Slot] = true;
            }

            bool changed = forceNotify || count != _slots.Length;
            var slots = count == _slots.Length ? _slots : new InventorySlotView[count];
            for (int i = 0; i < count; i++)
            {
                var view = new InventorySlotView(bag[i], preview[i]);
                if (!changed && !view.Equals(slots[i]))
                    changed = true;
                slots[i] = view;
            }
            _slots = slots;
            if (Selected != None && (Selected >= count || _slots[Selected].IsEmpty))
            {
                Selected = None;
                changed = true;
            }
            if (Hovered >= count)
                Hovered = None;
            if (changed)
                NotifyChanged();
        }
    }
}
