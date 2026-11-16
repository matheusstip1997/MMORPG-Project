#nullable enable
using System;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Forge;
using Game.UI.ViewModels.Hud;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.UI.Menus.Forge
{
    /// <summary>
    /// Connects the enchant tab (spec 5.3 R-5.3-04, 9.13; WP-M3-8) to the world client: a station's yes sets the station the tab
    /// lists for; the committed contents (<see cref="WorldClientFeed.InventoryReceived"/>) and the jobs
    /// (<see cref="WorldClientFeed.JobsReceived"/>) feed the item list and the stones held; an attempt goes out through
    /// <see cref="WorldClientFeed.EnchantRequests"/>, one at a time, and its answer comes back after the save. A refusal shows
    /// its <c>ReasonCodeStrings</c> text in the HUD toast, a result a news toast with the new level.
    /// </summary>
    public sealed class EnchantBinder : IEnchantPanelActions, IDisposable
    {
        private readonly UiText _text;
        private readonly EnchantPanelViewModel _panel;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private WorldClientFeed? _feed;

        public EnchantBinder(UiText text, EnchantPanelViewModel panel, ToastViewModel toast, int toastMs, Func<double> nowMs)
        {
            _text = text;
            _panel = panel;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
        }

        /// <summary>Whether contracts are unlocked at the own level (M4 decision 14): the banner offers none below it; null: always.</summary>
        public Func<bool>? ContractsUnlocked { get; set; }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.EnchantAnswered += OnAnswered;
            feed.InventoryReceived += OnInventory;
            feed.JobsReceived += OnJobs;
            feed.OutpostInteracted += OnInteracted;
            feed.ContractListReceived += OnContracts;
            feed.Disconnected += OnDisconnected;
            if (feed.Inventory != null) // they may have come before the HUD bound
                OnInventory(feed.Inventory);
            if (feed.JobsState != null)
                OnJobs(feed.JobsState);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.EnchantAnswered -= OnAnswered;
            _feed.InventoryReceived -= OnInventory;
            _feed.JobsReceived -= OnJobs;
            _feed.OutpostInteracted -= OnInteracted;
            _feed.ContractListReceived -= OnContracts;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void StepEnchantContract(int delta) => _panel.StepOffer(delta);

        public void WorkOnEnchantContract() => _panel.WorkOn();

        public void StopEnchantContract() => _panel.StopContract();

        /// <summary>The own contracts (WP-M4-10 part C): the banner offers the accepted enchant contracts this station enchants, from level 20.</summary>
        private void OnContracts(ContractList list)
        {
            bool unlocked = ContractsUnlocked?.Invoke() ?? true;
            _panel.SetContracts(unlocked && list.Reason == ContractReason.None ? list.Rows : Array.Empty<ContractEntry>());
        }

        public void Dispose() => Unbind();

        public void ChooseEnchant(int row)
        {
            if (_panel.PendingRequest == 0)
                _panel.Select(row);
        }

        public void ShowEnchantTab(bool active) => _panel.ShowTab(active);

        public void Enchant()
        {
            if (!_panel.CanEnchant || _panel.Chosen is not { } chosen || _feed?.EnchantRequests == null)
                return;
            uint id = _feed.EnchantRequests.Enchant(_panel.StationEntityId, chosen.Container, chosen.Slot, chosen.InstanceId,
                _panel.Contract?.ContractId ?? 0); // a contract's attempt names the contract, its item is in the escrow (WP-M4-10 part C)
            if (id != 0)
                _panel.Sent(id);
        }

        private void OnInteracted(OutpostInteractResult result)
        {
            if ((result.Kind != NpcInteraction.Forge && result.Kind != NpcInteraction.JewelerBench) || result.Reason != OutpostReason.None)
                return;
            _panel.OpenAt(result.NpcEntityId, result.Kind);
            if (_feed?.Contracts != null)
                OnContracts(_feed.Contracts); // the banner: this station's enchant contracts (WP-M4-10 part C)
        }

        private void OnAnswered(EnchantResult result)
        {
            bool mine = result.RequestId == _panel.PendingRequest && _panel.PendingRequest != 0;
            if (_panel.OnResult(result) is { } refused)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            if (!mine)
                return;
            string name = _panel.Rules?.Item(result.ItemNetId) is { } item ? _text.Get(item.NameId) : result.ItemNetId.ToString(CultureInfo.InvariantCulture);
            string id = result.ToLevel > result.FromLevel ? EnchantTextIds.ToastUp
                : result.ToLevel < result.FromLevel ? EnchantTextIds.ToastDown
                : EnchantTextIds.ToastSame;
            _toast.ShowText(id, "name", name, "level", result.ToLevel.ToString(CultureInfo.InvariantCulture), _now(), _toastMs);
        }

        private void OnInventory(InventoryContents contents) => _panel.SetContents(contents);

        private void OnJobs(JobState state) => _panel.SetJobs(state);

        private void OnDisconnected(string reasonCode) => _panel.Clear();
    }
}
