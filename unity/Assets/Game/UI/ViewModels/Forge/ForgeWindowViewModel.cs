#nullable enable
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;

namespace Game.UI.ViewModels.Forge
{
    /// <summary>The station window's own tabs (the Enchant tab is <c>EnchantPanelViewModel.Active</c>).</summary>
    public enum ForgeTab : byte
    {
        /// <summary>The mold slot and its material slots (spec 5.3 M3 decision 13, WP-M3-14).</summary>
        Forge = 0,

        /// <summary>The refine, convert and mold-making recipes: the list with Next (the WP-M3-6 window).</summary>
        Recipes = 1,
    }

    /// <summary>
    /// The forge window's state (spec 5.3, 9.13; WP-M3-6, Forge v2 WP-M3-14): the station it was opened at; on the Forge tab
    /// the mold the player put in the mold slot (one held in the Safe bag's equipment area) and the Normal bag stack the
    /// player put in each of its material slots (dragged from the inventory window or chosen in the picker: never picked for
    /// them); on the Recipes tab the station's recipes with a bag stack per input (the next candidate on request); the
    /// session the server last sent (actions, the quality range) and the request waiting for its answer. It only describes;
    /// the server checks every input and rolls every number. A slot assignment never moves an item: it only names the stack
    /// the start request sends.
    /// </summary>
    public sealed class ForgeWindowViewModel : ViewModel
    {
        /// <summary><see cref="PickerTarget"/> while no picker is open.</summary>
        public const int PickNone = -1;

        /// <summary><see cref="PickerTarget"/> of the mold slot's picker.</summary>
        public const int PickMold = -2;

        private readonly ForgeRules? _rules;
        private readonly List<ForgeDefinition> _molds = new List<ForgeDefinition>();
        private readonly List<ForgeDefinition> _recipes = new List<ForgeDefinition>();
        private readonly List<ForgeDefinition> _definitions = new List<ForgeDefinition>();
        private readonly List<int> _pickerMolds = new List<int>();
        private readonly List<InventorySlotEntry> _pickerStacks = new List<InventorySlotEntry>();
        private readonly List<List<InventorySlotEntry>> _candidates = new List<List<InventorySlotEntry>>();
        private readonly List<int> _chosen = new List<int>();
        private readonly List<InventorySlotEntry?> _assigned = new List<InventorySlotEntry?>();
        private readonly List<InventorySlotEntry> _sessionInputs = new List<InventorySlotEntry>();
        private IReadOnlyList<InventorySlotEntry> _bag = new InventorySlotEntry[0];
        private IReadOnlyList<InventorySlotEntry> _moldArea = new InventorySlotEntry[0];

        public ForgeWindowViewModel(ForgeRules? rules)
        {
            _rules = rules;
        }

        public bool Open { get; private set; }

        public uint StationEntityId { get; private set; }

        public NpcInteraction Station { get; private set; }

        public ForgeTab Tab { get; private set; }

        /// <summary>The station's molds in table order (the mold slot takes one of them).</summary>
        public IReadOnlyList<ForgeDefinition> Molds => _molds;

        /// <summary>The station's recipes in table order (the Recipes tab's rows).</summary>
        public IReadOnlyList<ForgeDefinition> Recipes => _recipes;

        /// <summary>The station's molds and recipes in table order (built when the window opens at a station).</summary>
        public IReadOnlyList<ForgeDefinition> Definitions => _definitions;

        /// <summary>
        /// The mold slot's picker as it opened: indexes into <see cref="Molds"/> (empty for a material slot's picker). A click
        /// picks the row it shows, even if the equipment area changed since.
        /// </summary>
        public IReadOnlyList<int> PickerMolds => _pickerMolds;

        /// <summary>
        /// A material slot's picker as it opened: the Normal bag stacks it lists. A click picks the stack it shows; the
        /// assignment checks it against the bag as it is now (<see cref="Assign"/>), so a stack that changed is refused, never
        /// swapped for another.
        /// </summary>
        public IReadOnlyList<InventorySlotEntry> PickerStacks => _pickerStacks;

        /// <summary>The mold in the mold slot (index into <see cref="Molds"/>), -1 for none.</summary>
        public int SelectedMold { get; private set; } = -1;

        /// <summary>The chosen recipe (index into <see cref="Recipes"/>), -1 for none.</summary>
        public int Selected { get; private set; } = -1;

