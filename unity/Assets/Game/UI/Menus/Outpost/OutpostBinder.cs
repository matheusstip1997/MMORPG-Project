#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using Shared.Data.Tables;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Outpost
{
    /// <summary>What the player does in the outpost sale window; the binder turns it into requests (WP-M2-4).</summary>
    public interface IOutpostWindowActions
    {
        void SelectStack(byte slot);
        void Step(int delta);
        void SelectAll();
        void Sell();
        void Close();
    }

    /// <summary>What the player does in the vendor window.</summary>
    public interface IVendorWindowActions
    {
        void SelectStock(int index);
        void StepCount(int delta);
        void Buy();
        void CloseVendor();
    }

    /// <summary>
    /// Connects the outpost and vendor windows, the NPC prompt and the gold display (spec 4.2, 9.13 M2; WP-M2-4) to the world
    /// client. The interaction key (<c>Hotkeys</c> row <c>interact</c>) asks the server to open the first interaction of
    /// the NPC in reach (<see cref="WorldClientFeed.NpcTarget"/>); the server's yes opens its window. In the sale window a
    /// selection asks for a quote, a confirm sells at that quote, and a <c>QuoteExpired</c> asks again; the vendor window
    /// buys at the fixed price. Refusals show their <c>ReasonCodeStrings</c> text in the HUD toast. A window closes when
    /// its NPC is no longer in reach, or on its close button. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class OutpostBinder : IOutpostWindowActions, IVendorWindowActions, IDisposable
    {
        private readonly OutpostConfig _config;
        private readonly OutpostWindowViewModel _sell;
        private readonly VendorWindowViewModel _vendor;
        private readonly NpcPromptViewModel _prompt;
        private readonly GoldViewModel _gold;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _interact;
        private readonly string _keyName;
        private WorldClientFeed? _feed;
        private uint _pendingInteract;
        private uint _pendingInteractNpc;

        /// <param name="readInteractKey">False in tests: no Input System action is made for the interaction key.</param>
        public OutpostBinder(OutpostConfig config, OutpostWindowViewModel sell, VendorWindowViewModel vendor, NpcPromptViewModel prompt,
            GoldViewModel gold, ToastViewModel toast, int toastMs, Func<double> nowMs, bool readInteractKey = true)
        {
            _config = config;
            _sell = sell;
            _vendor = vendor;
            _prompt = prompt;
            _gold = gold;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _keyName = KeyNameOf(config.InteractBinding);
            if (readInteractKey && config.InteractBinding.Length > 0)
            {
                _interact = new InputAction("Interact", InputActionType.Button, config.InteractBinding);
                _interact.Enable();
                _keyName = _interact.GetBindingDisplayString();
            }
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>The interaction key as the player reads it ("T").</summary>
        public string KeyName => _keyName;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.OutpostInteracted += OnInteracted;
            feed.SellQuoted += OnQuoted;
            feed.SellAnswered += OnSold;
            feed.PurchaseAnswered += OnPurchased;
            feed.GoldReceived += OnGold;
            feed.InventoryReceived += OnContents;
            feed.Disconnected += OnDisconnected;
            _gold.Set(feed.Gold);
            if (feed.Inventory != null)
                OnContents(feed.Inventory);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.SellQuoted -= OnQuoted;
            _feed.SellAnswered -= OnSold;
            _feed.PurchaseAnswered -= OnPurchased;
            _feed.GoldReceived -= OnGold;
            _feed.InventoryReceived -= OnContents;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _interact?.Dispose();
        }

        /// <summary>
        /// Once per frame: the interaction key (ignored while the chat input has focus), the prompt, and closing a window
        /// whose NPC left the reach.
        /// </summary>
        public void Frame(double nowMs)
        {
            if (_feed == null)
                return;
            if (_interact != null && _interact.WasPressedThisFrame() && !_feed.TextInputActive)
                Interact();
            uint target = _feed.NpcTarget;
            if (_sell.Open && _sell.NpcEntityId != target)
                _sell.Close();
            if (_vendor.Open && _vendor.NpcEntityId != target)
                _vendor.Close();
            var npc = target == 0 ? null : _config.Npc(_feed.NpcTargetDefId);
            bool windowOpen = (_sell.Open && _sell.NpcEntityId == target) || (_vendor.Open && _vendor.NpcEntityId == target);
            _prompt.Set(npc == null || windowOpen ? 0 : target, npc == null ? "" : OutpostTextIds.PromptOf(npc.Kind), _keyName);
        }

        /// <summary>The interaction key: open the first interaction of the NPC in reach (the server checks the reach again).</summary>
        public void Interact()
        {
            if (_feed?.Requests == null || _feed.NpcTarget == 0 || _config.Npc(_feed.NpcTargetDefId) is not { } npc
                || npc.Interactions.Length == 0)
                return;
            uint requestId = _feed.Requests.RequestOutpostInteract(_feed.NpcTarget, npc.Interactions[0]);
            if (requestId != 0)
            {
                _pendingInteract = requestId;
                _pendingInteractNpc = _feed.NpcTarget;
            }
        }

        public void SelectStack(byte slot)
        {
            if (_sell.Select(slot))
                RequestQuote();
        }

        public void Step(int delta)
        {
            if (_sell.Step(delta))
                RequestQuote();
        }

        public void SelectAll()
        {
            if (_sell.SelectAll())
                RequestQuote();
        }

        public void Sell()
        {
            var intent = _sell.Confirm();
            if (intent == null || _feed?.Requests == null)
                return;
            var sale = intent.Value;
            uint requestId = _feed.Requests.RequestSell(_sell.NpcEntityId, sale.Slot, sale.Count, sale.QuotedTotal);
            if (requestId != 0)
                _sell.SaleSent(requestId);
        }

        public void Close() => _sell.Close();

        public void SelectStock(int index) => _vendor.Select(index);

        public void StepCount(int delta) => _vendor.Step(delta);

        public void Buy()
        {
            var intent = _vendor.Confirm();
            if (intent == null || _feed?.Requests == null)
                return;
            var buy = intent.Value;
            uint requestId = _feed.Requests.RequestVendorBuy(_vendor.NpcEntityId, buy.ItemNetId, buy.Count, buy.Total);
            if (requestId != 0)
                _vendor.PurchaseSent(requestId);
        }

        public void CloseVendor() => _vendor.Close();

        private void RequestQuote()
        {
            if (_feed?.Requests == null || !_sell.TryQuoteRequest(out byte slot, out ushort count))
                return;
            uint requestId = _feed.Requests.RequestSellQuote(_sell.NpcEntityId, slot, count);
            if (requestId != 0)
                _sell.QuoteSent(requestId);
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if (result.RequestId != _pendingInteract)
                return;
            _pendingInteract = 0;
            if (result.Reason != OutpostReason.None)
            {
                Toast(result.Reason.ToString());
                return;
            }
            switch (result.Kind)
            {
                case NpcInteraction.Sell:
                    _vendor.Close();
                    _sell.OpenFor(_pendingInteractNpc);
                    if (_feed?.Inventory != null)
                        _sell.SetContents(_feed.Inventory, _config.IsSellable);
                    break;
                case NpcInteraction.Buy:
                    _sell.Close();
                    var npc = _feed == null ? null : _config.Npc(_feed.NpcTargetDefId);
                    _vendor.OpenFor(_pendingInteractNpc, npc == null ? Array.Empty<StockEntry>() : _config.Stock(npc.Id));
                    break;
            }
        }

        private void OnQuoted(SellQuoteResult quote)
        {
            if (_sell.OnQuote(quote) is { } refused)
                Toast(refused);
        }

        private void OnSold(SellResult result)
        {
            if (_sell.OnSale(result) is not { } refused)
                return;
            Toast(refused);
            if (result.Reason == OutpostReason.QuoteExpired)
                RequestQuote(); // the price changed: show the new one
        }

        private void OnPurchased(VendorBuyResult result)
        {
            if (_vendor.OnPurchase(result) is { } refused)
                Toast(refused);
        }

        private void OnGold(long gold) => _gold.Set(gold);

        private void OnContents(InventoryContents contents)
        {
            if (_sell.SetContents(contents, _config.IsSellable))
                RequestQuote();
        }

        private void OnDisconnected(string reasonCode)
        {
            _pendingInteract = 0;
            _sell.Clear();
            _vendor.Clear();
            _prompt.Clear();
            _gold.Set(null);
        }

        private void Toast(string reasonCode) => _toast.Show(reasonCode, false, _now(), _toastMs);

        /// <summary>The key name of a control path without the Input System ("&lt;Keyboard&gt;/t" is "T"), for tests.</summary>
        public static string KeyNameOf(string binding)
        {
            int slash = binding.LastIndexOf('/');
            return slash < 0 || slash == binding.Length - 1 ? binding : binding.Substring(slash + 1).ToUpperInvariant();
        }
    }
}
