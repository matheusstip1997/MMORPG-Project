#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;

namespace Game.UI.ViewModels.Auction
{
    /// <summary>The auction window's tabs (WP-M4-7).</summary>
    public enum AuctionTab : byte
    {
        Search = 0,
        Mine = 1,
        Sell = 2,
    }

    /// <summary>A buy the window asks for: the listing, the units and the unit price the player saw.</summary>
    public readonly struct AuctionBuy
    {
        public AuctionBuy(long listingId, int count, long unitPrice)
        {
            ListingId = listingId;
            Count = count;
            UnitPrice = unitPrice;
        }

        public long ListingId { get; }
        public int Count { get; }
        public long UnitPrice { get; }
    }

    /// <summary>A listing the window asks to create: one bag slot's units at a unit price for a duration.</summary>
    public readonly struct AuctionSale
    {
        public AuctionSale(byte container, byte slot, int count, long unitPrice, int durationIndex)
        {
            Container = container;
            Slot = slot;
            Count = count;
            UnitPrice = unitPrice;
            DurationIndex = durationIndex;
        }

        public byte Container { get; }
        public byte Slot { get; }
        public int Count { get; }
        public long UnitPrice { get; }
        public int DurationIndex { get; }
    }

    /// <summary>
    /// The auction window (spec 4.5, 9.13; M4 decision 6; WP-M4-7) at one auctioneer: three tabs. Search: the filters, the last page
    /// the server sent (cheapest unit price first) with its cursor stack for Previous and Next, the selected row and the count to
    /// buy. My listings: the player's active listings and the cap. Sell: one bag stack (dropped from the inventory window or picked),
    /// the count, the unit price and the duration. One create, buy or cancel waits for its answer at a time. A refusal comes back as
    /// its reason code; <c>ListingSold</c>, <c>ListingBusy</c> and <c>ListingChanged</c> also stay in the window as a notice line
    /// (the row the player saw is gone, saving, or no longer what it was) until the next search. Display only: the server owns every
    /// listing and decides every sale.
    /// </summary>
    public sealed class AuctionWindowViewModel : ViewModel
    {
        private enum CursorOp : byte
        {
            None,
            Reset,
            Push,
            Pop,
        }

        private readonly List<(long Price, long Id)> _cursors = new List<(long, long)>();
        private CursorOp _staged;
        private (long Price, long Id) _stagedPush;
        private CursorOp _pending;
        private (long Price, long Id) _pendingPush;
        private ListingEntry[] _rows = Array.Empty<ListingEntry>();
        private ListingEntry[] _mine = Array.Empty<ListingEntry>();

        public bool Open { get; private set; }

        /// <summary>The auctioneer every request names (the server checks the reach each time); 0 while closed.</summary>
        public uint NpcEntityId { get; private set; }

        public AuctionTab Tab { get; private set; }

        // --- Search ---

        public string NameText { get; private set; } = "";

        /// <summary>The category filter: an index into <c>AuctionConfig.Categories</c>, -1 for any.</summary>
        public int CategoryIndex { get; private set; } = -1;

        /// <summary>The rarity filter: an index into <c>AuctionConfig.RarityTiers</c> (that tier or better), -1 for any.</summary>
        public int RarityIndex { get; private set; } = -1;

        /// <summary>The job filter: an index into <c>AuctionConfig.Jobs</c>, -1 for any.</summary>
        public int JobIndex { get; private set; } = -1;

        /// <summary>The armour tier filter (<c>ArmorClass</c> 1 to 3), 0 for any.</summary>
        public byte ArmorTier { get; private set; }

        /// <summary>The level range on <c>ItemDef.minLevel</c>; 0 for no bound.</summary>
        public ushort LevelMin { get; private set; }

        public ushort LevelMax { get; private set; }

        /// <summary>The search waiting for its page; 0 for none.</summary>
        public uint PendingSearch { get; private set; }

        public bool HasPage { get; private set; }

        /// <summary>The rows of the shown page, cheapest unit price first.</summary>
        public IReadOnlyList<ListingEntry> Rows => _rows;

        public bool HasMore { get; private set; }