        public ForgeDefinition? Mold => SelectedMold >= 0 && SelectedMold < _molds.Count ? _molds[SelectedMold] : null;

        public ForgeDefinition? Recipe => Selected >= 0 && Selected < _recipes.Count ? _recipes[Selected] : null;

        /// <summary>The definition of the open tab: the mold on the Forge tab, the recipe on the Recipes tab.</summary>
        public ForgeDefinition? Definition => Tab == ForgeTab.Forge ? Mold : Recipe;

        /// <summary>The session as last sent (a running or finishing one; null otherwise).</summary>
        public ForgeState? Session { get; private set; }

        public bool InSession => Session != null && (Session.Phase == ForgePhase.Running || Session.Phase == ForgePhase.Finishing);

        public bool Finishing => Session?.Phase == ForgePhase.Finishing || PendingFinish != 0;

        public uint PendingStart { get; private set; }

        public uint PendingFinish { get; private set; }

        public uint PendingAction { get; private set; }

        public ForgeRules? Rules => _rules;

        /// <summary>The slot the picker lists stacks for (<see cref="PickMold"/>, an input index, or <see cref="PickNone"/>).</summary>
        public int PickerTarget { get; private set; } = PickNone;

        /// <summary>The container code of the Safe bag's equipment area, where molds are held (<c>MoldRequired</c>).</summary>
        public byte MoldContainer { get; private set; }

        /// <summary>The window opens at a station (its yes) on the Forge tab, its slots empty.</summary>
        public void OpenAt(uint stationEntityId, NpcInteraction station)
        {
            StationEntityId = stationEntityId;
            Station = station;
            Open = true;
            _molds.Clear();
            _recipes.Clear();
            if (_rules != null)
            {
                foreach (var definition in _rules.Definitions)
                {
                    if (definition.Station != station || definition.Output == null)
                        continue;
                    (definition.Kind == ForgeKind.Mold ? _molds : _recipes).Add(definition);
                }
            }
            _definitions.Clear();
            _definitions.AddRange(_molds);
            _definitions.AddRange(_recipes);
            if (!InSession)
            {
                Tab = ForgeTab.Forge;
                SelectedMold = -1;
                Selected = -1;
            }
            PickerTarget = PickNone;
            Reassign();
            Repick();
            NotifyChanged();
        }

        public void Close()
        {
            Open = false;
            PickerTarget = PickNone;
            NotifyChanged();
        }

        /// <summary>Shows a tab; refused during a session (the session's own tab stays).</summary>
        public void ShowTab(ForgeTab tab)
        {
            if (InSession || Tab == tab)
                return;
            Tab = tab;
            PickerTarget = PickNone;
            NotifyChanged();
        }

        /// <summary>A recipe row of the Recipes tab.</summary>
        public void Select(int index)
        {
            if (InSession)
                return;
            Selected = index >= 0 && index < _recipes.Count ? index : -1;
            Repick();
            NotifyChanged();
        }

        /// <summary>The Normal bag and the containers as last sent: the assignments and the recipe candidates follow them.</summary>
        public void SetBag(IReadOnlyList<InventorySlotEntry> bag, IReadOnlyList<InventorySlotEntry>? moldArea = null, byte moldContainer = 0)
        {
            _bag = bag;
            if (moldArea != null)
            {
                _moldArea = moldArea;
                MoldContainer = moldContainer;
            }
            if (!InSession && Mold is { } mold && !Holds(mold))
            {
                SelectedMold = -1; // the mold left the equipment area
                Reassign();
            }
            Revalidate();
            Repick();
            NotifyChanged();
        }

        /// <summary>Whether the Safe bag's equipment area holds the mold item of <paramref name="definition"/>.</summary>
        public bool Holds(ForgeDefinition definition)
        {
            foreach (var entry in _moldArea)
            {
                if (entry.Count > 0 && _rules?.Item(entry.ItemNetId) is { } item && item.Id == definition.MoldItemId)
                    return true;
            }
            return false;
        }

        /// <summary>The station's molds the player holds (indexes into <see cref="Molds"/>): the mold slot's picker.</summary>
        public List<int> MoldCandidates()
        {
            var list = new List<int>();
            for (int i = 0; i < _molds.Count; i++)
            {
                if (Holds(_molds[i]))
                    list.Add(i);
            }
            return list;
        }

