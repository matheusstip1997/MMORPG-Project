#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Auction;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.Menus.Auction
{
    /// <summary>What the player does in the auction window; the binder turns it into requests (WP-M4-7).</summary>
    public interface IAuctionWindowActions
    {
        void SelectTab(AuctionTab tab);
        void EditName(string text);
        void CycleCategory();
        void CycleRarity();
        void CycleJob();
        void CycleArmor();
        void EditLevelMin(string text);
        void EditLevelMax(string text);
        void Search();
        void Previous();
        void Next();
        void SelectRow(long listingId);
        void EditBuyCount(string text);
        void Buy();
        void CancelListing(long listingId);
        void PickSell(byte container, InventorySlotEntry entry);
        void EditSellCount(string text);
        void EditSellPrice(string text);
        void CycleDuration();
        void List();
        void CloseAuction();

        /// <summary>A text field of the window gained (true) or lost focus: gameplay keys stay off while one has it.</summary>
        void FocusField(bool focused);
    }

    /// <summary>
    /// Connects the auction window (spec 4.5, 9.13; M4 decision 6; WP-M4-7) to the world client. The auctioneer's yes
    /// (<see cref="WorldClientFeed.OutpostInteracted"/>, kind <c>Auction</c>) opens it at that NPC with the own listings the server
    /// pushed along; it closes when the NPC leaves the reach. Search turns the typed name into item net ids
    /// (<see cref="AuctionConfig.MatchNames"/>: the server never sees the text) and asks for the first page; Previous and Next walk the
    /// cursor stack. Buy, List and Cancel go through <see cref="WorldClientFeed.ListingRequests"/>, one at a time; an applied one
    /// (after its commit) shows a news toast and asks again for what it changed (the shown page after a buy, the own listings after a
    /// create or a cancel). A refusal shows its <c>ReasonCodeStrings</c> text in the HUD toast; <c>ListingSold</c>,
    /// <c>ListingChanged</c> and <c>ListingNotFound</c> also stay in the window and ask for the shown page again (the row the player
    /// saw is gone or moved), <c>ListingBusy</c> stays in the window (another purchase is saving: try again in a moment); a notice
    /// stays until the player's own next search, row or action, never only until that refresh. The own listings are asked for each
    /// time My listings is chosen and after a cancel refused as sold, gone or expired. An answer that comes after the window closed
    /// still shows its toast and asks for nothing. The sell form follows the bag: a stack that moved or went is cleared. Display
    /// time is the caller's clock in ms.
    /// </summary>
    public sealed class AuctionBinder : IAuctionWindowActions, IDisposable
    {
        private readonly AuctionConfig _config;
        private readonly UiText _text;
        private readonly AuctionWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;
        private ListingSearchRequest _filters = new ListingSearchRequest();
        private ListingAction _pendingKind;
        private int _pendingCount;
        private ushort _pendingItem;
        private uint _pendingMine;
        private bool _fieldFocused;

        public AuctionBinder(AuctionConfig config, UiText text, AuctionWindowViewModel window, ToastViewModel toast, int toastMs,
            Func<double> nowMs)
        {
            _config = config;
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>The filters of the shown page (the QA smoke reads them).</summary>
        public ListingSearchRequest Filters => _filters;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.OutpostInteracted += OnInteracted;
            feed.ListingPageReceived += OnPage;
            feed.MyListingsReceived += OnMine;
            feed.ListingAnswered += OnAnswered;
            feed.InventoryReceived += OnContents;
            feed.Disconnected += OnDisconnected;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.ListingPageReceived -= OnPage;
            _feed.MyListingsReceived -= OnMine;
            _feed.ListingAnswered -= OnAnswered;
            _feed.InventoryReceived -= OnContents;
            _feed.Disconnected -= OnDisconnected;
            if (_fieldFocused)
                _feed.WindowTextInput = false;
            _fieldFocused = false;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: the window closes when its auctioneer is no longer the NPC in reach.</summary>
        public void Frame(double nowMs)
        {
            if (!_window.Open || _feed == null)
                return;
            if (_feed.NpcTarget != _window.NpcEntityId)
                CloseAuction();
        }

        public void SelectTab(AuctionTab tab)
        {
            _window.SetTab(tab);
            if (tab == AuctionTab.Mine && _pendingMine == 0)
                RequestMine(); // every time the tab is chosen: a listing may have sold since (the server's rate limit applies)
        }

        public void EditName(string text) => _window.SetName(text);

        public void CycleCategory()
        {
            _window.CycleCategory(AuctionConfig.Categories.Length);
            _window.FiltersChanged();
        }

        public void CycleRarity()
        {
            _window.CycleRarity(_config.RarityTiers.Count);
            _window.FiltersChanged();
        }

        public void CycleJob()
        {
            _window.CycleJob(_config.Jobs.Count);
            _window.FiltersChanged();
        }

        public void CycleArmor()
        {
            _window.CycleArmor();
            _window.FiltersChanged();
        }

        public void EditLevelMin(string text) => _window.SetLevelMin(text);

        public void EditLevelMax(string text) => _window.SetLevelMax(text);

        /// <summary>The first page of the filters as they are now.</summary>
        public void Search()
        {
            if (!_window.CanSearch)
                return;
            var ids = _config.MatchNames(_window.NameText, id => _text.Get(id), out bool tooMany);
            byte category = _window.CategoryIndex < 0 ? (byte)0 : (byte)((byte)AuctionConfig.Categories[_window.CategoryIndex] + 1);
            byte rarity = _window.RarityIndex < 0 ? (byte)0 : _config.RarityTiers[_window.RarityIndex];
            ushort job = _window.JobIndex < 0 ? (ushort)0 : (ushort)_config.Jobs[_window.JobIndex].NetId;
            if (!_window.FirstPage(ids, category, rarity, job, tooMany, out var request))
                return;
            _filters = request;
            SendSearch(request);
        }

        public void Previous()
        {
            if (_window.PreviousPage(_filters, out var request))
                SendSearch(request);
        }

        public void Next()
        {
            if (_window.NextPage(_filters, out var request))
                SendSearch(request);
        }

        public void SelectRow(long listingId) => _window.Select(listingId);

        public void EditBuyCount(string text) => _window.SetBuyCount(text);

        public void Buy()
        {
            if (_feed?.ListingRequests == null || !_window.TryBuy(out var buy))
                return;
            var row = _window.SelectedRow!;
            uint id = _feed.ListingRequests.BuyListing(_window.NpcEntityId, buy.ListingId, buy.Count, buy.UnitPrice);
            if (id != 0)
                Pending(id, ListingAction.Buy, buy.Count, row.Item.ItemNetId);
        }

        public void CancelListing(long listingId)
        {
            if (_feed?.ListingRequests == null || !_window.CanCancel)
                return;
            uint id = _feed.ListingRequests.CancelListing(_window.NpcEntityId, listingId);
            if (id != 0)
                Pending(id, ListingAction.Cancel, 0, 0);
        }

        public void PickSell(byte container, InventorySlotEntry entry)
        {
            if (_window.Open && entry.Count > 0)
                _window.SetSellItem(container, entry);
        }

        public void EditSellCount(string text) => _window.SetSellCount(text);

        public void EditSellPrice(string text) => _window.SetSellPrice(text);

        public void CycleDuration() => _window.CycleDuration(_config.DurationsMs.Count);

        public void List()
        {
            if (_feed?.ListingRequests == null || !_window.TryList(out var sale))
                return;
            ushort item = _window.SellItem!.ItemNetId;
            uint id = _feed.ListingRequests.CreateListing(_window.NpcEntityId, sale.Container, sale.Slot, sale.Count, sale.UnitPrice,
                sale.DurationIndex);
            if (id != 0)
                Pending(id, ListingAction.Create, sale.Count, item);
        }

        public void CloseAuction()
        {
            FocusField(false);
            _window.Close();
        }

        public void FocusField(bool focused)
        {
            _fieldFocused = focused && _window.Open;
            if (_feed != null)
                _feed.WindowTextInput = _fieldFocused;
        }

        /// <summary>
        /// An inventory window drop onto the open window (the HUD's hook): the stack becomes the one to list, on the Sell tab. The
        /// item stays where it is until the server takes it into escrow.
        /// </summary>
        public bool Drop(byte container, InventorySlotEntry entry)
        {
            if (!_window.Open || entry.Count == 0)
                return false;
            _window.SetSellItem(container, entry);
            return true;
        }

        private void Pending(uint requestId, ListingAction kind, int count, ushort item)
        {
            _pendingKind = kind;
            _pendingCount = count;
            _pendingItem = item;
            _window.ActionSent(requestId);
        }

        private void SendSearch(ListingSearchRequest request)
        {
            uint id = _window.Open ? _feed?.ListingRequests?.Search(_window.NpcEntityId, request) ?? 0 : 0;
            if (id != 0)
                _window.SearchSent(id);
            else
                _window.SearchNotSent(); // the page move it staged is dropped
        }

        private void RequestMine()
        {
            if (!_window.Open)
                return; // closed: the auction pushes the own listings when it opens again
            uint id = _feed?.ListingRequests?.RequestMyListings(_window.NpcEntityId) ?? 0;
            if (id != 0)
                _pendingMine = id;
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.Kind != NpcInteraction.Auction || result.Reason != OutpostReason.None)
                return;
            _window.OpenFor(result.NpcEntityId);
            if (_feed?.Inventory != null)
                OnContents(_feed.Inventory);
        }

        private void OnPage(ListingSearchResponse page)
        {
            if (_window.OnPage(page) is { } refused)
                Toast(refused);
        }

        /// <summary>The own listings: pushed when the auction opens (request id 0), or the answer to the last ask.</summary>
        private void OnMine(MyListings mine)
        {
            if (mine.RequestId != 0 && mine.RequestId != _pendingMine)
                return;
            _pendingMine = 0;
            if (_window.OnMine(mine) is { } refused && mine.RequestId != 0)
                Toast(refused);
        }

        private void OnAnswered(ListingResult result)
        {
            var kind = _pendingKind;
            int count = _pendingCount;
            ushort item = _pendingItem;
            string? refused = _window.OnResult(result, out bool applied);
            if (refused != null)
            {
                Toast(refused);
                if (result.Reason is ListingReason.ListingSold or ListingReason.ListingChanged or ListingReason.ListingNotFound
                    && _window.HasPage && _window.CanSearch)
                    SendSearch(_window.SamePage(_filters));
                if (kind == ListingAction.Cancel && result.Reason is ListingReason.ListingNotFound or ListingReason.ListingExpired
                    or ListingReason.ListingSold)
                    RequestMine();
                return;
            }
            if (!applied)
                return;
            switch (result.Action)
            {
                case ListingAction.Create:
                    _toast.ShowText(AuctionTextIds.ToastListed, "what", ItemCount(_config, _text, item, result.Count), _now(), _toastMs);
                    RequestMine();
                    break;
                case ListingAction.Buy:
                    string what = ItemCount(_config, _text, item, result.Count > 0 ? result.Count : count);
                    if (result.ToMail)
                        _toast.ShowText(AuctionTextIds.ToastBoughtMail, "what", what, _now(), _toastMs);
                    else
                        _toast.ShowText(AuctionTextIds.ToastBought, "what", what, "gold", NumberText.Of(result.Price), _now(), _toastMs);
                    if (_window.HasPage && _window.CanSearch)
                        SendSearch(_window.SamePage(_filters));
                    break;
                case ListingAction.Cancel:
                    _toast.ShowText(AuctionTextIds.ToastCancelled, "", "", _now(), _toastMs);
                    RequestMine();
                    break;
            }
        }

        private void OnContents(InventoryContents contents)
        {
            _window.SetSellable(_config.Inventory.NormalBagCode, Sellable(_config, contents));
            if (_window.SellItem is not { } sell)
                return;
            _window.RefreshSellItem(SlotOf(contents, _config.Inventory.NormalBagCode, _window.SellContainer, sell.Slot));
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Clear();
            _pendingMine = 0;
            _fieldFocused = false;
            _filters = new ListingSearchRequest();
        }

        private void Toast(string reasonCode) => _toast.Show(reasonCode, false, _now(), _toastMs);

        /// <summary>The slot <paramref name="slot"/> of container <paramref name="container"/> in the bag now; null when empty.</summary>
        public static InventorySlotEntry? SlotOf(InventoryContents contents, byte normalBag, byte container, byte slot)
        {
            var slots = container == normalBag ? contents.Slots : null;
            if (slots == null)
            {
                foreach (var entry in contents.Containers)
                {
                    if (entry.Code == container)
                        slots = entry.Slots;
                }
            }
            if (slots == null)
                return null;
            foreach (var entry in slots)
            {
                if (entry.Slot == slot && entry.Count > 0)
                    return entry;
            }
            return null;
        }

        /// <summary>The Normal bag's stacks the Sell tab offers: tradable, not reserved (the server checks every rule again).</summary>
        public static List<InventorySlotEntry> Sellable(AuctionConfig config, InventoryContents? contents)
        {
            var result = new List<InventorySlotEntry>();
            if (contents == null)
                return result;
            foreach (var entry in contents.Slots)
            {
                if (entry.Count == 0 || entry.Reserved || entry.NonTradable || entry.Stolen || entry.Tainted)
                    continue;
                var def = config.Item(entry.ItemNetId);
                if (def == null || Array.IndexOf(AuctionConfig.Categories, def.Category) < 0)
                    continue;
                result.Add(entry);
            }
            return result;
        }

        /// <summary>"Demon Ash x4" (the item's <c>UiStrings</c> name; its net id when the tables lack it).</summary>
        public static string ItemCount(AuctionConfig config, UiText text, int itemNetId, int count)
        {
            var item = config.Item(itemNetId);
            string name = item == null ? "#" + NumberText.Of(itemNetId) : text.Get(item.NameId);
            return text.Get(AuctionTextIds.ItemCount, new TextArg("name", name), new TextArg("count", NumberText.Of(count)));
        }

        /// <summary>
        /// A row's item: "Forged Sword +7 x1 (Rare, quality 80)", with the enchant level on the name, the rarity above common and the
        /// quality (the inventory tooltip's texts).
        /// </summary>
        public static string ItemText(AuctionConfig config, UiText text, InventorySlotEntry item)
        {
            string line = ItemCount(config, text, item.ItemNetId, item.Count);
            if (item.Enchant > 0 && config.Item(item.ItemNetId) is { } def)
            {
                string name = text.Get(EnchantTextIds.TooltipName, new TextArg("name", text.Get(def.NameId)),
                    new TextArg("level", NumberText.Of(item.Enchant)));
                line = text.Get(AuctionTextIds.ItemCount, new TextArg("name", name), new TextArg("count", NumberText.Of(item.Count)));
            }
            var details = new List<string>();
            if (item.Rarity > 0)
            {
                string rarityId = config.Inventory.RarityTextId(item.Rarity);
                details.Add(rarityId.Length > 0 ? text.Get(rarityId) : NumberText.Of(item.Rarity));
            }
            if (item.Quality > 0)
                details.Add(text.Get(InventoryTextIds.TooltipQuality, new TextArg("quality", NumberText.Of(item.Quality))));
            return details.Count == 0 ? line : text.Get(AuctionTextIds.ItemDetails, new TextArg("item", line),
                new TextArg("details", string.Join(", ", details)));
        }
    }
}