        /// <summary>The shown page, from 0.</summary>
        public int PageIndex { get; private set; }

        /// <summary>No item is named like the typed text: nothing was asked.</summary>
        public bool NoMatch { get; private set; }

        /// <summary>More items have a name like the typed text than one search names: only the first were searched.</summary>
        public bool TooManyMatches { get; private set; }

        /// <summary>The selected row's listing id; 0 for none.</summary>
        public long Selected { get; private set; }

        public int BuyCount { get; private set; } = 1;

        // --- My listings ---

        public IReadOnlyList<ListingEntry> Mine => _mine;

        public int MineCap { get; private set; }

        public bool HasMine { get; private set; }

        // --- Sell ---

        private InventorySlotEntry[] _sellable = Array.Empty<InventorySlotEntry>();

        /// <summary>The Normal bag's stacks the Sell tab offers to pick (tradable, not reserved; the server checks again).</summary>
        public IReadOnlyList<InventorySlotEntry> Sellable => _sellable;

        /// <summary>The <c>ContainerDef.code</c> of <see cref="Sellable"/> (the Normal bag).</summary>
        public byte SellableContainer { get; private set; }

        public void SetSellable(byte container, IReadOnlyList<InventorySlotEntry> stacks)
        {
            SellableContainer = container;
            _sellable = new InventorySlotEntry[stacks.Count];
            for (int i = 0; i < stacks.Count; i++)
                _sellable[i] = stacks[i];
            NotifyChanged();
        }

        /// <summary>The stack to list; null for none. <see cref="SellContainer"/> is its <c>ContainerDef.code</c>.</summary>
        public InventorySlotEntry? SellItem { get; private set; }

        public byte SellContainer { get; private set; }

        public int SellCount { get; private set; }

        public long SellPrice { get; private set; }

        public int DurationIndex { get; private set; }

        // --- Answers ---

        /// <summary>The create, buy or cancel waiting for its answer (after its commit); 0 for none, one at a time.</summary>
        public uint PendingAction { get; private set; }

        /// <summary>
        /// The reason code of the last <c>ListingSold</c>, <c>ListingBusy</c> or <c>ListingChanged</c>, shown in the window (spec 9.1:
        /// its <c>ReasonCodeStrings</c> text) until the next page; empty for none.
        /// </summary>
        public string Notice { get; private set; } = "";

        public ListingEntry? SelectedRow
        {
            get
            {
                foreach (var row in _rows)
                {
                    if (row.ListingId == Selected)
                        return row;
                }
                return null;
            }
        }

        public bool CanSearch => Open && PendingSearch == 0;

        public bool CanPrevious => CanSearch && PageIndex > 0;

        public bool CanNext => CanSearch && HasPage && HasMore && _rows.Length > 0;

        public bool CanBuy => Open && PendingAction == 0 && SelectedRow is { } row && BuyCount >= 1 && BuyCount <= row.Item.Count;

        public bool CanCancel => Open && PendingAction == 0;

        public bool CanList => Open && PendingAction == 0 && SellItem != null && SellCount >= 1 && SellCount <= SellItem.Count
                               && SellPrice >= 1;

        /// <summary>The buy's total at the selected row's unit price; 0 without a selection or on overflow.</summary>
        public long BuyTotal => SelectedRow is { } row && row.UnitPrice <= long.MaxValue / Math.Max(1, BuyCount)
            ? row.UnitPrice * BuyCount
            : 0;

        /// <summary>Opens at an auctioneer (the server's yes to the interaction): the Search tab, no page yet.</summary>
        public void OpenFor(uint npcEntityId)
        {
            if (Open && NpcEntityId == npcEntityId)
                return;
            Open = true;
            NpcEntityId = npcEntityId;
            Tab = AuctionTab.Search;
            ClearPage();
            Notice = "";
            NotifyChanged();
        }

        /// <summary>
        /// Closes: the filters stay for the next visit; the page and the selection do not. A create, buy or cancel already sent
        /// stays pending (<see cref="PendingAction"/>): its answer still comes after its commit and still shows its toast.
        /// </summary>
        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            NpcEntityId = 0;
            ClearPage();
            ClearSell();
            Notice = "";
            NotifyChanged();
        }