        /// <summary>Puts the mold <paramref name="index"/> (of <see cref="Molds"/>) in the mold slot; its slots start empty.</summary>
        public void SetMold(int index)
        {
            if (InSession)
                return;
            int mold = index >= 0 && index < _molds.Count ? index : -1;
            PickerTarget = PickNone;
            Tab = ForgeTab.Forge;
            if (mold != SelectedMold)
            {
                SelectedMold = mold;
                Reassign();
            }
            NotifyChanged();
        }

        /// <summary>
        /// A stack dropped on the mold slot: from <paramref name="container"/> (it must be the Safe bag's equipment area), a mold
        /// this station uses. The refusal code for the toast, or null.
        /// </summary>
        public string? DropMold(byte container, InventorySlotEntry entry)
        {
            if (InSession)
                return null;
            var item = _rules?.Item(entry.ItemNetId);
            int index = item == null ? -1 : _molds.FindIndex(m => m.MoldItemId == item.Id);
            if (index < 0)
            {
                bool otherStation = item != null && _rules != null && FindMold(item.Id) != null;
                return otherStation ? ForgeReason.WrongStation.ToString() : ForgeReason.MoldRequired.ToString();
            }
            if (container != MoldContainer)
                return ForgeReason.MoldRequired.ToString(); // the server reads molds from the Safe bag's equipment area only
            SetMold(index);
            return null;
        }

        /// <summary>
        /// A Normal bag stack put in material slot <paramref name="input"/> of the mold (a drop or the picker's choice): the
        /// refusal code for the toast (<c>SlotMaterialInvalid</c>, <c>MaterialsMissing</c>), or null when it is assigned.
        /// </summary>
        public string? Assign(int input, byte container, InventorySlotEntry entry, byte normalBag)
        {
            if (InSession || Mold is not { } mold || input < 0 || input >= mold.Inputs.Count)
                return null;
            if (container != normalBag)
                return ForgeReason.SlotMaterialInvalid.ToString();
            var current = Find(entry.Slot);
            if (current == null || current.ItemNetId != entry.ItemNetId)
                return Refusal(mold, input, entry) == ForgeReason.SlotMaterialInvalid
                    ? ForgeReason.SlotMaterialInvalid.ToString()
                    : ForgeReason.MaterialsMissing.ToString(); // that stack is gone or changed since it was shown
            if (Refusal(mold, input, current) is { } refused)
                return refused.ToString();
            _assigned[input] = current;
            PickerTarget = PickNone;
            NotifyChanged();
            return null;
        }

        /// <summary>Empties material slot <paramref name="input"/>.</summary>
        public void ClearInput(int input)
        {
            if (InSession || input < 0 || input >= _assigned.Count || _assigned[input] == null)
                return;
            _assigned[input] = null;
            NotifyChanged();
        }

        /// <summary>The Normal bag stacks material slot <paramref name="input"/> can take now (the picker's list), in slot order.</summary>
        public List<InventorySlotEntry> Candidates(int input)
        {
            var list = new List<InventorySlotEntry>();
            if (Mold is not { } mold || input < 0 || input >= mold.Inputs.Count)
                return list;
            foreach (var entry in _bag)
            {
                if (Refusal(mold, input, entry) == null)
                    list.Add(entry);
            }
            return list;
        }

        /// <summary>
        /// Opens the picker of the mold slot (<see cref="PickMold"/>) or of a material slot, its rows taken now
        /// (<see cref="PickerMolds"/>, <see cref="PickerStacks"/>); the same target closes it.
        /// </summary>
        public void OpenPicker(int target)
        {
            if (InSession)
                return;
            bool valid = target == PickMold || (Mold is { } mold && target >= 0 && target < mold.Inputs.Count);
            PickerTarget = valid && PickerTarget != target ? target : PickNone;
            _pickerMolds.Clear();
            _pickerStacks.Clear();
            if (PickerTarget == PickMold)
                _pickerMolds.AddRange(MoldCandidates());
            else if (PickerTarget != PickNone)
                _pickerStacks.AddRange(Candidates(PickerTarget));
            NotifyChanged();
        }

