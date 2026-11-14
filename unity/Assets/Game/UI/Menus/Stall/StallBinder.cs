#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.Menus.Outpost;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Outpost;
using Game.UI.ViewModels.Stall;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Stall
{
    /// <summary>What the player does in the stall window; the binder turns it into requests (WP-M4-8).</summary>
    public interface IStallWindowActions
    {
        void SelectChoice(int index);
        void EditCount(string text);
        void EditPrice(string text);
        /// <summary>A count or price field took or lost focus: gameplay keys are off while one has it.</summary>
        void FocusField(bool focused);
        /// <summary>Open the stall (at a free place) or add the picked stack (at the own stall).</summary>
        void List();
        void SelectRow(long listingId);
        void EditBuyCount(string text);
        void Buy();
        void CancelListing(long listingId);
        void CloseStall();
        void CloseWindow();
    }

    /// <summary>
    /// Shop stalls on the HUD (spec 4.5, 9.13; M4 decision 7; WP-M4-8). The interaction key (<c>Hotkeys</c> row <c>interact</c>)
    /// on the stall in reach (<see cref="WorldClientFeed.StallTarget"/>) asks to look at it: a buyer gets its listings with Buy,
    /// the owner its listings with Cancel, Add and Close stall; on a free stall place (<see cref="WorldClientFeed.StallPlaceTarget"/>)
    /// it opens the window to open a stall there with one stack of the Normal bag (the user answer of 2026-11-13: one listing at
    /// the open, the others added one at a time). An NPC in reach keeps the key (<see cref="OutpostBinder"/>). The prompt
    /// (<c>stall_prompt</c>) shows over the stall, or over the own character at a free place, while the window is closed. The
    /// window closes, and the server is told, once the stall or place leaves reach or the stall closes. Every answer comes after
    /// its commit; a refusal toasts its <c>ReasonCodeStrings</c> text (the listing model's reason for <c>StallListingRefused</c>).
    /// Display time is the caller's clock in ms.
    /// </summary>
    public sealed class StallBinder : IStallWindowActions, IDisposable
    {
        private readonly StallConfig _config;
        private readonly UiText _text;
        private readonly StallWindowViewModel _window;
        private readonly NpcPromptViewModel _prompt;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _interact;
        private readonly string _keyName;
        private readonly List<StallChoice> _choices = new List<StallChoice>();
        private InventoryContents? _choicesFrom;
        private WorldClientFeed? _feed;
        private bool _fieldFocused;

        /// <param name="readInteractKey">False in tests: no Input System action is made for the interaction key.</param>
        public StallBinder(StallConfig config, UiText text, StallWindowViewModel window, NpcPromptViewModel prompt, ToastViewModel toast,
            int toastMs, Func<double> nowMs, bool readInteractKey = true)
        {
            _config = config;
            _text = text;
            _window = window;
            _prompt = prompt;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _keyName = OutpostBinder.KeyNameOf(config.InteractBinding);
            if (readInteractKey && config.InteractBinding.Length > 0)
            {
                _interact = new InputAction("Stall", InputActionType.Button, config.InteractBinding);
                _interact.Enable();
                _keyName = _interact.GetBindingDisplayString();
            }
        }

        public WorldClientFeed? Feed => _feed;

        public string KeyName => _keyName;

        /// <summary>
        /// Whether the left-edge windows are blocked (a trade is open; the HUD's <c>WindowGroup</c>); null: never. User decision
        /// 2026-11-14 ("Yes, one at a time"): the stall window opens nothing while a trade is open, and the group closes it when one opens.
        /// </summary>
        public Func<bool>? TradeOpen { get; set; }

        private bool TradeIsOpen => TradeOpen?.Invoke() ?? false;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.StallStateReceived += OnState;
            feed.StallAnswered += OnAnswered;
            feed.Disconnected += OnDisconnected;
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.StallStateReceived -= OnState;
            _feed.StallAnswered -= OnAnswered;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _interact?.Dispose();
        }

        /// <summary>
        /// Once per frame: the interaction key (ignored while a text field has focus or an NPC is in reach), the window closed
        /// when its stall or place left reach, the bag stacks for the owner, and the prompt.
        /// </summary>
        public void Frame(double nowMs)
        {
            if (_feed == null)
                return;
            if (_interact != null && _interact.WasPressedThisFrame() && !_feed.TextInputActive)
                Interact();
            if (_window.Open)
            {
                // A stall standing at the place the window opens one on (another player was faster, or its own spawn came before
                // the answer): the window turns to that stall once nothing waits.
                if (_window.Mode == StallWindowMode.Open && _feed.StallTarget != 0)
                {
                    if (_window.Pending == 0)
                        Interact();
                }
                else
                {
                    // The stall in reach, or (just opened by this player) its place before its entity arrived.
                    bool atPlace = _window.Place >= 0 && _feed.StallTarget == 0 && _feed.StallPlaceTarget == _window.Place;
                    bool inReach = _window.Mode == StallWindowMode.Open
                        ? atPlace || _window.Pending != 0
                        : _feed.StallTarget == _window.StallEntityId || atPlace;
                    if (!inReach)
                        CloseWindow();
                }
            }
            if (_window.Open && _window.Mode != StallWindowMode.Browse && !ReferenceEquals(_choicesFrom, _feed.Inventory))
                RefreshChoices();
            uint anchor = _feed.NpcTarget == 0 && !_window.Open ? _feed.StallPromptAnchor : 0;
            if (anchor == 0)
                _prompt.Set(0, "", _keyName);
            else
                _prompt.Set(anchor, PromptText(), _keyName);
        }

        /// <summary>The interaction key: look at the stall in reach, or open the window at the free place in reach.</summary>
        public void Interact()
        {
            if (_feed?.StallRequests == null || _feed.NpcTarget != 0 || TradeIsOpen)
                return;
            if (_feed.StallTarget != 0)
            {
                uint stall = _feed.StallTarget;
                _feed.Identities.TryGet(stall, out string owner);
                _window.ShowStall(stall, owner, IsOwn(stall), _config.ListingsPerStall);
                _feed.StallRequests.BrowseStall(stall);
                RefreshChoices();
            }
            else if (_feed.StallPlaceTarget >= 0)
            {
                _window.ShowPlace(_feed.StallPlaceTarget, _config.ListingsPerStall);
                RefreshChoices();
            }
        }

        public void SelectChoice(int index) => _window.Select(index);

        public void EditCount(string text) => _window.SetCountText(text);

        public void EditPrice(string text) => _window.SetPriceText(text);

        public void FocusField(bool focused)
        {
            _fieldFocused = focused;
            if (_feed != null)
                _feed.WindowTextInput = focused && _window.Open;
        }

        public void List()
        {
            var requests = _feed?.StallRequests;
            if (requests == null || !_window.CanList || !_window.TryCount(out int count) || !_window.TryPrice(out long price))
                return;
            var choice = _window.Choices[_window.Selected];
            uint id = _window.Mode == StallWindowMode.Open
                ? requests.OpenStall(_window.Place, choice.Container, choice.Slot, count, price)
                : requests.AddToStall(_window.StallEntityId, choice.Container, choice.Slot, count, price);
            if (id != 0)
                _window.SetPending(id);
        }

        /// <summary>A buyer chooses a row (a second click clears it); the count starts at 1.</summary>
        public void SelectRow(long listingId)
        {
            if (_window.Mode == StallWindowMode.Browse)
                _window.SelectBuy(listingId);
        }

        public void EditBuyCount(string text) => _window.SetBuyCount(text);

        /// <summary>
        /// Buys the chosen count of the chosen row at the unit price shown (user decision 2026-11-14, "Choose the count"); the server
        /// takes any count up to the units left and refuses a changed price.
        /// </summary>
        public void Buy()
        {
            var requests = _feed?.StallRequests;
            if (requests == null || !_window.CanBuy)
                return;
            var row = _window.BuySelectedRow!;
            uint id = requests.BuyFromStall(_window.StallEntityId, row.ListingId, _window.BuyCount, row.UnitPrice);
            if (id != 0)
                _window.SetPending(id);
        }

        public void CancelListing(long listingId)
        {
            var requests = _feed?.StallRequests;
            if (requests == null || _window.Mode != StallWindowMode.Manage || _window.Pending != 0)
                return;
            uint id = requests.CancelStallListing(_window.StallEntityId, listingId);
            if (id != 0)
                _window.SetPending(id);
        }

        public void CloseStall()
        {
            var requests = _feed?.StallRequests;
            if (requests == null || _window.Mode != StallWindowMode.Manage || _window.Pending != 0)
                return;
            uint id = requests.CloseStall(_window.StallEntityId);
            if (id != 0)
                _window.SetPending(id);
        }

        public void CloseWindow()
        {
            if (!_window.Open)
                return;
            if (_window.StallEntityId != 0)
                _feed?.StallRequests?.LeaveStall(_window.StallEntityId);
            _window.Close();
            if (_fieldFocused)
                FocusField(false); // the fields hid with the window: their focus may never report a loss
        }

        /// <summary>The own stall: the one the open's answer named, or the one standing on the own stall's place.</summary>
        private bool IsOwn(uint stall)
        {
            var stalls = _feed?.Stalls;
            if (stalls == null)
                return false;
            if (stalls.OwnStallId != 0)
                return stalls.OwnStallId == stall;
            return stalls.OwnPlace >= 0 && stalls.StateOf(stall)?.Own == true;
        }

        private string PromptText()
        {
            if (_feed!.StallTarget == 0)
                return StallTextIds.PromptOpen;
            return IsOwn(_feed.StallTarget) ? StallTextIds.PromptManage : StallTextIds.PromptBrowse;
        }

        /// <summary>
        /// The Normal bag's stacks the owner may list: the auction's filter (<see cref="ListingItems.Sellable"/>: not reserved,
        /// tradable, not stolen or tainted, never sell-only), so the picker never offers a stack the server always refuses.
        /// </summary>
        private void RefreshChoices()
        {
            var contents = _feed?.Inventory;
            _choicesFrom = contents;
            _choices.Clear();
            if (contents != null)
            {
                foreach (var slot in ListingItems.Sellable(_config.Inventory, contents))
                    _choices.Add(new StallChoice(_config.Inventory.NormalBagCode, slot.Slot, slot.ItemNetId, slot.Count, slot));
            }
            _window.SetChoices(_choices.ToArray());
        }

        private void OnState(StallState state)
        {
            if (_window.Open && state.StallEntityId == _window.StallEntityId)
            {
                if (!state.Open)
                {
                    _toast.ShowText(StallTextIds.Closed, "", "", _now(), _toastMs);
                    CloseWindow();
                    return;
                }
                _window.SetState(state);
            }
        }

        private void OnAnswered(StallResult result)
        {
            if (result.RequestId != 0 && result.RequestId == _window.Pending)
                _window.SetPending(0);
            if (result.Reason != StallReason.None)
            {
                _toast.Show(result.Reason == StallReason.StallListingRefused ? result.ListingReason.ToString() : result.Reason.ToString(), false,
                    _now(), _toastMs);
                return;
            }
            string name = ItemName(result.ListingId);
            switch (result.Action)
            {
                case StallAction.Open:
                case StallAction.Add:
                    _window.ClearCount(); // written back into the field even if it has the focus
                    _toast.ShowText(StallTextIds.ToastListed, "name", name, "count", Number(result.Count), _now(), _toastMs);
                    if (result.Action == StallAction.Open && result.StallEntityId != 0 && _window.Open)
                    {
                        _window.ShowStall(result.StallEntityId, _feed?.Identities.TryGet(result.StallEntityId, out string own) == true ? own : "", true,
                            _config.ListingsPerStall, _window.Place);
                        _feed?.StallRequests?.BrowseStall(result.StallEntityId);
                    }
                    break;
                case StallAction.Buy:
                    _toast.ShowText(StallTextIds.ToastBought, "name", name, "count", Number(result.Count), _now(), _toastMs);
                    break;
                case StallAction.Cancel:
                    _toast.ShowText(StallTextIds.ToastCancelled, "name", name, "", "", _now(), _toastMs);
                    break;
                case StallAction.Close:
                    _toast.ShowText(StallTextIds.ToastClosed, "", "", _now(), _toastMs);
                    CloseWindow();
                    break;
            }
            RefreshChoices();
        }

        /// <summary>The name of the item of a listing shown in the window (empty when it is no longer there).</summary>
        private string ItemName(long listingId)
        {
            foreach (var row in _window.Rows)
            {
                if (row.ListingId == listingId && _config.Inventory.Item(row.Item.ItemNetId) is { } item)
                    return _text.Get(item.Def.NameId);
            }
            if (_window.Selected >= 0 && _config.Inventory.Item(_window.Choices[_window.Selected].ItemNetId) is { } picked)
                return _text.Get(picked.Def.NameId);
            return "";
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Close();
            _fieldFocused = false;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
