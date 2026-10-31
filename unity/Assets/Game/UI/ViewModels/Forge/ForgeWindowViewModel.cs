#nullable enable
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.UI.ViewModels.Forge
{
    /// <summary>
    /// The forge window's state (spec 5.3, 9.13; WP-M3-6): the station it was opened at, the molds and recipes of that station,
    /// the chosen one with a bag stack per input (picked from the Normal bag by the slot's tags and tier, the next candidate on
    /// request), the session the server last sent (actions, the quality range) and the request waiting for its answer. It only
    /// describes; the server checks every input and rolls every number.
    /// </summary>
    public sealed class ForgeWindowViewModel : ViewModel
    {
        private readonly ForgeRules? _rules;
        private readonly List<ForgeDefinition> _definitions = new List<ForgeDefinition>();
        private readonly List<List<InventorySlotEntry>> _candidates = new List<List<InventorySlotEntry>>();
        private readonly List<int> _chosen = new List<int>();
        private readonly List<InventorySlotEntry> _sessionInputs = new List<InventorySlotEntry>();
        private IReadOnlyList<InventorySlotEntry> _bag = new InventorySlotEntry[0];

        public ForgeWindowViewModel(ForgeRules? rules)
        {
            _rules = rules;
        }

        public bool Open { get; private set; }

        public uint StationEntityId { get; private set; }

        public NpcInteraction Station { get; private set; }

        /// <summary>The station's molds and recipes in table order.</summary>
        public IReadOnlyList<ForgeDefinition> Definitions => _definitions;

        /// <summary>The chosen definition (index into <see cref="Definitions"/>), -1 for none.</summary>
        public int Selected { get; private set; } = -1;

        public ForgeDefinition? Definition => Selected >= 0 && Selected < _definitions.Count ? _definitions[Selected] : null;

        /// <summary>The session as last sent (a running or finishing one; null otherwise).</summary>
        public ForgeState? Session { get; private set; }

        public bool InSession => Session != null && (Session.Phase == ForgePhase.Running || Session.Phase == ForgePhase.Finishing);

        public bool Finishing => Session?.Phase == ForgePhase.Finishing || PendingFinish != 0;

        public uint PendingStart { get; private set; }

        public uint PendingFinish { get; private set; }

        public uint PendingAction { get; private set; }

        public ForgeRules? Rules => _rules;

        /// <summary>The window opens at a station (its yes); the list is the station's own.</summary>
        public void OpenAt(uint stationEntityId, NpcInteraction station)
        {
            StationEntityId = stationEntityId;
            Station = station;
            Open = true;
            _definitions.Clear();
            if (_rules != null)
            {
                foreach (var definition in _rules.Definitions)
                {
                    if (definition.Station == station && definition.Output != null)
                        _definitions.Add(definition);
                }
            }
            if (Selected >= _definitions.Count)
                Selected = -1;
            Repick();
            NotifyChanged();
        }

        public void Close()
        {
            Open = false;
            NotifyChanged();
        }

        public void Select(int index)
        {
            Selected = index >= 0 && index < _definitions.Count ? index : -1;
            Repick();
            NotifyChanged();
        }

        /// <summary>The Normal bag as last sent: the candidates follow it.</summary>
        public void SetBag(IReadOnlyList<InventorySlotEntry> bag)
        {
            _bag = bag;
            Repick();
            NotifyChanged();
        }

        /// <summary>The next bag stack for input <paramref name="input"/>.</summary>
        public void Cycle(int input)
        {
            if (input < 0 || input >= _candidates.Count || _candidates[input].Count == 0)
                return;
            _chosen[input] = (_chosen[input] + 1) % _candidates[input].Count;
            NotifyChanged();
        }

        public int InputCount => _candidates.Count;

        /// <summary>
        /// The bag stack chosen for input <paramref name="input"/>, or null when none fits. During a session it is the stack the
        /// start sent: the session holds those units reserved, so the picker (free units only) would find none (wave 1 QA F2).
        /// </summary>
        public InventorySlotEntry? Chosen(int input)
        {
            if (InSession && _sessionInputs.Count > 0)
                return input >= 0 && input < _sessionInputs.Count ? _sessionInputs[input] : null;
            return input >= 0 && input < _candidates.Count && _candidates[input].Count > 0 ? _candidates[input][_chosen[input]] : null;
        }

        public int CandidateCount(int input) => input >= 0 && input < _candidates.Count ? _candidates[input].Count : 0;

        /// <summary>Every input has a stack (the server checks them again, counts included).</summary>
        public bool CanStart => Definition != null && !InSession && PendingStart == 0 && _candidates.TrueForAll(c => c.Count > 0);

        /// <summary>The inputs of a start, in input order.</summary>
        public List<ForgeInput> Inputs(byte normalBag)
        {
            var inputs = new List<ForgeInput>(_candidates.Count);
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (Chosen(i) is { } entry)
                    inputs.Add(new ForgeInput { Container = normalBag, Slot = entry.Slot });
            }
            return inputs;
        }

        public int ActionsUsed => Session?.Actions.Length ?? 0;

        public int MaxActions => Session?.MaxActions ?? 0;

        public bool CanAct => InSession && !Finishing && PendingAction == 0 && ActionsUsed < MaxActions;

        public void StartSent(uint requestId)
        {
            PendingStart = requestId;
            _sessionInputs.Clear();
            for (int i = 0; i < _candidates.Count; i++)
            {
                if (Chosen(i) is { } entry)
                    _sessionInputs.Add(entry);
            }
            NotifyChanged();
        }

        public void ActionSent(uint requestId)
        {
            PendingAction = requestId;
            NotifyChanged();
        }

        public void FinishSent(uint requestId)
        {
            PendingFinish = requestId;
            NotifyChanged();
        }

        /// <summary>
        /// A session state: the answer to a start, action or cancel (its refusal code returned for the toast) or the server's own
        /// cancel. A running session selects its definition.
        /// </summary>
        public string? SetState(ForgeState state)
        {
            string? refusal = null;
            if (state.RequestId != 0 && (state.RequestId == PendingStart || state.RequestId == PendingAction))
            {
                if (state.Reason != ForgeReason.None)
                    refusal = state.Reason.ToString();
                if (state.RequestId == PendingStart)
                    PendingStart = 0;
                if (state.RequestId == PendingAction)
                    PendingAction = 0;
            }
            else if (state.RequestId == 0 && state.Phase == ForgePhase.Cancelled && state.Reason != ForgeReason.None)
                refusal = state.Reason.ToString(); // the server ended it: leaving the station, combat
            Session = state.Phase == ForgePhase.Running || state.Phase == ForgePhase.Finishing ? state : null;
            if (Session == null && PendingStart == 0)
                _sessionInputs.Clear(); // refused, cancelled or finished: the picker takes over again
            if (Session != null)
            {
                for (int i = 0; i < _definitions.Count; i++)
                {
                    if (_definitions[i].Kind == Session.Kind && _definitions[i].NetId == Session.DefNetId && Selected != i)
                    {
                        Selected = i;
                        Repick();
                    }
                }
            }
            NotifyChanged();
            return refusal;
        }

        /// <summary>A finish's answer: the refusal code, or null for a result (its toast is the binder's).</summary>
        public string? OnResult(ForgeResult result)
        {
            if (result.RequestId != PendingFinish || PendingFinish == 0)
                return null;
            PendingFinish = 0;
            NotifyChanged();
            return result.Reason == ForgeReason.None ? null : result.Reason.ToString();
        }

        public void Clear()
        {
            Open = false;
            Session = null;
            _sessionInputs.Clear();
            PendingStart = PendingAction = PendingFinish = 0;
            NotifyChanged();
        }

        private void Repick()
        {
            _candidates.Clear();
            _chosen.Clear();
            if (Definition is not { } definition || _rules == null)
                return;
            var taken = new Dictionary<int, int>();
            foreach (var spec in definition.Inputs)
            {
                var list = new List<InventorySlotEntry>();
                foreach (var entry in _bag)
                {
                    if (entry.Count <= 0 || entry.Reserved || _rules.Item(entry.ItemNetId) is not { } item || !spec.Takes(item))
                        continue;
                    taken.TryGetValue(entry.Slot, out int used);
                    if (entry.Count - used >= spec.Count)
                        list.Add(entry);
                }
                if (list.Count > 0)
                {
                    taken.TryGetValue(list[0].Slot, out int already);
                    taken[list[0].Slot] = already + spec.Count;
                }
                _candidates.Add(list);
                _chosen.Add(0);
            }
        }
    }
}