        /// <summary>Row <paramref name="row"/> of the open picker, as it was shown: the refusal code for the toast, or null.</summary>
        public string? PickRow(int row, byte normalBag)
        {
            if (PickerTarget == PickMold)
            {
                if (row >= 0 && row < _pickerMolds.Count && Holds(_molds[_pickerMolds[row]]))
                    SetMold(_pickerMolds[row]);
                return null;
            }
            if (PickerTarget == PickNone || row < 0 || row >= _pickerStacks.Count)
                return null;
            return Assign(PickerTarget, normalBag, _pickerStacks[row], normalBag);
        }

        public void ClosePicker()
        {
            if (PickerTarget == PickNone)
                return;
            PickerTarget = PickNone;
            NotifyChanged();
        }

        /// <summary>The Next of a recipe input: the next bag stack that fits.</summary>
        public void Cycle(int input)
        {
            if (input < 0 || input >= _candidates.Count || _candidates[input].Count == 0)
                return;
            _chosen[input] = (_chosen[input] + 1) % _candidates[input].Count;
            NotifyChanged();
        }

        public int InputCount => Definition?.Inputs.Count ?? 0;

        /// <summary>
        /// The bag stack in input <paramref name="input"/> of the open tab's definition, or null: during a session the stack the
        /// start sent (the session holds those units reserved, wave 1 QA F2); on the Forge tab the one the player put there; on
        /// the Recipes tab the picked candidate.
        /// </summary>
        public InventorySlotEntry? Chosen(int input)
        {
            if (InSession && _sessionInputs.Count > 0)
                return input >= 0 && input < _sessionInputs.Count ? _sessionInputs[input] : null;
            if (Tab == ForgeTab.Forge)
                return input >= 0 && input < _assigned.Count ? _assigned[input] : null;
            return input >= 0 && input < _candidates.Count && _candidates[input].Count > 0 ? _candidates[input][_chosen[input]] : null;
        }

        public int CandidateCount(int input) => input >= 0 && input < _candidates.Count ? _candidates[input].Count : 0;

        /// <summary>
        /// Every input has a stack and, on the Forge tab, the mold is held (the server checks them again, counts included).
        /// </summary>
        public bool CanStart
        {
            get
            {
                if (Definition is not { } definition || InSession || PendingStart != 0)
                    return false;
                if (Tab == ForgeTab.Recipes)
                    return _candidates.TrueForAll(c => c.Count > 0);
                return Holds(definition) && _assigned.Count == definition.Inputs.Count && _assigned.TrueForAll(a => a != null);
            }
        }

        /// <summary>The inputs of a start, in input order.</summary>
        public List<ForgeInput> Inputs(byte normalBag)
        {
            var inputs = new List<ForgeInput>(InputCount);
            for (int i = 0; i < InputCount; i++)
            {
                if (Chosen(i) is { } entry)
                    inputs.Add(new ForgeInput { Container = normalBag, Slot = entry.Slot });
            }
            return inputs;
        }

        /// <summary>
        /// The stat ranges the forged item can end with (spec 5.3 M3 decision 13), never a rolled number: before Start what the
        /// chosen materials can reach (<see cref="ForgeRules.ReachRange"/>), during a session the range the server's quality
        /// bounds allow. Empty until every slot of a mold has a material, and for recipes.
        /// </summary>
        public List<CraftedStatRange> StatPreview()
        {
            var none = new List<CraftedStatRange>();
            if (_rules == null || Definition is not { } definition || !definition.RollsQuality)
                return none;
            var items = new List<ItemDef>(definition.Inputs.Count);
            for (int i = 0; i < definition.Inputs.Count; i++)
            {
                if (Chosen(i) is not { } entry || _rules.Item(entry.ItemNetId) is not { } item)
                    return none;
                items.Add(item);
            }
            if (InSession)
                return _rules.StatRange(definition, items, Session!.QualityMin, Session.QualityMax);
            _rules.ReachRange(definition, items, out int min, out int max);
            return _rules.StatRange(definition, items, min, max);
        }

        public int ActionsUsed => Session?.Actions.Length ?? 0;

        public int MaxActions => Session?.MaxActions ?? 0;

        public bool CanAct => InSession && !Finishing && PendingAction == 0 && ActionsUsed < MaxActions;

