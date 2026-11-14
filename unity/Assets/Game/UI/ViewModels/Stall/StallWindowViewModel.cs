#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Stall
{
    /// <summary>What the stall window shows (WP-M4-8).</summary>
    public enum StallWindowMode
    {
        None,
        /// <summary>Another player's stall: its listings with Buy.</summary>
        Browse,
        /// <summary>The own stall: its listings with Cancel, Add from the bag, Close stall.</summary>
        Manage,
        /// <summary>A free stall place: pick a bag stack, a count and a price, then Open stall.</summary>
        Open,
    }

    /// <summary>One stack of the Normal bag the owner may list (WP-M4-8).</summary>
    public readonly struct StallChoice
    {
        public StallChoice(byte container, byte slot, ushort itemNetId, int count, InventorySlotEntry? entry = null)
        {
            Entry = entry;
            Container = container;
            Slot = slot;
            ItemNetId = itemNetId;
            Count = count;
        }

        public byte Container { get; }
        public byte Slot { get; }
        public ushort ItemNetId { get; }
        public int Count { get; }

        /// <summary>The bag's entry (enchant level, rarity, quality) for the picker's text; null in tests that need none.</summary>
        public InventorySlotEntry? Entry { get; }
    }

    /// <summary>
    /// The stall window (spec 4.5, 9.13; M4 decision 7; WP-M4-8): the stall in reach as the server last sent it
    /// (<see cref="StallState"/>: its listings, oldest first, never the seller), its owner's display name (the stall entity's
    /// <c>EntityIdentity</c>, rule 8), the mode, and for the owner the bag stacks to list with the count and price typed. Display
    /// only: the server owns every stall and decides every open, add, cancel, close and buy; a refusal comes back as its reason.
    /// </summary>
    public sealed class StallWindowViewModel : ViewModel
    {
        private StallChoice[] _choices = Array.Empty<StallChoice>();

        public bool Open { get; private set; }

        public StallWindowMode Mode { get; private set; }

        /// <summary>The stall's entity (browse, manage); 0 at a free place.</summary>
        public uint StallEntityId { get; private set; }

        /// <summary>The free place's ordinal (open); -1 otherwise.</summary>
        public int Place { get; private set; } = -1;

        public string OwnerName { get; private set; } = "";

        /// <summary>The stall as last sent; null before it came (and at a free place).</summary>
        public StallState? State { get; private set; }

        public IReadOnlyList<ListingEntry> Rows => State?.Rows ?? Array.Empty<ListingEntry>();

        public int Cap { get; private set; }

        /// <summary>The bag stacks the owner may list (manage, open).</summary>
        public IReadOnlyList<StallChoice> Choices => _choices;

        /// <summary>The picked stack's index in <see cref="Choices"/>; -1 for none.</summary>
        public int Selected { get; private set; } = -1;

        public string CountText { get; private set; } = "";

        public string PriceText { get; private set; } = "";

        /// <summary>The request waiting for its answer (after its commit); 0 for none, one at a time.</summary>
        public uint Pending { get; private set; }

        /// <summary>
        /// The listing the buyer chose (browse; user decision 2026-11-14, "Choose the count": the buyer picks how many units of a row,
        /// with the total, as in the auction window); 0 for none.
        /// </summary>
        public long BuySelected { get; private set; }

        /// <summary>The units to buy of <see cref="BuySelectedRow"/>: 1 to its units (kept within them as typed).</summary>
        public int BuyCount { get; private set; } = 1;

        public ListingEntry? BuySelectedRow
        {
            get
            {
                foreach (var row in Rows)
                {
                    if (row.ListingId == BuySelected)
                        return row;
                }
                return null;
            }
        }

        public bool CanBuy => Open && Mode == StallWindowMode.Browse && Pending == 0 && BuySelectedRow is { } row && BuyCount >= 1
                              && BuyCount <= row.Item.Count;

        /// <summary>The buy's total at the chosen row's unit price; 0 without a choice or on overflow.</summary>
        public long BuyTotal => BuySelectedRow is { } row && row.UnitPrice <= long.MaxValue / Math.Max(1, BuyCount)
            ? row.UnitPrice * BuyCount
            : 0;

        public bool CanList => Mode is StallWindowMode.Manage or StallWindowMode.Open && Pending == 0 && Selected >= 0
                               && TryCount(out _) && TryPrice(out _) && (Mode == StallWindowMode.Open || Rows.Count < Cap);

        /// <param name="place">The place the stall stands on when the client knows it (its own open), else -1.</param>
        public void ShowStall(uint stallEntityId, string ownerName, bool own, int cap, int place = -1)
        {
            bool changed = StallEntityId != stallEntityId;
            StallEntityId = stallEntityId;
            Place = place;
            OwnerName = ownerName;
            Mode = own ? StallWindowMode.Manage : StallWindowMode.Browse;
            Cap = cap;
            if (changed)
            {
                State = null;
                Selected = -1;
                ClearBuy();
            }
            Open = true;
            NotifyChanged();
        }

        public void ShowPlace(int place, int cap)
        {
            ClearBuy();
            Mode = StallWindowMode.Open;
            Place = place;
            StallEntityId = 0;
            State = null;
            OwnerName = "";
            Cap = cap;
            Selected = -1;
            Open = true;
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            Mode = StallWindowMode.None;
            StallEntityId = 0;
            Place = -1;
            State = null;
            Pending = 0;
            ClearBuy();
            NotifyChanged();
        }

        /// <summary>A state of the stall shown; an own stall's state turns a browse into manage (and an open into manage).</summary>
        public void SetState(StallState state)
        {
            State = state;
            Cap = state.Cap;
            if (state.Own && state.Open)
                Mode = StallWindowMode.Manage;
            if (BuySelectedRow is { } chosen)
                BuyCount = Math.Min(BuyCount, chosen.Item.Count); // fewer units left: the count stays within them
            else
                ClearBuy(); // sold out or taken back
            NotifyChanged();
        }

        public void SetChoices(StallChoice[] choices)
        {
            _choices = choices;
            if (Selected >= choices.Length)
                Selected = -1;
            NotifyChanged();
        }

        public void Select(int index)
        {
            Selected = index >= 0 && index < _choices.Length ? index : -1;
            if (Selected >= 0 && CountText.Length == 0)
            {
                CountText = _choices[Selected].Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                TextRevision++; // set by the window, not typed: written into the field even if it has the focus
            }
            NotifyChanged();
        }

        /// <summary>
        /// Bumped when the window itself sets the count (an applied open or add clears it, a picked stack fills it in): the presenter
        /// then writes it into the field even while the field has the focus (the buttons never take it), so field and model agree.
        /// </summary>
        public int TextRevision { get; private set; }

        /// <summary>An applied open or add: the count is cleared for the next listing (the price stays).</summary>
        public void ClearCount()
        {
            CountText = "";
            TextRevision++;
            NotifyChanged();
        }

        public void SetCountText(string text)
        {
            CountText = text;
            NotifyChanged();
        }

        public void SetPriceText(string text)
        {
            PriceText = text;
            NotifyChanged();
        }

        /// <summary>Chooses a row to buy from (a second click clears it); the count starts at 1.</summary>
        public void SelectBuy(long listingId)
        {
            BuySelected = BuySelected == listingId ? 0 : listingId;
            BuyCount = 1;
            NotifyChanged();
        }

        /// <summary>The count to buy from its field, kept within the chosen row's units.</summary>
        public void SetBuyCount(string text)
        {
            int max = BuySelectedRow?.Item.Count ?? 1;
            BuyCount = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out int count) ? Math.Max(1, Math.Min(count, max)) : 1;
            NotifyChanged();
        }

        private void ClearBuy()
        {
            BuySelected = 0;
            BuyCount = 1;
        }

        public void SetPending(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>The typed count: 1 to the picked stack's units.</summary>
        public bool TryCount(out int count) =>
            int.TryParse(CountText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out count)
            && count > 0 && Selected >= 0 && count <= _choices[Selected].Count;

        /// <summary>The typed unit price: a positive whole number (the server checks its cap).</summary>
        public bool TryPrice(out long price) =>
            long.TryParse(PriceText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out price)
            && price > 0;
    }
}