        /// <summary>A lost connection: everything goes.</summary>
        public void Clear()
        {
            Open = false;
            NpcEntityId = 0;
            ClearPage();
            _mine = Array.Empty<ListingEntry>();
            MineCap = 0;
            HasMine = false;
            _sellable = Array.Empty<InventorySlotEntry>();
            PendingAction = 0;
            ClearSell();
            Notice = "";
            NotifyChanged();
        }

        public void SetTab(AuctionTab tab)
        {
            if (Tab == tab)
                return;
            Tab = tab;
            NotifyChanged();
        }

        public void SetName(string text)
        {
            NameText = text ?? "";
        }

        /// <summary>Steps a filter through "any" and its values (<paramref name="count"/> of them).</summary>
        public void CycleCategory(int count) => CategoryIndex = Cycle(CategoryIndex, count);

        public void CycleRarity(int count) => RarityIndex = Cycle(RarityIndex, count);

        public void CycleJob(int count) => JobIndex = Cycle(JobIndex, count);

        public void CycleArmor() => ArmorTier = (byte)((ArmorTier + 1) % 4);

        /// <summary>A level bound from its field; anything but a whole number from 0 to 65535 is no bound.</summary>
        public void SetLevelMin(string text) => LevelMin = ParseLevel(text);

        public void SetLevelMax(string text) => LevelMax = ParseLevel(text);

        public void FiltersChanged() => NotifyChanged();

        /// <summary>
        /// The first page of the current filters: the cursor stack starts again. <paramref name="itemNetIds"/> is the name filter's
        /// items (empty for no name). False (nothing to ask) when a name was typed but no item matched: the window says so.
        /// </summary>
        /// <param name="tooMany">
        /// More items match the typed name than one search may name (<c>ListingSettings.searchItemIdsMax</c>): only the first are
        /// searched, and the window says so with the page.
        /// </param>
        public bool FirstPage(ushort[] itemNetIds, byte category, byte rarityMin, ushort jobNetId, bool tooMany,
            out ListingSearchRequest request)
        {
            request = Filters(itemNetIds, category, rarityMin, jobNetId);
            Notice = ""; // the player's own search: the last notice has been read
            bool named = NameText.Trim().Length > 0;
            NoMatch = named && itemNetIds.Length == 0;
            TooManyMatches = tooMany && !NoMatch;
            if (NoMatch)
            {
                _cursors.Clear();
                PageIndex = 0;
                _staged = CursorOp.None;
                _rows = Array.Empty<ListingEntry>();
                HasPage = true;
                HasMore = false;
                Selected = 0;
                NotifyChanged();
                return false;
            }
            Stage(CursorOp.Reset, (0, 0));
            NotifyChanged();
            return true;
        }

        /// <summary>
        /// The page after the shown one, with the same filters (the last row is the cursor). The cursor stack moves only when that
        /// page arrives: a refused or unsent search leaves it where it was.
        /// </summary>
        public bool NextPage(ListingSearchRequest filters, out ListingSearchRequest request)
        {
            request = filters;
            if (!CanNext)
                return false;
            var last = _rows[_rows.Length - 1];
            Stage(CursorOp.Push, (last.UnitPrice, last.ListingId));
            Notice = "";
            request = WithCursor(filters, (last.UnitPrice, last.ListingId));
            NotifyChanged();
            return true;
        }

        /// <summary>The page before the shown one (its cursor was kept); the stack moves only when that page arrives.</summary>
        public bool PreviousPage(ListingSearchRequest filters, out ListingSearchRequest request)
        {
            request = filters;
            if (!CanPrevious || _cursors.Count < 2)
                return false;
            Stage(CursorOp.Pop, (0, 0));
            Notice = "";
            request = WithCursor(filters, _cursors[_cursors.Count - 2]);
            NotifyChanged();
            return true;
        }

        /// <summary>The shown page again (after a buy, or a notice that the row moved); the cursor stack stays.</summary>
        public ListingSearchRequest SamePage(ListingSearchRequest filters)
        {
            _staged = CursorOp.None;
            return WithCursor(filters, _cursors.Count == 0 ? (0, 0) : _cursors[_cursors.Count - 1]);
        }

