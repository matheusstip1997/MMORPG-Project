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
    /// the tab opens; Pin (a crafted instance in any of the player's containers, the server refuses one the player did not craft:
/// <c>PortfolioNotCrafter</c>),
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
        private readonly List<byte> _candidateContainers = new List<byte>(); // the container of each candidate, in order
        private WorldClientFeed? _feed;
        private InventoryContents? _contents;
        private byte _normalBag = 1;

        public PortfolioBinder(OwnPortfolioViewModel model, ToastViewModel toast, int toastMs, Func<double> nowMs, byte normalBag)
        {
            _model = model;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            _normalBag = normalBag;
        }

        /// <summary>
        /// The own crafted instances (crafted by <see cref="WorldClientFeed.OwnCharacterId"/>; any crafter while it is 0, the dev scene
        /// without the login) of the Normal bag, then of every container in the order sent (the pin picker's rows).
        /// QA M4 wave 2: the picker listed the Normal bag only, which holds no instances (crafted gear lands in the Safe bag's
        /// equipment area, and a worn piece in the equipped container), so a player could never pin anything.
        /// </summary>
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

        public void OpenPinPicker(int showcaseSlot)
        {
            RebuildCandidates(); // the own id may have come after the bag
            _model.OpenPicker(showcaseSlot);
        }

        public void PickPin(int row)
        {
            int slot = _model.PinTarget;
            if (slot < 0 || row < 0 || row >= _candidates.Count)
                return;
            var entry = _candidates[row];
            _model.ClosePicker();
            Send(new PortfolioRequest
            {
                Action = PortfolioAction.Pin, Container = _candidateContainers[row], Slot = entry.Slot, InstanceId = entry.InstanceId, ShowcaseSlot = (byte)slot,
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
            _contents = contents;
            RebuildCandidates();
        }

        private void RebuildCandidates()
        {
            _candidates.Clear();
            _candidateContainers.Clear();
            if (_contents == null)
                return;
            AddCandidates(_normalBag, _contents.Slots);
            foreach (var container in _contents.Containers ?? Array.Empty<InventoryContainerEntry>())
                AddCandidates(container.Code, container.Slots);
        }

        private void AddCandidates(byte container, InventorySlotEntry[]? slots)
        {
            // Review of #279: only the own crafts (the server refuses another's: PortfolioNotCrafter).
            long own = _feed?.OwnCharacterId ?? 0;
            foreach (var entry in slots ?? Array.Empty<InventorySlotEntry>())
            {
                if (entry.InstanceId != 0 && entry.Count > 0 && entry.CrafterId != 0 && (own == 0 || entry.CrafterId == own))
                {
                    _candidates.Add(entry);
                    _candidateContainers.Add(container);
                }
            }
        }

        private void OnDisconnected(string reasonCode) => _model.Clear();
    }
}
