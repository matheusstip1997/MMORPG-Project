#nullable enable
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.UI.ViewModels.Forge
{
    /// <summary>
    /// The enchant tab of the station window (spec 5.3 R-5.3-04, 9.13; WP-M3-8): whether it is the shown tab, the station it
    /// was opened at, the items of the character's jobs that enchant there (Equipped first, then the Safe bag's equipment
    /// area) with their committed level, the chosen one, the stones held and the request waiting for its answer. It only
    /// describes; the server checks the attempt and rolls the outcome.
    /// </summary>
    public sealed class EnchantPanelViewModel : ViewModel
    {
        private readonly ClientEnchant _core;
        private readonly List<EnchantCandidate> _candidates = new List<EnchantCandidate>();
        private readonly List<ContractEntry> _offers = new List<ContractEntry>();
        private InventoryContents? _contents;
        private JobState? _jobs;
        private long _selectedInstance;

        // ---- Enchant contracts (spec 5.4, WP-M4-10 part C): the crafter's accepted enchant contracts whose item enchants here ----

        /// <summary>The own accepted enchant contracts as crafter whose item this station enchants (the banner steps through them).</summary>
        public IReadOnlyList<ContractEntry> ContractOffers => _offers;

        /// <summary>The offer the banner shows (an index into <see cref="ContractOffers"/>).</summary>
        public int ContractOffer { get; private set; }

        /// <summary>The contract worked on: the list shows its escrowed item only and Enchant sends its id; null for the own items.</summary>
        public ContractEntry? Contract { get; private set; }

        /// <summary>The worked-on contract's customer supplies the stones (they are in its escrow): none are needed from the bag.</summary>
        public bool CustomerStones => Contract is { } contract && contract.Slots.Length > Shared.Simulation.Contracts.ContractRules.EnchantStonesSlot
            && contract.Slots[Shared.Simulation.Contracts.ContractRules.EnchantStonesSlot].Supplier == ContractSupplier.Customer;

        /// <summary>
        /// The own contracts as last pushed (<c>ContractList</c>), or none below level 20: the banner offers the crafter's accepted
        /// enchant contracts whose item this station enchants. A contract that left the list stops (unless its attempt is waiting).
        /// </summary>
        public void SetContracts(IReadOnlyList<ContractEntry> mine)
        {
            long shown = ContractOffer >= 0 && ContractOffer < _offers.Count ? _offers[ContractOffer].ContractId : 0;
            _offers.Clear();
            var rules = Rules;
            foreach (var entry in mine)
            {
                if (entry.Role == ContractRole.Crafter && entry.Status == ContractStatus.Accepted && entry.Kind == ForgeKind.Enchant
                    && rules?.Item(entry.DefNetId) is { } item && rules.StationOf(rules.JobOf(item)) == Station)
                    _offers.Add(entry);
            }
            int at = _offers.FindIndex(e => e.ContractId == shown);
            ContractOffer = at >= 0 ? at : 0;
            if (Contract != null)
            {
                int working = _offers.FindIndex(e => e.ContractId == Contract.ContractId);
                if (working >= 0)
                    Contract = _offers[working];
                else if (PendingRequest == 0)
                    Contract = null;
            }
            Rebuild();
            NotifyChanged();
        }

        /// <summary>The banner's previous or next offer.</summary>
        public void StepOffer(int delta)
        {
            if (_offers.Count == 0 || Contract != null)
                return;
            ContractOffer = ((ContractOffer + delta) % _offers.Count + _offers.Count) % _offers.Count;
            NotifyChanged();
        }

        /// <summary>Works on the offer shown: the list shows its escrowed item, Enchant sends the contract's id.</summary>
        public void WorkOn()
        {
            if (PendingRequest != 0 || ContractOffer < 0 || ContractOffer >= _offers.Count)
                return;
            Contract = _offers[ContractOffer];
            Rebuild();
            NotifyChanged();
        }

        /// <summary>Back to the own items.</summary>
        public void StopContract()
        {
            if (Contract == null || PendingRequest != 0)
                return;
            Contract = null;
            Rebuild();
            NotifyChanged();
        }

        public EnchantPanelViewModel(EnchantRules? rules)
        {
            _core = new ClientEnchant { Rules = rules };
        }

        public EnchantRules? Rules => _core.Rules;

        /// <summary>The enchant tab is the one shown (the forge tab otherwise).</summary>
        public bool Active { get; private set; }

        public uint StationEntityId { get; private set; }

        public NpcInteraction Station { get; private set; }

        public IReadOnlyList<EnchantCandidate> Candidates => _candidates;

        /// <summary>The chosen item (index into <see cref="Candidates"/>), -1 for none; it follows the instance across refreshes.</summary>
        public int Selected { get; private set; } = -1;

        public EnchantCandidate? Chosen => Selected >= 0 && Selected < _candidates.Count ? _candidates[Selected] : (EnchantCandidate?)null;

        /// <summary>Unreserved stones of the chosen item's kind in the Normal bag.</summary>
        public int StonesHeld => Chosen is { } chosen ? _core.StonesHeld(_contents, chosen.Stone) : 0;

        public uint PendingRequest { get; private set; }

        public bool CanEnchant => Chosen is { Maxed: false } chosen && PendingRequest == 0 && (CustomerStones || StonesHeld >= chosen.StoneCount);

        /// <summary>The odds of the chosen item's level from the table (the job tree's bonus is the server's).</summary>
        public EnchantOdds? Odds => Chosen is { } chosen ? Rules?.Odds(chosen.Level) : null;

        /// <summary>The station window opened at a station: the tab lists what enchants there.</summary>
        public void OpenAt(uint stationEntityId, NpcInteraction station)
        {
            StationEntityId = stationEntityId;
            Station = station;
            Active = false; // the window opens on the forge tab
            PendingRequest = 0;
            Contract = null; // the binder's SetContracts offers this station's contracts again
            _offers.Clear();
            Rebuild();
            NotifyChanged();
        }

        public void ShowTab(bool active)
        {
            if (Active == active)
                return;
            Active = active;
            NotifyChanged();
        }

        public void Close()
        {
            Active = false;
            PendingRequest = 0;
            NotifyChanged();
        }

        public void SetContents(InventoryContents contents)
        {
            _contents = contents;
            Rebuild();
            NotifyChanged();
        }

        public void SetJobs(JobState state)
        {
            _jobs = state;
            Rebuild();
            NotifyChanged();
        }

        public void Select(int index)
        {
            if (index < 0 || index >= _candidates.Count)
                return;
            Selected = index;
            _selectedInstance = _candidates[index].InstanceId;
            NotifyChanged();
        }

        public void Sent(uint requestId)
        {
            PendingRequest = requestId;
            NotifyChanged();
        }

        /// <summary>
        /// Takes an answer: the reason code of a refusal to the waiting request (for the toast), else null. A result clears the
        /// wait; the new level shows with the contents that follow it.
        /// </summary>
        public string? OnResult(EnchantResult result)
        {
            if (result.RequestId != PendingRequest || PendingRequest == 0)
                return null;
            PendingRequest = 0;
            NotifyChanged();
            return result.Reason == EnchantReason.None ? null : result.Reason.ToString();
        }

        public void Clear()
        {
            Active = false;
            PendingRequest = 0;
            _contents = null;
            _jobs = null;
            _candidates.Clear();
            Selected = -1;
            _selectedInstance = 0;
            Contract = null;
            _offers.Clear();
            NotifyChanged();
        }

        private void Rebuild()
        {
            var jobs = _jobs;
            var rules = Rules;
            if (Contract != null)
            {
                // The contract's item is in its escrow, never in the bag: one row from the terms (its level at the proposal).
                _candidates.Clear();
                var slot = Contract.Slots.Length > 0 ? Contract.Slots[Shared.Simulation.Contracts.ContractRules.EnchantItemSlot] : null;
                if (slot != null && rules?.Item(slot.ItemNetId) is { } item && rules.TryMaterial(item, out var stone, out int count))
                    _candidates.Add(new EnchantCandidate(0, 0, slot.InstanceId, item, slot.EnchantLevel, stone, count));
                Selected = _candidates.Count > 0 ? 0 : -1;
                return;
            }
            _core.Candidates(_contents, Station, job => rules != null && rules.Holds(jobs, job), _candidates);
            Selected = -1;
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (_candidates[i].InstanceId == _selectedInstance)
                    Selected = i;
            }
            if (Selected < 0 && _candidates.Count > 0)
            {
                Selected = 0;
                _selectedInstance = _candidates[0].InstanceId;
            }
        }
    }
}