        public void SearchSent(uint requestId)
        {
            PendingSearch = requestId;
            _pending = _staged;
            _pendingPush = _stagedPush;
            _staged = CursorOp.None;
            NotifyChanged();
        }

        /// <summary>The search could not be sent (not connected): the staged page move is dropped.</summary>
        public void SearchNotSent() => _staged = CursorOp.None;

        /// <summary>
        /// A search page: shown when it answers the pending search, and only then the cursor stack moves (a first page starts it
        /// again, Next pushes, Previous pops). Returns the reason code of a refusal (the stack and the shown page stay), else null.
        /// The notice line stays: it goes with the player's own next action, not with this answer.
        /// </summary>
        public string? OnPage(ListingSearchResponse page)
        {
            if (page.RequestId != PendingSearch || PendingSearch == 0)
                return null;
            PendingSearch = 0;
            var op = _pending;
            _pending = CursorOp.None;
            if (page.Reason != ListingReason.None)
            {
                NotifyChanged();
                return page.Reason.ToString();
            }
            switch (op)
            {
                case CursorOp.Reset:
                    _cursors.Clear();
                    _cursors.Add((0, 0));
                    break;
                case CursorOp.Push:
                    _cursors.Add(_pendingPush);
                    break;
                case CursorOp.Pop:
                    if (_cursors.Count > 1)
                        _cursors.RemoveAt(_cursors.Count - 1);
                    break;
            }
            PageIndex = Math.Max(0, _cursors.Count - 1);
            _rows = page.Rows;
            HasMore = page.HasMore;
            HasPage = true;
            if (SelectedRow == null)
                Selected = 0;
            else
                BuyCount = Math.Min(BuyCount, SelectedRow.Item.Count);
            NotifyChanged();
            return null;
        }

        /// <summary>Selects a row of the page (a second click clears it); the count starts at 1, and the notice goes.</summary>
        public void Select(long listingId)
        {
            Selected = Selected == listingId ? 0 : listingId;
            BuyCount = 1;
            Notice = "";
            NotifyChanged();
        }

        private void Stage(CursorOp op, (long Price, long Id) push)
        {
            _staged = op;
            _stagedPush = push;
        }

