#nullable enable
using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.UI.ViewModels.Contracts;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;

namespace Game.UI.Menus.Contracts
{
    /// <summary>
    /// The own portfolio of the Jobs window's portfolio tab (spec 5.4 R-5.4-04, M4 decision 11; WP-M4-10 part B): a view is read when
    /// the tab opens; Pin (an instance of the Normal bag, the server refuses one the player did not craft: <c>PortfolioNotCrafter</c>),
    /// Unpin and the hide flag are sent through <see cref="WorldClientFeed.ContractRequests"/>, one at a time, each answered by a fresh
    /// state after its commit. A refusal shows its <c>ReasonCodeStrings</c> text in the HUD toast. The server decides everything.
    /// </summary>
    public sealed class PortfolioBinder : IPortfolioEditor, IDisposable
    {
        private readonly OwnPortfolioViewModel _model;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly List<InventorySlotEntry> _candidates = new List<InventorySlotEntry>();
        private WorldClientFeed? _feed;
        private byte _normalBag = 1;

        public PortfolioBinder(OwnPortfolioViewModel model, ToastViewModel toast, int toastMs, Func<double> nowMs, byte normalBag)
        {
            _model = model;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _normalBag = normalBag;
        }

        /// <summary>The Normal bag's instances (the pin picker's rows), as last sent.</summary>
        public IReadOnlyList<InventorySlotEntry> PinCandidates => _candidates;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.PortfolioReceived += OnState;
            feed.InventoryReceived += OnContents;
            feed.Disconnected += OnDisconnected;
            if (feed.Inventory != null)
                OnContents(feed.Inventory);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.PortfolioReceived -= OnState;
            _feed.InventoryReceived -= OnContents;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose() => Unbind();

        /// <summary>The tab opened: the own portfolio is read again (a craft may have counted since).</summary>
        public void Request() => Send(new PortfolioRequest { Action = PortfolioAction.View });

        public void OpenPinPicker(int showcaseSlot) => _model.OpenPicker(showcaseSlot);

        public void PickPin(int row)
        {
            int slot = _model.PinTarget;
            if (slot < 0 || row < 0 || row >= _candidates.Count)
                return;
            var entry = _candidates[row];
            _model.ClosePicker();
            Send(new PortfolioRequest
            {
                Action = PortfolioAction.Pin, Container = _normalBag, Slot = entry.Slot, InstanceId = entry.InstanceId, ShowcaseSlot = (byte)slot,
            });
        }

        public void Unpin(int showcaseSlot) => Send(new PortfolioRequest { Action = PortfolioAction.Unpin, ShowcaseSlot = (byte)showcaseSlot });

        public void SetHidden(bool hidden) => Send(new PortfolioRequest { Action = PortfolioAction.SetHidden, Hidden = hidden });

        private void Send(PortfolioRequest request)
        {
            if (_model.Pending != 0)
                return;
            uint id = _feed?.ContractRequests?.RequestPortfolio(request) ?? 0;
            if (id != 0)
                _model.Sent(id);
        }

        private void OnState(PortfolioState state)
        {
            if (_model.Answer(state, out string? refusal) && refusal != null)
                _toast.Show(refusal, false, _now(), _toastMs);
        }

        private void OnContents(InventoryContents contents)
        {
            _candidates.Clear();
            foreach (var entry in contents.Slots)
            {
                if (entry.InstanceId != 0 && entry.Count > 0)
                    _candidates.Add(entry);
            }
        }

        private void OnDisconnected(string reasonCode) => _model.Clear();
    }
}