        public void StartSent(uint requestId)
        {
            PendingStart = requestId;
            PickerTarget = PickNone;
            _sessionInputs.Clear();
            for (int i = 0; i < InputCount; i++)
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
        /// cancel. A running session shows its definition on its own tab.
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
                _sessionInputs.Clear(); // refused, cancelled or finished: the slots show the player's choice again
            if (Session != null)
            {
                PickerTarget = PickNone;
                if (Session.Kind == ForgeKind.Mold)
                {
                    int index = _molds.FindIndex(d => d.NetId == Session.DefNetId);
                    Tab = ForgeTab.Forge;
                    if (index >= 0 && index != SelectedMold)
                    {
                        SelectedMold = index;
                        Reassign();
                    }
                }
                else
                {
                    int index = _recipes.FindIndex(d => d.NetId == Session.DefNetId);
                    Tab = ForgeTab.Recipes;
                    if (index >= 0 && index != Selected)
                    {
                        Selected = index;
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
            PickerTarget = PickNone;
            NotifyChanged();
        }

        private ForgeDefinition? FindMold(string itemId)
        {
            if (_rules == null)
                return null;
            foreach (var definition in _rules.Definitions)
            {
                if (definition.Kind == ForgeKind.Mold && definition.MoldItemId == itemId)
                    return definition;
            }
            return null;
        }

        private InventorySlotEntry? Find(int slot)
        {
            foreach (var entry in _bag)
            {
                if (entry.Slot == slot)
                    return entry;
            }
            return null;
        }

        /// <summary>
        /// The one slot rule of the client (wave 2 fixer: the drop, the picker and the bag's revalidation used to write it
        /// three times): material slot <paramref name="input"/> of <paramref name="mold"/> can take <paramref name="entry"/>
        /// when the slot takes its item (<c>ForgeInputSpec.Takes</c>, the server's rule) and the stack has the slot's units
        /// free, not reserved and not counted by the other material slots on it. Null when it fits, else the refusal:
        /// <c>SlotMaterialInvalid</c> for an item the slot does not take, <c>MaterialsMissing</c> for too few free units.
        /// </summary>
        private ForgeReason? Refusal(ForgeDefinition mold, int input, InventorySlotEntry entry)
        {
            if (_rules?.Item(entry.ItemNetId) is not { } item || !mold.Inputs[input].Takes(item))
                return ForgeReason.SlotMaterialInvalid;
            if (entry.Count <= 0 || entry.Reserved || FreeUnits(entry, input) < mold.Inputs[input].Count)
                return ForgeReason.MaterialsMissing;
            return null;
        }

        /// <summary>The units of a stack left for input <paramref name="input"/> after the other material slots on it.</summary>
        private int FreeUnits(InventorySlotEntry entry, int input)
        {
            int free = entry.Count;
            var mold = Mold;
            for (int i = 0; i < _assigned.Count; i++)
            {
                if (i != input && _assigned[i] is { } other && other.Slot == entry.Slot && mold != null && i < mold.Inputs.Count)
                    free -= mold.Inputs[i].Count;
            }
            return free;
        }

        /// <summary>The mold changed: one empty material slot per input.</summary>
        private void Reassign()
        {
            _assigned.Clear();
            if (Mold is { } mold)
            {
                for (int i = 0; i < mold.Inputs.Count; i++)
                    _assigned.Add(null);
            }
        }

        /// <summary>
        /// The bag changed: each assignment follows its stack's new contents, and drops when the slot no longer holds that item,
        /// the stack became reserved (another session holds it), or it has too few units for every material slot on it (the
        /// later slots drop first: each is checked by <see cref="Refusal"/> against the slots before it only). Not while a start
        /// waits or a session runs: the start's own reservation reaches the bag first, and the slots keep the player's choice
        /// for after.
        /// </summary>
        private void Revalidate()
        {
            if (InSession || PendingStart != 0 || Mold is not { } mold)
                return;
            int count = System.Math.Min(_assigned.Count, mold.Inputs.Count);
            var old = new InventorySlotEntry?[count];
            for (int i = 0; i < count; i++)
            {
                old[i] = _assigned[i];
                _assigned[i] = null;
            }
            for (int i = 0; i < count; i++)
            {
                if (old[i] is { } was && Find(was.Slot) is { } current && current.ItemNetId == was.ItemNetId
                    && Refusal(mold, i, current) == null)
                    _assigned[i] = current;
            }
        }

        /// <summary>The recipe's candidates per input from the bag (the Recipes tab).</summary>
        private void Repick()
        {
            _candidates.Clear();
            _chosen.Clear();
            if (Recipe is not { } definition || _rules == null)
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
