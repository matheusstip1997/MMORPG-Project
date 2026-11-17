#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace Game.UI.ViewModels.Contracts
{
    /// <summary>The two tabs of the C window (WP-M4-10 part B).</summary>
    public enum ContractTab : byte
    {
        Mine = 0,
        Propose = 1,
    }

    /// <summary>
    /// A portfolio shown read-only inside a window (spec 5.4 R-5.4-04, M4 decision 11): another player's, read through one of the own
    /// contracts (its crafter) or the target lock at the board. The request waiting for its <see cref="PortfolioState"/>, then the state.
    /// </summary>
    public sealed class PortfolioView
    {
        /// <summary>The view request waiting for its answer; 0 when none.</summary>
        public uint Pending { get; private set; }

        /// <summary>The state shown; null while waiting.</summary>
        public PortfolioState? State { get; private set; }

        /// <summary>Whether the view is up (waiting or shown).</summary>
        public bool Shown { get; private set; }

        public void Asked(uint requestId)
        {
            Pending = requestId;
            State = null;
            Shown = requestId != 0;
        }

        /// <summary>An answer: true when it was this view's (a refusal closes the view; its reason is the caller's toast).</summary>
        public bool Answer(PortfolioState state)
        {
            if (Pending == 0 || state.RequestId != Pending)
                return false;
            Pending = 0;
            if (state.Reason != ContractReason.None)
            {
                Shown = false;
                State = null;
            }
            else
                State = state;
            return true;
        }

        public void Close()
        {
            Pending = 0;
            State = null;
            Shown = false;
        }
    }

    /// <summary>One input of the propose form: who supplies it, the customer's bag stack, or the item a crafter's slot names (0 any).</summary>
    public sealed class ContractFormSlot
    {
        public ContractSupplier Supplier { get; set; }

        /// <summary>The Normal bag stack the customer puts in escrow (customer slots); null until chosen.</summary>
        public InventorySlotEntry? Stack { get; set; }

        /// <summary>The item a crafter's slot requires (crafter slots); 0 for any the input takes.</summary>
        public int CrafterItem { get; set; }
    }

    /// <summary>
    /// The propose form (spec 5.4, M4 decision 10; the C window's Propose tab; WP-M4-10 part B): the job action (a craftable forge
    /// definition, stepped through), one slot per input with its supplier, the gold and the deadline. <see cref="Check"/> runs the
    /// server's own checks (<see cref="ContractMath"/>, <see cref="ContractRules"/>, <see cref="TradeRules"/>) so the button stays
    /// off with the reason shown; the server checks everything again. Choosing a stack never moves it: the proposal names it and
    /// the server holds its units in escrow.
    /// </summary>
    public sealed class ContractFormModel
    {
        private readonly ContractConfig _config;
        private readonly List<ForgeDefinition> _definitions = new List<ForgeDefinition>();
        private readonly List<ContractFormSlot> _slots = new List<ContractFormSlot>();
        private IReadOnlyList<InventorySlotEntry> _bag = Array.Empty<InventorySlotEntry>();
        private IReadOnlyList<InventorySlotEntry> _safe = Array.Empty<InventorySlotEntry>();

        public ContractFormModel(ContractConfig config)
        {
            _config = config;
            if (config.Forge != null)
            {
                foreach (var definition in config.Forge.Definitions)
                {
                    if (definition.Output != null)
                        _definitions.Add(definition);
                }
            }
            Gold = config.MinGold;
            DeadlineHours = config.MaxDeadlineHours >= 24 && config.MinDeadlineHours <= 24 ? 24 : config.MinDeadlineHours;
            Step(0);
        }

        /// <summary>Every craftable definition, in the forge rules' order (the form steps through them, then the enchant action).</summary>
        public IReadOnlyList<ForgeDefinition> Definitions => _definitions;

        public int Index { get; private set; } = -1;

        /// <summary>The steps of the action: the definitions, then the enchant action when the enchant rules are loaded.</summary>
        private int StepCount => _definitions.Count + (_config.Enchant != null ? 1 : 0);

        public ForgeDefinition? Definition => Index >= 0 && Index < _definitions.Count ? _definitions[Index] : null;

        /// <summary>The enchant action (WP-M4-10 part C): one attempt on an item of the Safe bag's equipment area.</summary>
        public bool IsEnchant => _config.Enchant != null && Index == _definitions.Count;

        /// <summary>The enchant action's item (its slot's stack); null until chosen or for a forge action.</summary>
        public ItemDef? EnchantItem => IsEnchant && _slots.Count > 0 && _slots[ContractRules.EnchantItemSlot].Stack is { } stack
            ? _config.Enchant!.Item(stack.ItemNetId) : null;

        /// <summary>The kind and definition a proposal sends: the forge definition's, or for the enchant action the item's net id.</summary>
        public ForgeKind ProposeKind => IsEnchant ? ForgeKind.Enchant : Definition?.Kind ?? ForgeKind.None;

        public int ProposeDefNetId => IsEnchant ? EnchantItem?.NetId ?? 0 : Definition?.NetId ?? 0;

        public IReadOnlyList<ContractFormSlot> Slots => _slots;

        public long Gold { get; set; }

        public int DeadlineHours { get; set; }

        /// <summary>The Normal bag's container id (what a customer slot names).</summary>
        public byte NormalBag { get; set; }

        /// <summary>The material slot whose picker is open; -1 none.</summary>
        public int PickerTarget { get; set; } = -1;

        /// <summary>
        /// The Normal bag and the Safe bag's equipment area (the enchant action's items) as last sent: a chosen stack follows them
        /// (dropped when it is gone, changed or no longer offerable).
        /// </summary>
        public void SetBag(IReadOnlyList<InventorySlotEntry> bag, IReadOnlyList<InventorySlotEntry>? safe = null)
        {
            _bag = bag;
            if (safe != null)
                _safe = safe;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Stack is not { } was)
                    continue;
                InventorySlotEntry? now = null;
                foreach (var entry in SourceOf(i))
                {
                    if (entry.Slot == was.Slot && entry.ItemNetId == was.ItemNetId && entry.InstanceId == was.InstanceId)
                        now = entry;
                }
                _slots[i].Stack = now != null && Refusal(i, now) == null ? now : null;
            }
        }

        /// <summary>Steps the action by <paramref name="delta"/> (wrapping); the slots start again, the customer's by default.</summary>
        public void Step(int delta)
        {
            if (StepCount == 0)
            {
                Index = -1;
                _slots.Clear();
                return;
            }
            Index = Index < 0 ? 0 : ((Index + delta) % StepCount + StepCount) % StepCount;
            _slots.Clear();
            if (IsEnchant)
            {
                _slots.Add(new ContractFormSlot { Supplier = ContractSupplier.Customer }); // the item: always the customer's
                _slots.Add(new ContractFormSlot { Supplier = ContractSupplier.Customer }); // the stones: either side (user, 2026-11-16)
            }
            else
            {
                foreach (var _ in _definitions[Index].Inputs)
                    _slots.Add(new ContractFormSlot { Supplier = ContractSupplier.Customer });
            }
            PickerTarget = -1;
        }

        /// <summary>Switches who supplies input <paramref name="input"/>; its stack or named item starts empty (never the enchant item).</summary>
        public void Switch(int input)
        {
            if (input < 0 || input >= _slots.Count || (IsEnchant && input == ContractRules.EnchantItemSlot))
                return;
            var slot = _slots[input];
            slot.Supplier = slot.Supplier == ContractSupplier.Customer ? ContractSupplier.Crafter : ContractSupplier.Customer;
            slot.Stack = null;
            slot.CrafterItem = 0;
            if (PickerTarget == input)
                PickerTarget = -1;
        }

        /// <summary>A crafter's slot: the next material the input takes, after the last one back to "any" (0). Not for the enchant stones.</summary>
        public void CycleCrafterItem(int input)
        {
            if (Definition is not { } definition || input < 0 || input >= _slots.Count || _slots[input].Supplier != ContractSupplier.Crafter)
                return;
            var spec = definition.Inputs[input];
            int current = _slots[input].CrafterItem;
            bool passed = current == 0;
            foreach (var item in _config.Materials)
            {
                if (!spec.Takes(item))
                    continue;
                if (passed)
                {
                    _slots[input].CrafterItem = item.NetId;
                    return;
                }
                passed = item.NetId == current;
            }
            _slots[input].CrafterItem = 0;
        }

        /// <summary>
        /// The stacks customer slot <paramref name="input"/> can take now (the picker's list), in slot order: the Normal bag's, or
        /// for the enchant item the Safe bag's equipment area's.
        /// </summary>
        public List<InventorySlotEntry> Candidates(int input)
        {
            var list = new List<InventorySlotEntry>();
            if (input < 0 || input >= _slots.Count || _slots[input].Supplier != ContractSupplier.Customer)
                return list;
            foreach (var entry in SourceOf(input))
            {
                if (Refusal(input, entry) == null)
                    list.Add(entry);
            }
            return list;
        }

        /// <summary>Puts <paramref name="entry"/> in customer slot <paramref name="input"/>: the refusal code for the toast, or null.</summary>
        public string? Assign(int input, InventorySlotEntry entry)
        {
            if (input < 0 || input >= _slots.Count || _slots[input].Supplier != ContractSupplier.Customer)
                return null;
            if (Refusal(input, entry) is { } refused)
                return refused.ToString();
            _slots[input].Stack = entry;
            if (IsEnchant && input == ContractRules.EnchantItemSlot && _slots[ContractRules.EnchantStonesSlot].Stack is { } stones
                && Refusal(ContractRules.EnchantStonesSlot, stones) != null)
                _slots[ContractRules.EnchantStonesSlot].Stack = null; // another tier's item takes other stones
            PickerTarget = -1;
            return null;
        }

        public void ClearSlot(int input)
        {
            if (input >= 0 && input < _slots.Count)
                _slots[input].Stack = null;
        }

        /// <summary>
        /// The server's checks of a proposal, in its order: the value, the deadline, every customer slot chosen, then the terms
        /// (<see cref="ContractRules.CheckTerms"/>, or <see cref="ContractRules.CheckEnchantTerms"/> for the enchant action).
        /// <see cref="ContractReason.None"/> when the proposal may be sent.
        /// </summary>
        public ContractReason Check()
        {
            if (!IsEnchant && (Definition == null || _config.Forge == null))
                return ContractReason.ContractTermsInvalid;
            var gold = ContractMath.CheckGold(Gold, _config.MinGold, _config.MaxGold);
            if (gold != ContractReason.None)
                return gold;
            if (!ContractMath.DeadlineAllowed(DeadlineHours, _config.MinDeadlineHours, _config.MaxDeadlineHours))
                return ContractReason.ContractDeadlineInvalid;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (_slots[i].Supplier == ContractSupplier.Customer && _slots[i].Stack == null)
                    return ContractReason.ContractTermsInvalid;
            }
            if (IsEnchant)
            {
                var stack = _slots[ContractRules.EnchantItemSlot].Stack!;
                return ContractRules.CheckEnchantTerms(_config.Enchant!, EnchantItem, stack.Enchant, ToSlots());
            }
            return ContractRules.CheckTerms(Definition, ToSlots(), _config.Forge!.Item);
        }

        /// <summary>The terms as the proposal sends them: one slot per input, in order (the enchant action: the item, then the stones).</summary>
        public ContractSlot[] ToSlots()
        {
            if (IsEnchant)
                return EnchantSlots();
            var definition = Definition;
            var slots = new ContractSlot[_slots.Count];
            for (int i = 0; i < slots.Length; i++)
            {
                var form = _slots[i];
                int count = definition != null && i < definition.Inputs.Count ? definition.Inputs[i].Count : 0;
                slots[i] = form.Supplier == ContractSupplier.Customer && form.Stack is { } stack
                    ? new ContractSlot
                    {
                        Supplier = ContractSupplier.Customer, Container = NormalBag, Slot = stack.Slot, ItemNetId = stack.ItemNetId,
                        Rarity = stack.Rarity, Count = (ushort)count,
                    }
                    : new ContractSlot { Supplier = form.Supplier, ItemNetId = (ushort)form.CrafterItem, Count = (ushort)count };
            }
            return slots;
        }

        /// <summary>The enchant action's two slots: the item instance of the Safe bag's equipment area, then the item's tier's stones.</summary>
        private ContractSlot[] EnchantSlots()
        {
            var rules = _config.Enchant!;
            var item = _slots[ContractRules.EnchantItemSlot].Stack;
            var target = item == null
                ? new ContractSlot { Supplier = ContractSupplier.Customer, Container = rules.SafeCode, Count = 1 }
                : new ContractSlot
                {
                    Supplier = ContractSupplier.Customer, Container = rules.SafeCode, Slot = item.Slot, ItemNetId = item.ItemNetId,
                    Rarity = item.Rarity, Count = 1, InstanceId = item.InstanceId, EnchantLevel = item.Enchant,
                };
            var stonesForm = _slots[ContractRules.EnchantStonesSlot];
            ushort stone = 0, count = 0;
            if (EnchantItem is { } def && rules.TryMaterial(def, out var stoneDef, out int stoneCount))
            {
                stone = (ushort)stoneDef.NetId;
                count = (ushort)stoneCount;
            }
            var stones = stonesForm.Supplier == ContractSupplier.Customer && stonesForm.Stack is { } held
                ? new ContractSlot
                {
                    Supplier = ContractSupplier.Customer, Container = NormalBag, Slot = held.Slot, ItemNetId = stone, Rarity = held.Rarity,
                    Count = count,
                }
                : new ContractSlot { Supplier = stonesForm.Supplier, ItemNetId = stone, Count = count };
            return new[] { target, stones };
        }

        private IReadOnlyList<InventorySlotEntry> SourceOf(int input) =>
            IsEnchant && input == ContractRules.EnchantItemSlot ? _safe : _bag;

        /// <summary>
        /// Whether customer slot <paramref name="input"/> can take <paramref name="entry"/>: the input takes its item (the enchant
        /// action: an enchantable equipment instance below the top level, or the item's stones), it may enter a contract (R-5.4-03:
        /// never stolen, tainted, non-tradable, sell-only, quest or reserved), and it has the input's units free after the other
        /// customer slots on the same stack. Null when it fits.
        /// </summary>
        private ContractReason? Refusal(int input, InventorySlotEntry entry)
        {
            if (IsEnchant)
                return EnchantRefusal(input, entry);
            if (Definition is not { } definition || _config.Forge?.Item(entry.ItemNetId) is not { } item || !definition.Inputs[input].Takes(item))
                return ContractReason.ContractTermsInvalid;
            var offer = TradeRules.CheckOffer(item.Category, entry.Stolen || entry.StolenMark != 0, entry.Tainted, entry.NonTradable, false);
            if (offer != TradeOfferCheck.Ok || entry.Reserved)
                return ContractReason.ContractNotTradable;
            int free = entry.Count;
            for (int i = 0; i < _slots.Count; i++)
            {
                if (i != input && _slots[i].Stack is { } other && other.Slot == entry.Slot)
                    free -= definition.Inputs[i].Count;
            }
            return free >= definition.Inputs[input].Count ? (ContractReason?)null : ContractReason.ContractTermsInvalid;
        }

        private ContractReason? EnchantRefusal(int input, InventorySlotEntry entry)
        {
            var rules = _config.Enchant!;
            if (rules.Item(entry.ItemNetId) is not { } item)
                return ContractReason.ContractTermsInvalid;
            if (input == ContractRules.EnchantItemSlot)
            {
                if (entry.InstanceId == 0 || item.Category != ItemCategory.Equipment || rules.JobOf(item).Length == 0
                    || entry.Enchant >= EnchantLimits.MaxLevel || !rules.TryMaterial(item, out _, out _))
                    return ContractReason.ContractTermsInvalid;
            }
            else if (EnchantItem is not { } target || !rules.TryMaterial(target, out var stone, out int count) || item.NetId != stone.NetId
                     || entry.Count < count)
                return ContractReason.ContractTermsInvalid;
            var offer = TradeRules.CheckOffer(item.Category, entry.Stolen || entry.StolenMark != 0, entry.Tainted, entry.NonTradable, false);
            return offer != TradeOfferCheck.Ok || entry.Reserved ? ContractReason.ContractNotTradable : (ContractReason?)null;
        }
    }

    /// <summary>
    /// The C window (spec 5.4, 9.3 C, 9.13; M4 decisions 10-12; WP-M4-10 part B): the own contracts as the server last pushed them
    /// (<see cref="ContractList"/>, the C badge too), the tab, the propose form, the one action waiting for its answer, and a
    /// crafter's portfolio read through a contract. Display only: the server owns the contracts and decides every request.
    /// </summary>
    public sealed class ContractWindowViewModel : ViewModel
    {
        private ContractEntry[] _mine = Array.Empty<ContractEntry>();

        public ContractWindowViewModel(ContractConfig config)
        {
            Form = new ContractFormModel(config);
        }

        public bool Open { get; private set; }

        public ContractTab Tab { get; private set; }

        public IReadOnlyList<ContractEntry> Mine => _mine;

        /// <summary>The C button's count (<see cref="ContractList.Badge"/>: the server's, never counted here).</summary>
        public int Badge { get; private set; }

        public bool HasList { get; private set; }

        public ContractFormModel Form { get; }

        /// <summary>The propose or cancel waiting for its answer; 0 when none (one at a time).</summary>
        public uint PendingAction { get; private set; }

        /// <summary>The contract the waiting cancel names (its toast shows the cost).</summary>
        public long PendingContract { get; private set; }

        public PortfolioView Portfolio { get; } = new PortfolioView();

        public void SetOpen(bool open)
        {
            if (Open == open)
                return;
            Open = open;
            if (!open)
            {
                Portfolio.Close();
                Form.PickerTarget = -1;
            }
            NotifyChanged();
        }

        public void SetTab(ContractTab tab)
        {
            if (Tab == tab)
                return;
            Tab = tab;
            Portfolio.Close();
            Form.PickerTarget = -1;
            NotifyChanged();
        }

        public void SetList(ContractList list)
        {
            _mine = list.Rows;
            Badge = list.Badge;
            HasList = true;
            NotifyChanged();
        }

        /// <summary>The own contract <paramref name="contractId"/> as last pushed, or null.</summary>
        public ContractEntry? Entry(long contractId) => Array.Find(_mine, e => e.ContractId == contractId);

        public void ActionSent(uint requestId, long contractId = 0)
        {
            PendingAction = requestId;
            PendingContract = contractId;
            NotifyChanged();
        }

        /// <summary>An answer: true when it was the waiting action's (it is no longer waiting).</summary>
        public bool Answered(ContractResult result)
        {
            if (PendingAction == 0 || result.RequestId != PendingAction)
                return false;
            PendingAction = 0;
            PendingContract = 0;
            NotifyChanged();
            return true;
        }

        /// <summary>The form or the bag changed: redraw.</summary>
        public void Touch() => NotifyChanged();

        public void PortfolioAsked(uint requestId)
        {
            Portfolio.Asked(requestId);
            NotifyChanged();
        }

        public bool PortfolioAnswer(PortfolioState state)
        {
            if (!Portfolio.Answer(state))
                return false;
            NotifyChanged();
            return true;
        }

        public void ClosePortfolio()
        {
            Portfolio.Close();
            NotifyChanged();
        }

        /// <summary>The connection ended: nothing is known until the next list.</summary>
        public void Clear()
        {
            _mine = Array.Empty<ContractEntry>();
            Badge = 0;
            HasList = false;
            Open = false;
            PendingAction = 0;
            PendingContract = 0;
            Portfolio.Close();
            Form.PickerTarget = -1;
            NotifyChanged();
        }
    }

    /// <summary>
    /// The contract board (spec 5.4, 9.13; M4 decision 10; WP-M4-10 part B), open at the board NPC: a page of open proposals with its
    /// cursor, the job and action filters, the row selected, the board request and the accept waiting for their answers, and the
    /// portfolio of the target-locked player. Display only: the server decides every acceptance.
    /// </summary>
    public sealed class ContractBoardViewModel : ViewModel
    {
        private readonly List<long> _cursors = new List<long>();
        private ContractEntry[] _rows = Array.Empty<ContractEntry>();

        public bool Open { get; private set; }

        public uint NpcEntityId { get; private set; }

        public IReadOnlyList<ContractEntry> Rows => _rows;

        public bool HasMore { get; private set; }

        /// <summary>The cursor of the page shown (0 the first page).</summary>
        public long After => _cursors.Count > 0 ? _cursors[_cursors.Count - 1] : 0;

        public int Page => _cursors.Count;

        public int Selected { get; private set; } = -1;

        public ContractEntry? SelectedRow => Selected >= 0 && Selected < _rows.Length ? _rows[Selected] : null;

        /// <summary>The job filter (a <c>JobDef.netId</c>); 0 any.</summary>
        public byte JobFilter { get; private set; }

        public ForgeKind KindFilter { get; private set; }

        public uint PendingBoard { get; private set; }

        public uint PendingAccept { get; private set; }

        public bool HasPage { get; private set; }

        public PortfolioView Portfolio { get; } = new PortfolioView();

        public void OpenFor(uint npcEntityId)
        {
            NpcEntityId = npcEntityId;
            Open = true;
            _cursors.Clear();
            _rows = Array.Empty<ContractEntry>();
            HasMore = false;
            HasPage = false;
            Selected = -1;
            Portfolio.Close();
            NotifyChanged();
        }

        public void Close()
        {
            if (!Open)
                return;
            Open = false;
            Portfolio.Close();
            NotifyChanged();
        }

        public void Select(int index)
        {
            Selected = index >= 0 && index < _rows.Length ? index : -1;
            NotifyChanged();
        }

        /// <summary>Steps the job filter through <paramref name="jobs"/> (0 any first); the board starts again at its first page.</summary>
        public void StepJob(IReadOnlyList<JobDef> jobs)
        {
            int at = -1;
            for (int i = 0; i < jobs.Count; i++)
            {
                if (jobs[i].NetId == JobFilter)
                    at = i;
            }
            JobFilter = at + 1 < jobs.Count ? (byte)jobs[at + 1].NetId : (byte)0;
            _cursors.Clear();
            NotifyChanged();
        }

        /// <summary>Steps the action filter: any, mold crafts, recipes.</summary>
        public void StepKind()
        {
            KindFilter = KindFilter == ForgeKind.None ? ForgeKind.Mold : KindFilter == ForgeKind.Mold ? ForgeKind.Recipe
                : KindFilter == ForgeKind.Recipe ? ForgeKind.Enchant : ForgeKind.None; // WP-M4-10 part C: enchants
            _cursors.Clear();
            NotifyChanged();
        }

        /// <summary>The next page after the last row shown (only when the server said there is more).</summary>
        public void NextPage()
        {
            if (!HasMore || _rows.Length == 0)
                return;
            _cursors.Add(_rows[_rows.Length - 1].ContractId);
        }

        public void FirstPage() => _cursors.Clear();

        public void BoardSent(uint requestId)
        {
            PendingBoard = requestId;
            NotifyChanged();
        }

        /// <summary>A page: true when it answers the board request waiting (an older one is dropped). A refusal's reason is the caller's.</summary>
        public bool OnPage(ContractBoard board)
        {
            if (PendingBoard == 0 || board.RequestId != PendingBoard)
                return false;
            PendingBoard = 0;
            if (board.Reason == ContractReason.None)
            {
                long selected = SelectedRow?.ContractId ?? 0;
                _rows = board.Rows;
                HasMore = board.HasMore;
                HasPage = true;
                Selected = Array.FindIndex(_rows, r => r.ContractId == selected);
            }
            NotifyChanged();
            return true;
        }

        public void AcceptSent(uint requestId)
        {
            PendingAccept = requestId;
            NotifyChanged();
        }

        /// <summary>An answer: true when it was the waiting accept's.</summary>
        public bool Answered(ContractResult result)
        {
            if (PendingAccept == 0 || result.RequestId != PendingAccept)
                return false;
            PendingAccept = 0;
            NotifyChanged();
            return true;
        }

        public void PortfolioAsked(uint requestId)
        {
            Portfolio.Asked(requestId);
            NotifyChanged();
        }

        public bool PortfolioAnswer(PortfolioState state)
        {
            if (!Portfolio.Answer(state))
                return false;
            NotifyChanged();
            return true;
        }

        public void ClosePortfolio()
        {
            Portfolio.Close();
            NotifyChanged();
        }

        public void Clear()
        {
            Open = false;
            _rows = Array.Empty<ContractEntry>();
            _cursors.Clear();
            HasMore = false;
            HasPage = false;
            Selected = -1;
            PendingBoard = PendingAccept = 0;
            Portfolio.Close();
            NotifyChanged();
        }
    }

    /// <summary>
    /// The own portfolio of the Jobs window's portfolio tab (spec 5.4 R-5.4-04, M4 decision 11; WP-M4-10 part B): the state last
    /// read, the pin picker, and the request waiting (a view, pin, unpin or the hide flag; each is answered by a fresh state after its
    /// commit). Display only.
    /// </summary>
    public sealed class OwnPortfolioViewModel : ViewModel
    {
        public PortfolioState? State { get; private set; }

        public uint Pending { get; private set; }

        /// <summary>The showcase slot the pin picker is open for; -1 none.</summary>
        public int PinTarget { get; private set; } = -1;

        public void Sent(uint requestId)
        {
            Pending = requestId;
            NotifyChanged();
        }

        /// <summary>An answer: true when it was the waiting request's; a refusal's reason is returned for the toast.</summary>
        public bool Answer(PortfolioState state, out string? refusal)
        {
            refusal = null;
            if (Pending == 0 || state.RequestId != Pending)
                return false;
            Pending = 0;
            if (state.Reason != ContractReason.None)
                refusal = state.Reason.ToString();
            else
                State = state;
            NotifyChanged();
            return true;
        }

        public void OpenPicker(int showcaseSlot)
        {
            PinTarget = PinTarget == showcaseSlot ? -1 : showcaseSlot;
            NotifyChanged();
        }

        public void ClosePicker()
        {
            if (PinTarget < 0)
                return;
            PinTarget = -1;
            NotifyChanged();
        }

        public void Clear()
        {
            State = null;
            Pending = 0;
            PinTarget = -1;
            NotifyChanged();
        }
    }
}