        /// <summary>The count to buy from its field, kept within the selected row's units.</summary>
        public void SetBuyCount(string text)
        {
            int max = SelectedRow?.Item.Count ?? 1;
            BuyCount = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out int count) ? Math.Max(1, Math.Min(count, max)) : 1;
            NotifyChanged();
        }

        public bool TryBuy(out AuctionBuy buy)
        {
            buy = default;
            if (!CanBuy)
                return false;
            var row = SelectedRow!;
            buy = new AuctionBuy(row.ListingId, BuyCount, row.UnitPrice);
            return true;
        }

        public void ActionSent(uint requestId)
        {
            PendingAction = requestId;
            Notice = ""; // the player's own next action: the last notice has been read
            NotifyChanged();
        }

        /// <summary>The own listings (asked for, or pushed when the auction opened). Returns the reason code of a refusal, else null.</summary>
        public string? OnMine(MyListings mine)
        {
            if (mine.Reason != ListingReason.None)
                return mine.Reason.ToString();
            _mine = mine.Rows;
            MineCap = mine.Cap;
            HasMine = true;
            NotifyChanged();
            return null;
        }

        /// <summary>The stack to list (a drop from the inventory window or the picker); the count starts at the whole stack.</summary>
        public void SetSellItem(byte container, InventorySlotEntry entry)
        {
            SellContainer = container;
            SellItem = entry;
            SellCount = entry.Count;
            Tab = AuctionTab.Sell;
            NotifyChanged();
        }

        /// <summary>
        /// The bag changed: the stack to list is kept while its slot still holds the same item (the count kept within it), else
        /// cleared. <paramref name="current"/> is that slot now (null: empty).
        /// </summary>
        public void RefreshSellItem(InventorySlotEntry? current)
        {
            if (SellItem == null)
                return;
            if (current == null || current.ItemNetId != SellItem.ItemNetId || current.Reserved || current.InstanceId != SellItem.InstanceId)
            {
                ClearSell();
            }
            else
            {
                SellItem = current;
                SellCount = Math.Max(1, Math.Min(SellCount, current.Count));
            }
            NotifyChanged();
        }

        public void SetSellCount(string text)
        {
            int max = SellItem?.Count ?? 0;
            SellCount = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out int count) ? Math.Max(1, Math.Min(count, max)) : 0;
            NotifyChanged();
        }

        public void SetSellPrice(string text)
        {
            SellPrice = long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
                out long price) ? price : 0;
            NotifyChanged();
        }

        public void CycleDuration(int count)
        {
            DurationIndex = count <= 0 ? 0 : (DurationIndex + 1) % count;
            NotifyChanged();
        }

        public bool TryList(out AuctionSale sale)
        {
            sale = default;
            if (!CanList)
                return false;
            sale = new AuctionSale(SellContainer, SellItem!.Slot, SellCount, SellPrice, DurationIndex);
            return true;
        }

        /// <summary>
        /// The answer to the pending create, buy or cancel. Returns the reason code of a refusal, else null; <paramref name="applied"/>
        /// for one that committed. A sold-out, busy or changed listing also sets <see cref="Notice"/>; an applied create clears the
        /// sell form; an applied buy clears the selection.
        /// </summary>
        public string? OnResult(ListingResult result, out bool applied)
        {
            applied = false;
            if (result.RequestId != PendingAction || PendingAction == 0)
                return null;
            PendingAction = 0;
            if (result.Reason != ListingReason.None)
            {
                if (result.Reason == ListingReason.ListingSold || result.Reason == ListingReason.ListingBusy
                    || result.Reason == ListingReason.ListingChanged || result.Reason == ListingReason.ListingNotFound)
                {
                    Notice = result.Reason.ToString();
                    if (result.Reason != ListingReason.ListingBusy)
                        Selected = 0;
                }
                NotifyChanged();
                return result.Reason.ToString();
            }
            applied = true;
            switch (result.Action)
            {
                case ListingAction.Create:
                    ClearSell();
                    break;
                case ListingAction.Buy:
                    Selected = 0;
                    BuyCount = 1;
                    break;
            }
            NotifyChanged();
            return null;
        }

        private void ClearPage()
        {
            _rows = Array.Empty<ListingEntry>();
            _cursors.Clear();
            PageIndex = 0;
            HasPage = false;
            HasMore = false;
            NoMatch = false;
            TooManyMatches = false;
            PendingSearch = 0;
            _staged = CursorOp.None;
            _pending = CursorOp.None;
            Selected = 0;
            BuyCount = 1;
        }

        private void ClearSell()
        {
            SellItem = null;
            SellContainer = 0;
            SellCount = 0;
        }

        private ListingSearchRequest Filters(ushort[] itemNetIds, byte category, byte rarityMin, ushort jobNetId) => new ListingSearchRequest
        {
            ItemNetIds = itemNetIds,
            Category = category,
            RarityMin = rarityMin,
            LevelMin = LevelMin,
            LevelMax = LevelMax,
            JobNetId = jobNetId,
            ArmorTier = ArmorTier,
        };

        private static ListingSearchRequest WithCursor(ListingSearchRequest filters, (long Price, long Id) cursor) => new ListingSearchRequest
        {
            ItemNetIds = filters.ItemNetIds,
            Category = filters.Category,
            RarityMin = filters.RarityMin,
            RarityMax = filters.RarityMax,
            LevelMin = filters.LevelMin,
            LevelMax = filters.LevelMax,
            JobNetId = filters.JobNetId,
            ArmorTier = filters.ArmorTier,
            AfterUnitPrice = cursor.Price,
            AfterListingId = cursor.Id,
        };

        private static int Cycle(int index, int count) => count <= 0 ? -1 : index + 1 >= count ? -1 : index + 1;

        private static ushort ParseLevel(string text) =>
            ushort.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ushort level)
                ? level
                : (ushort)0;
    }
}
