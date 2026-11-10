#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Trade;
using Shared.Protocol;

namespace Game.UI.Menus.Trade
{
    /// <summary>What the player does in the trade window and the invite prompt; the binder turns it into requests (WP-M4-4).</summary>
    public interface ITradeWindowActions
    {
        void Accept();

        void Decline();

        void Cancel();

        void Lock();

        void Confirm();

        void RemoveOffer(int index);

        /// <summary>The player typed in the gold field (Lock and Confirm wait until the typed amount is set).</summary>
        void EditGold(string text);

        /// <summary>The gold field gained or lost focus: while it has it the gameplay keys are suspended (typing 1 is no hotbar key).</summary>
        void FocusGold(bool focused);

        /// <summary>The gold field's text; ignored unless it is a whole number of zero or more.</summary>
        void SetGold(string text);

        /// <summary>A stack or instance dropped from the inventory window onto the own offer: the whole stack is offered.</summary>
        void DropOffer(byte container, InventorySlotEntry entry);
    }

    /// <summary>
    /// Connects the trade window and the invite prompt (spec 4.5 "M4 trade window", 9.13; WP-M4-4) to the world client: every
    /// <see cref="WorldClientFeed.TradeChanged"/> updates both (the window is open while the server says Open or Settling); the
    /// end of a trade shows a toast (done, or the <c>TradeReason</c> it ended with); refusals of
    /// <see cref="WorldClientFeed.TradeAnswered"/> show their reason code. Requests go out through
    /// <see cref="WorldClientFeed.TradeRequests"/>, lock and confirm with the revision this client saw. When the window opens it
    /// opens the inventory window so the player can drag items in.
    /// </summary>
    public sealed class TradeBinder : ITradeWindowActions, IDisposable
    {
        private readonly TradeWindowViewModel _window;
        private readonly TradeInviteViewModel _invite;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;
        private uint _lastEndedTrade;

        public TradeBinder(TradeWindowViewModel window, TradeInviteViewModel invite, ToastViewModel toast, int toastMs, Func<double> nowMs)
        {
            _window = window;
            _invite = invite;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        /// <summary>Whether the inventory window is open, and how to open it (set by the HUD; the forge's pattern).</summary>
        public Func<bool>? InventoryOpen { get; set; }

        public Action<bool>? SetInventoryOpen { get; set; }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.TradeChanged += OnState;
            feed.TradeAnswered += OnAnswered;
            feed.Disconnected += OnDisconnected;
            if (feed.Trade?.State is { } state) // it may have come before the HUD bound
            {
                if (state.Phase is TradePhase.Done or TradePhase.Cancelled)
                    _lastEndedTrade = state.TradeId; // ended before this binder: its toast was shown (or missed) already
                OnState(state);
            }
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.TradeChanged -= OnState;
            _feed.TradeAnswered -= OnAnswered;
            _feed.Disconnected -= OnDisconnected;
            _feed.WindowTextInput = false;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>Once per frame: the countdowns.</summary>
        public void Frame(double nowMs)
        {
            _window.Tick(nowMs);
            _invite.Tick(nowMs);
        }

        public void Accept()
        {
            _invite.Hide();
            Send(TradeAction.Accept);
        }

        public void Decline()
        {
            _invite.Hide();
            Send(TradeAction.Decline);
        }

        public void Cancel()
        {
            _invite.Hide();
            Send(TradeAction.Cancel);
        }

        public void Lock()
        {
            if (_window.CanLock)
                Send(TradeAction.Lock, track: true);
        }

        public void Confirm()
        {
            if (_window.CanConfirm)
                Send(TradeAction.Confirm, track: true);
        }

        public void RemoveOffer(int index)
        {
            if (_window.CanEdit && index >= 0 && index < _window.OwnOffer.Length)
                Send(TradeAction.Remove, track: true, offerIndex: (byte)index);
        }

        public void EditGold(string text) => _window.EditGold(text);

        public void FocusGold(bool focused)
        {
            if (_feed != null)
                _feed.WindowTextInput = focused && _window.Open;
        }

        public void SetGold(string text)
        {
            if (!_window.CanEdit || !long.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out long gold) || gold == _window.OwnGold)
                return;
            Send(TradeAction.SetGold, track: true, gold: gold);
        }

        public void DropOffer(byte container, InventorySlotEntry entry)
        {
            if (!_window.CanEdit || entry.Count == 0)
                return;
            Send(TradeAction.Add, track: true, container: container, slot: entry.Slot, count: entry.InstanceId != 0 ? (ushort)1 : entry.Count);
        }

        private void Send(TradeAction action, bool track = false, byte container = 0, ushort slot = 0, ushort count = 0,
            byte offerIndex = 0, long gold = 0)
        {
            if (_feed?.TradeRequests is not { } requests)
                return;
            uint id = requests.RequestTrade(action, 0, container, slot, count, offerIndex, gold);
            if (id != 0 && track)
                _window.Sent(id);
        }

        private void OnState(TradeState state)
        {
            double now = _now();
            bool wasOpen = _window.Open;
            _window.Apply(state, now);
            _invite.Apply(state, now);
            if (!wasOpen && _window.Open && InventoryOpen != null && SetInventoryOpen != null && !InventoryOpen())
                SetInventoryOpen(true);
            if (wasOpen && !_window.Open && _feed != null)
                _feed.WindowTextInput = false; // the field hid with the window: its focus may never report a loss
            if (state.Phase is TradePhase.Done or TradePhase.Cancelled && state.TradeId != _lastEndedTrade)
            {
                _lastEndedTrade = state.TradeId;
                if (state.Phase == TradePhase.Done)
                    _toast.ShowText(TradeTextIds.ToastDone, "name", state.PartnerName, _now(), _toastMs);
                else if (state.Reason != TradeReason.None)
                    _toast.Show(state.Reason.ToString(), false, _now(), _toastMs);
            }
        }

        private void OnAnswered(TradeResult result)
        {
            if (_window.OnResult(result) is { } refused)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            // A refused invite or answer is not tracked by the window (no trade yet): its reason is shown too.
            if (result.Reason != TradeReason.None && result.Action is TradeAction.Invite or TradeAction.Accept or TradeAction.Decline
                    or TradeAction.Cancel)
                _toast.Show(result.Reason.ToString(), false, _now(), _toastMs);
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Clear();
            _invite.Clear();
        }
    }
}
