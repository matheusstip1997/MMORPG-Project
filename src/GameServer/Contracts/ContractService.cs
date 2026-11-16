using System.Globalization;
using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Jobs;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Contracts;
using Shared.Simulation.Crafting;
using Shared.Simulation.Economy;
using Shared.Simulation.Items;
using Shared.Util;

namespace GameServer.Contracts;

/// <summary>One contract request as the world hands it over in phase 1, after the gate (the board for an acceptance).</summary>
public readonly record struct ContractAsk(long CharacterId, int Connection, uint RequestId, byte RegionIndex, bool Blocked, string OpKey);

/// <summary>
/// <b>The only writer of contracts</b> (spec 5.4 R-5.4-01..04, M4 decision 10, user answers 2026-11-14 and 2026-11-15; WP-M4-10),
/// on the world thread. It keeps every open contract in memory (loaded at world start) and decides every race there; the
/// database guards are the backstop. The state machine is the spec's: <c>Proposed → Accepted → InProgress → Completed | Expired |
/// Cancelled | Failed</c>.
/// <list type="bullet">
/// <item><b>Propose</b> (the customer, anywhere): the gold and the units of the customer's slots go into one escrow
/// (<see cref="EscrowService.Hold"/>) and the <c>contracts</c> row is written, both in the customer's save; answered after the
/// commit. The hold is at the proposal (user answer 2026-11-15): the acceptance never writes the customer.</item>
/// <item><b>Accept</b> (a crafter at the contract board): the job and level, the crafter's cap and the per-pair cap per UTC day
/// (R-5.4-04), then a guarded update in the crafter's save; the deadline runs from now (user answer 2026-11-15).</item>
/// <item><b>The forge</b> (<see cref="IForgeContracts"/>): a session of the crafter opens the contract (in progress; the first start
/// sets <c>work_started</c>), reads the customer's units from the escrow, and its finish settles the contract in the crafter's
/// save: the escrow's gold to the crafter less the contract fee (<see cref="FeeService.Apply"/>, last), the escrowed units consumed,
/// the output to the customer by mail (always by mail, user answer 2026-11-15), the row completed and the crafter's portfolio
/// counted. A lost session leaves the contract accepted with everything in escrow; the deadline runs on.</item>
/// <item><b>Cancel</b>: the customer's (an open proposal, or accepted without work started; free) and every <b>expiry</b> release
/// the escrow to the customer's mail and close the row in one transaction off the world thread (<see cref="EscrowService.ReleaseToMail"/>
/// with a holder close); the crafter's (accepted; after work started it costs <c>ContractSettings.cancelCostPercent</c> of the
/// gold, from the crafter's own gold, a sink) is one crafter save: the cost, the escrow into one return mail, the row closed.</item>
/// </list>
/// Nothing names the customer or the crafter to anyone (rule 8; TODO(open) a rule for showing them, as the auction).
/// </summary>
public sealed class ContractService : IContractBatchSource, IForgeContracts
{
    /// <summary>Answers remembered by operation key, so a repeated request gets its first answer.</summary>
    private const int RememberedAnswers = 1024;

    private readonly ContractSettings _settings;
    private readonly InventoryService _inventory;
    private readonly EscrowService _escrow;
    private readonly FeeService _fees;
    private readonly MailService _mail;
    private readonly ForgeRules _forge;
    private readonly IJobQuery? _jobs;
    private readonly IClock _clock;
    private readonly EventBus? _events;
    private readonly long _maxGold;
    private readonly Dictionary<long, Contract> _byId = new();
    private readonly Dictionary<string, long> _byEscrow = new(StringComparer.Ordinal);
    private readonly Dictionary<long, HashSet<long>> _byCustomer = new();
    private readonly Dictionary<long, HashSet<long>> _byCrafter = new();
    private readonly SortedSet<(long DueMs, long ContractId)> _expiry = new();
    private readonly Dictionary<(long Day, long Low, long High), int> _pairs = new();
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly Dictionary<long, InFlight> _inFlight = new();
    private readonly Dictionary<string, ContractResult?> _answers = new(StringComparer.Ordinal);
    private readonly Queue<string> _answerOrder = new();
    private readonly List<long> _due = new();
    private readonly Dictionary<long, List<ContractChargeRow>> _chargesWaiting = new(); // crafter -> expiry charges to collect
    private readonly HashSet<string> _chargesKnown = new(StringComparer.Ordinal); // waiting or collecting in this run
    private readonly List<long> _chargeCrafters = new();
    private long _nextId = 1;

    public ContractService(ContractSettings settings, InventoryService inventory, EscrowService escrow, FeeService fees, MailService mail,
        ForgeRules forge, IJobQuery? jobs, IClock clock, EventBus? events, long maxGold)
    {
        _settings = settings;
        _inventory = inventory;
        _escrow = escrow;
        _fees = fees;
        _mail = mail;
        _forge = forge;
        _jobs = jobs;
        _clock = clock;
        _events = events;
        _maxGold = maxGold;
    }

    public ContractSettings Settings => _settings;

    public ForgeRules Forge => _forge;

    /// <summary>True once <see cref="Load"/> ran: before that no request is taken (a contract id could repeat).</summary>
    public bool Loaded { get; private set; }

    /// <summary>Asks a character's save at once (the scheduler's <c>RequestSave</c>; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>An answer to the ask (its character and connection), once its operation committed (set by the world: the outbox).</summary>
    public Action<ContractAsk, ContractResult>? Answer { get; set; }

    /// <summary>A contract of this character changed (set by the world: its <c>ContractList</c> is pushed when it is online).</summary>
    public Action<long>? Changed { get; set; }

    /// <summary>Ends the running forge session of a crafter at its contract's deadline (set by the world; the session tells <see cref="Closed"/>).</summary>
    public Action<long>? AbortSession { get; set; }

    /// <summary>The region index of an online character, for the fee quote (set by the world; 0 when unknown).</summary>
    public Func<long, byte>? RegionOf { get; set; }

    /// <summary>The portfolio's pending writes of a character, merged into its save's contract batch (set by the world).</summary>
    public PortfolioService? Portfolio { get; set; }

    /// <summary>Contracts open in memory (any state but unknown); for tests and tools.</summary>
    public int OpenCount => _byId.Values.Count(c => c.State != State.Unknown);

    /// <summary>Starts with the open contracts of the database (world start), the highest id ever written and today's acceptances.</summary>
    public void Load(ContractLoad load)
    {
        foreach (var row in load.Open)
        {
            if (!_escrow.IsHeld(row.EscrowId))
                continue; // its escrow is not held (an inconsistent row): never touched by this run; the database keeps it
            Add(new Contract(row) { State = row.State == ContractDbState.Accepted ? State.Accepted : State.Proposed });
        }
        foreach (var (customer, crafter, acceptedMs) in load.AcceptedToday)
            CountPair(ContractMath.UtcDay(acceptedMs), customer, crafter, +1);
        _nextId = Math.Max(_nextId, load.MaxContractId + 1);
        Loaded = true;
    }

    /// <summary>The contract with that id while this world holds it (read-only for views and tests).</summary>
    public ContractRow? Find(long contractId) => _byId.TryGetValue(contractId, out var c) ? c.Row : null;

    /// <summary>The status a client sees of a contract this world holds.</summary>
    public ContractStatus StatusOf(long contractId) => _byId.TryGetValue(contractId, out var c) ? Status(c) : ContractStatus.None;

    /// <summary>
    /// The customer proposes a contract (phase 1). Returns the refusal (or a repeated request's first answer) to send now, or null when
    /// it waits for the customer's save (answered through <see cref="Answer"/>).
    /// </summary>
    public ContractResult? Propose(in ContractAsk ask, ForgeKind kind, int defNetId, long gold, int deadlineHours,
        IReadOnlyList<ContractSlot> slots)
    {
        if (Repeated(ask.OpKey, out var first))
            return first;
        long customer = ask.CharacterId;
        if (!Loaded || !_inventory.IsAttached(customer) || !_inventory.HasGold(customer) || !_mail.IsAttached(customer))
            return Refuse(ask, ContractAction.Propose, ContractReason.InventoryUnavailable);
        if (ask.Blocked)
            return Refuse(ask, ContractAction.Propose, ContractReason.InCombat);
        if (!ContractMath.DeadlineAllowed(deadlineHours, _settings.MinDeadlineHours, _settings.MaxDeadlineHours))
            return Refuse(ask, ContractAction.Propose, ContractReason.ContractDeadlineInvalid);
        var reason = ContractMath.CheckGold(gold, _settings.MinGold, _maxGold);
        if (reason != ContractReason.None)
            return Refuse(ask, ContractAction.Propose, reason);
        _forge.TryGet(kind, defNetId, out var definition);
        reason = ContractRules.CheckTerms(definition, slots, netId => _forge.Item(netId));
        if (reason != ContractReason.None)
            return Refuse(ask, ContractAction.Propose, reason);
        if (CountOpen(customer) >= _settings.MaxOpenPerCustomer)
            return Refuse(ask, ContractAction.Propose, ContractReason.ContractTooMany);
        if (!_mail.HasRoom(customer))
            return Refuse(ask, ContractAction.Propose, ContractReason.MailboxFull); // a contract is a new mail source (spec 4.5)
        if (_inventory.Gold(customer) < gold)
            return Refuse(ask, ContractAction.Propose, ContractReason.NotEnoughGold);

        // The customer's slots: the units of each named bag place, tradable (R-5.4-03), unreserved; two slots may name one stack.
        var items = _inventory.CurrentInventory(customer);
        var held = new Dictionary<InventoryLocation, int>();
        var rows = new List<ContractSlotRow>(slots.Count);
        foreach (var slot in slots)
        {
            if (slot.Supplier == ContractSupplier.Crafter)
            {
                rows.Add(new ContractSlotRow(ContractSupplier.Crafter, slot.ItemNetId, 0, slot.Count));
                continue;
            }
            var at = new InventoryLocation(slot.Container, slot.Slot);
            if (!items.TryIndex(at.Container, out int index) || at.Slot < 0 || at.Slot >= items.BagAt(index).SlotCount)
                return Refuse(ask, ContractAction.Propose, ContractReason.ContractChanged);
            var stack = items.BagAt(index)[at.Slot];
            if (stack.IsEmpty || stack.ItemNetId != slot.ItemNetId || !_inventory.Rules.TryGetItem(stack.ItemNetId, out var def))
                return Refuse(ask, ContractAction.Propose, ContractReason.ContractChanged);
            bool tainted = stack.IsInstance && _inventory.TryGetLatestProperties(customer, stack.InstanceId, out var properties)
                && properties.Tainted;
            reason = ContractMath.ReasonOf(TradeRules.CheckOffer(def.Category, stack.Stolen || stack.StolenFrom != 0, tainted,
                _inventory.IsNonTradable(customer, stack), items.Rule(index).IsEquipped));
            if (reason != ContractReason.None)
                return Refuse(ask, ContractAction.Propose, reason);
            held[at] = held.GetValueOrDefault(at) + slot.Count;
            if (_inventory.Unreserved(customer, at) < held[at] || (stack.IsInstance && held[at] != 1))
                return Refuse(ask, ContractAction.Propose, ContractReason.ContractChanged);
            rows.Add(new ContractSlotRow(ContractSupplier.Customer, stack.ItemNetId, stack.Rarity, slot.Count));
        }

        long id = _nextId;
        string escrowId = ask.OpKey;
        var hold = _escrow.Hold(customer, held.Select(h => new ReserveItem(h.Key, h.Value)).ToList(), gold, EscrowHolderKind.Contract,
            id.ToString(CultureInfo.InvariantCulture), escrowId);
        if (!hold.Applied)
            return Refuse(ask, ContractAction.Propose, hold.Refusal == EscrowRefusal.NotOnline
                ? ContractReason.InventoryUnavailable : ContractReason.ContractChanged);
        _nextId++;
        long now = _clock.UtcNowMs;
        var output = definition!.Output!;
        var row = new ContractRow(id, ask.OpKey, customer, 0, escrowId, kind, defNetId, definition.Id, definition.JobId,
            (short)((int)output.Category + 1), gold, deadlineHours, rows, now, ContractMath.OpenUntil(now, _settings.ProposalOpenHours), 0, 0,
            false, ContractDbState.Proposed);
        var contract = new Contract(row) { State = State.Proposing, Ask = ask };
        Add(contract);
        PendingOf(customer).Proposals.Add(id);
        Remember(ask.OpKey, null);
        RequestSave?.Invoke(customer, SaveReason.Class1);
        return null;
    }

    /// <summary>A crafter accepts an open proposal at the board (phase 1). Null when it waits for the crafter's save.</summary>
    public ContractResult? Accept(in ContractAsk ask, long contractId)
    {
        if (Repeated(ask.OpKey, out var first))
            return first;
        long crafter = ask.CharacterId;
        if (!Loaded || !_inventory.IsAttached(crafter))
            return Refuse(ask, ContractAction.Accept, ContractReason.InventoryUnavailable, contractId);
        if (ask.Blocked)
            return Refuse(ask, ContractAction.Accept, ContractReason.InCombat, contractId);
        if (!_byId.TryGetValue(contractId, out var contract) || contract.State is State.Proposing or State.Unknown)
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractNotFound, contractId);
        if (contract.State == State.Closing)
            return Refuse(ask, ContractAction.Accept, contract.ClosingAs == ContractDbState.Expired
                ? ContractReason.ContractExpired : ContractReason.ContractNotFound, contractId);
        if (contract.State != State.Proposed)
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractStateInvalid, contractId);
        var row = contract.Row;
        long now = _clock.UtcNowMs;
        if (now >= row.OpenUntilUtcMs)
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractExpired, contractId);
        if (row.CustomerId == crafter)
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractOwn, contractId);
        if (!_forge.TryGet(row.Kind, row.DefNetId, out var definition)
            || (_jobs?.JobLevel(crafter, definition.JobId) ?? 0) < Math.Max(1, definition.MinJobLevel))
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractJobRequired, contractId);
        if (CountActive(crafter) >= _settings.MaxActivePerCrafter)
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractTooMany, contractId);
        long day = ContractMath.UtcDay(now);
        if (!ContractMath.PairAllows(PairCount(day, row.CustomerId, crafter), _settings.PairCapPerDay))
            return Refuse(ask, ContractAction.Accept, ContractReason.ContractPairCap, contractId);

        contract.State = State.Accepting;
        contract.Ask = ask;
        contract.AcceptingBy = crafter;
        contract.AcceptingAtMs = now;
        CountPair(day, row.CustomerId, crafter, +1);
        IndexOf(_byCrafter, crafter).Add(contractId);
        PendingOf(crafter).Writes.Add(new ContractWrite(ContractWriteKind.Accept, contractId, crafter, ask.OpKey, now,
            ContractMath.DeadlineAt(now, row.DeadlineHours)));
        Remember(ask.OpKey, null);
        RequestSave?.Invoke(crafter, SaveReason.Class1);
        return null;
    }

    /// <summary>
    /// The customer or the crafter cancels (phase 1). The customer's goes back by mail off the world thread (free); the crafter's is
    /// one crafter save with the cost after work started. Null when it waits.
    /// </summary>
    public ContractResult? Cancel(in ContractAsk ask, long contractId)
    {
        if (Repeated(ask.OpKey, out var first))
            return first;
        if (!Loaded)
            return Refuse(ask, ContractAction.Cancel, ContractReason.InventoryUnavailable, contractId);
        if (!_byId.TryGetValue(contractId, out var contract) || contract.State is State.Proposing or State.Unknown)
            return Refuse(ask, ContractAction.Cancel, ContractReason.ContractNotFound, contractId);
        var row = contract.Row;
        var role = row.CustomerId == ask.CharacterId ? ContractRole.Customer
            : row.CrafterId != 0 && row.CrafterId == ask.CharacterId ? ContractRole.Crafter
            : ContractRole.None;
        if (role == ContractRole.None)
            return Refuse(ask, ContractAction.Cancel, ContractReason.ContractNotFound, contractId);
        if (contract.State == State.Closing)
            return Refuse(ask, ContractAction.Cancel, contract.ClosingAs == ContractDbState.Expired
                ? ContractReason.ContractExpired : ContractReason.ContractNotFound, contractId);
        if (contract.State is State.Accepting or State.Settling)
            return Refuse(ask, ContractAction.Cancel, ContractReason.ContractBusy, contractId);
        var reason = ContractMath.CheckCancel(Status(contract), row.WorkStarted, role, out bool costs);
        if (reason != ContractReason.None)
            return Refuse(ask, ContractAction.Cancel, reason, contractId);
        if (role == ContractRole.Customer)
        {
            if (!StartClose(contract, ContractDbState.Cancelled, ask.OpKey))
                return Refuse(ask, ContractAction.Cancel, ContractReason.ContractBusy, contractId);
            contract.CloseAsk = ask;
            Remember(ask.OpKey, null);
            return null;
        }

        long crafter = ask.CharacterId;
        if (!_inventory.IsAttached(crafter) || !_inventory.HasGold(crafter) || !_mail.IsAttached(crafter))
            return Refuse(ask, ContractAction.Cancel, ContractReason.InventoryUnavailable, contractId);
        if (ask.Blocked)
            return Refuse(ask, ContractAction.Cancel, ContractReason.InCombat, contractId);
        long cost = costs ? ContractMath.CancelCost(row.Gold, _settings.CancelCostPercent) : 0;
        if (_inventory.Gold(crafter) < cost)
            return Refuse(ask, ContractAction.Cancel, ContractReason.NotEnoughGold, contractId);
        if (_escrow.Find(row.EscrowId) is not { } escrowed || !_escrow.IsHeld(row.EscrowId))
            return Refuse(ask, ContractAction.Cancel, ContractReason.ContractBusy, contractId);
        // The return mail is checked before anything moves (a refused send could not be undone after the release).
        var draft = ReturnDraft(row, escrowed.Items, escrowed.Gold, ask.OpKey + ":return");
        if (_mail.Rules.Check(draft) is { } error)
            throw new InvalidOperationException($"contract {contractId}: the return mail is invalid: {error}");
        var release = _escrow.ReleaseIntoMail(row.EscrowId, crafter, row.CustomerId, ask.OpKey, out _, out _);
        if (!release.Applied)
            return Refuse(ask, ContractAction.Cancel, ContractReason.ContractBusy, contractId);
        if (cost > 0 && !_inventory.TryRemoveGold(crafter, cost, ask.OpKey + ":cost", InventoryReason.ContractCancel,
                $"contract:{contractId}").Applied)
            throw new InvalidOperationException($"contract {contractId}: the cancel cost was checked against the gold and still refused");
        if (!_mail.Send(crafter, draft).Accepted)
            throw new InvalidOperationException($"contract {contractId}: the checked return mail was refused");
        long now = _clock.UtcNowMs;
        contract.State = State.Settling;
        contract.ClosingAs = ContractDbState.Cancelled;
        contract.Ask = ask;
        contract.Cost = cost;
        var pending = PendingOf(crafter);
        pending.Writes.Add(new ContractWrite(ContractWriteKind.CrafterCancel, contractId, crafter, ask.OpKey, now, Cost: cost));
        pending.Counters.Add(new PortfolioIncrement(crafter, row.JobId, row.OutputCategory, 0, PortfolioOutcome.ContractCancelled,
            ask.OpKey + ":portfolio", now));
        pending.Settles.Add(contractId);
        Remember(ask.OpKey, null);
        RequestSave?.Invoke(crafter, SaveReason.Class1);
        return null;
    }

    /// <summary>One board page: open proposals oldest first after <paramref name="afterId"/>, filtered by job and kind.</summary>
    public IReadOnlyList<ContractRow> Board(int jobNetId, ForgeKind kind, long afterId, int limit, Func<string, int> jobNetIdOf)
    {
        long now = _clock.UtcNowMs;
        return _byId.Values
            .Where(c => c.State == State.Proposed && c.Row.ContractId > afterId && now < c.Row.OpenUntilUtcMs
                && (kind == ForgeKind.None || c.Row.Kind == kind) && (jobNetId == 0 || jobNetIdOf(c.Row.JobId) == jobNetId))
            .OrderBy(c => c.Row.ContractId).Take(limit).Select(c => c.Row).ToList();
    }

    /// <summary>A character's open contracts as customer and crafter, oldest first.</summary>
    public IReadOnlyList<ContractRow> Mine(long characterId) =>
        Of(_byCustomer.GetValueOrDefault(characterId)).Concat(Of(_byCrafter.GetValueOrDefault(characterId)))
            .Where(c => c.State != State.Unknown).Select(c => c.Row).Distinct().OrderBy(r => r.ContractId).ToList();

    /// <summary>The C button's count: the character's open contracts on either side.</summary>
    public int Badge(long characterId) => Mine(characterId).Count;

    /// <summary>The view of one contract for <paramref name="viewer"/> (its role, if any).</summary>
    public ContractEntry Entry(ContractRow row, long viewer)
    {
        var contract = _byId.GetValueOrDefault(row.ContractId);
        return new ContractEntry
        {
            ContractId = row.ContractId,
            Status = contract == null ? ContractStatus.None : Status(contract),
            Role = row.CustomerId == viewer ? ContractRole.Customer : row.CrafterId == viewer && viewer != 0 ? ContractRole.Crafter : ContractRole.None,
            Kind = row.Kind,
            DefNetId = (ushort)row.DefNetId,
            Gold = row.Gold,
            DeadlineHours = (byte)Math.Min(byte.MaxValue, row.DeadlineHours),
            OpenUntilUtcMs = row.State == ContractDbState.Proposed ? row.OpenUntilUtcMs : 0,
            DeadlineUtcMs = row.DeadlineUtcMs,
            WorkStarted = row.WorkStarted,
            Slots = row.Slots.Select(s => new ContractSlot
            {
                Supplier = s.Supplier, ItemNetId = (ushort)s.ItemNetId, Rarity = (byte)s.Rarity, Count = (ushort)s.Count,
            }).ToArray(),
        };
    }

    /// <summary>
    /// The expiry job (phase 7): proposals past their time on the board and accepted contracts past their deadline start closing,
    /// at most <c>ContractSettings.expiryBatch</c> per tick, oldest first. A running forge session is ended first; a contract whose
    /// operation is saving waits for it.
    /// </summary>
    public void ExpireDue(long nowMs)
    {
        if (!Loaded)
            return;
        CollectCharges();
        _due.Clear();
        foreach (var (dueMs, id) in _expiry)
        {
            if (dueMs > nowMs || _due.Count >= _settings.ExpiryBatch)
                break;
            if (_byId.TryGetValue(id, out var contract) && contract.State is State.Proposed or State.Accepted or State.InProgress)
                _due.Add(id);
        }
        foreach (long id in _due)
        {
            var contract = _byId[id];
            if (contract.State == State.InProgress)
                AbortSession?.Invoke(contract.Row.CrafterId); // the session tells Closed: accepted again
            if (contract.State is State.Proposed or State.Accepted)
                StartClose(contract, ContractDbState.Expired, ExpireKey(id));
        }
    }

    /// <summary>Test and dev console hook: the character's open contracts (either side) come due now, through <see cref="ExpireDue"/>.</summary>
    public int ExpireNow(long characterId, long nowMs)
    {
        int moved = 0;
        foreach (var contract in Of(_byCustomer.GetValueOrDefault(characterId)).Concat(Of(_byCrafter.GetValueOrDefault(characterId)))
                     .Distinct().Where(c => c.State is State.Proposed or State.Accepted or State.InProgress).ToList())
        {
            _expiry.Remove((DueOf(contract.Row), contract.Row.ContractId));
            contract.Row = contract.Row.State == ContractDbState.Proposed
                ? contract.Row with { OpenUntilUtcMs = Math.Min(contract.Row.OpenUntilUtcMs, nowMs) }
                : contract.Row with { DeadlineUtcMs = Math.Min(contract.Row.DeadlineUtcMs, nowMs) };
            _expiry.Add((DueOf(contract.Row), contract.Row.ContractId));
            moved++;
        }
        return moved;
    }

    /// <summary>
    /// <see cref="EscrowService.MailReleaseSettled"/>: a customer's cancel or an expiry committed (the row is closed in the same
    /// transaction) or found the escrow gone (the contract becomes unknown until a restart).
    /// </summary>
    public void OnMailReleaseSettled(string escrowId, bool released)
    {
        if (!_byEscrow.TryGetValue(escrowId, out long id) || !_byId.TryGetValue(id, out var contract) || contract.State != State.Closing)
            return;
        var row = contract.Row;
        if (!released)
        {
            MarkUnknown(contract);
            if (contract.CloseAsk is { } failed)
                Deliver(failed, new ContractResult
                {
                    RequestId = failed.RequestId, Action = ContractAction.Cancel, Reason = ContractReason.ContractBusy, ContractId = id,
                });
            return;
        }
        Remove(contract);
        if (contract.ClosingAs == ContractDbState.Expired)
        {
            // "Yes, charge on expiry" (user, 2026-11-16): after work started the crafter owes the cancel cost. The charge committed
            // with this close (ContractRepository.CloseAsync); it is collected in the crafter's own save, now if it is online, else
            // at its next login (AttachCharges).
            long charged = row.CrafterId != 0 && row.WorkStarted ? ContractMath.CancelCost(row.Gold, _settings.CancelCostPercent) : 0;
            if (charged > 0)
                AttachCharges(row.CrafterId, new[]
                {
                    new ContractChargeRow(ContractRepository.ChargeKeyOf(ExpireKey(id)), id, row.CrafterId, charged),
                });
            _events?.Publish(new ContractExpired(id, row.CustomerId, row.CrafterId, row.Gold, charged), row.CustomerId);
        }
        else
            _events?.Publish(new ContractCancelled(id, row.CustomerId, row.CrafterId, row.Gold, false, 0), row.CustomerId);
        if (contract.CloseAsk is { } ask)
            Deliver(ask, new ContractResult { RequestId = ask.RequestId, Action = ContractAction.Cancel, ContractId = id, Gold = row.Gold });
        Notify(row);
    }

    // ---- The forge (IForgeContracts) ----

    public ForgeReason Open(long crafterId, long contractId, ForgeKind kind, int defNetId, out ContractSessionTerms? terms)
    {
        terms = null;
        if (!_byId.TryGetValue(contractId, out var contract) || contract.Row.CrafterId != crafterId
            || contract.State is State.Proposing or State.Proposed or State.Accepting or State.Unknown)
            return ForgeReason.ContractNotFound;
        if (contract.State == State.Closing)
            return contract.ClosingAs == ContractDbState.Expired ? ForgeReason.ContractExpired : ForgeReason.ContractNotFound;
        if (contract.State != State.Accepted)
            return ForgeReason.ContractStateInvalid;
        var row = contract.Row;
        if (_clock.UtcNowMs >= row.DeadlineUtcMs)
            return ForgeReason.ContractExpired;
        if (row.Kind != kind || row.DefNetId != defNetId || !_escrow.IsHeld(row.EscrowId))
            return ForgeReason.ContractStateInvalid;
        contract.State = State.InProgress;
        if (!row.WorkStarted)
        {
            // The first start: the customer can no longer cancel, the crafter's cancel costs (user answer 2026-11-15). Saved at once,
            // not waited for: a crash before it commits only makes a later cancel free.
            contract.Row = row with { WorkStarted = true };
            PendingOf(crafterId).Writes.Add(new ContractWrite(ContractWriteKind.WorkStarted, contractId, crafterId,
                $"contractStart:{contractId}", _clock.UtcNowMs));
            RequestSave?.Invoke(crafterId, SaveReason.Class1);
        }
        terms = new ContractSessionTerms(contractId, row.CustomerId, row.Slots);
        Notify(contract.Row);
        return ForgeReason.None;
    }

    public void Closed(long contractId)
    {
        if (_byId.TryGetValue(contractId, out var contract) && contract.State == State.InProgress)
        {
            contract.State = State.Accepted;
            Notify(contract.Row);
        }
    }

    public ForgeReason PrepareSettle(long crafterId, long contractId, ContractForgeOutput output)
    {
        if (!_byId.TryGetValue(contractId, out var contract) || contract.Row.CrafterId != crafterId || contract.State != State.InProgress)
            return ForgeReason.ContractStateInvalid;
        var row = contract.Row;
        if (!_inventory.IsAttached(crafterId) || !_inventory.HasGold(crafterId) || !_mail.IsAttached(crafterId)
            || _escrow.Find(row.EscrowId) is not { } escrowed || !_escrow.IsHeld(row.EscrowId))
            return ForgeReason.ContractStateInvalid;
        if (!_inventory.GoldFits(crafterId, escrowed.Gold))
            return ForgeReason.GoldCapReached;
        var draft = OutputDraft(row, output, escrowed.Items);
        if (draft.Items.Count > 0 && _mail.Rules.Check(draft) is { } error)
            throw new InvalidOperationException($"contract {contractId}: the output mail is invalid: {error}");
        return ForgeReason.None;
    }

    public void Settle(long crafterId, long contractId, ContractForgeOutput output)
    {
        var contract = _byId[contractId];
        var row = contract.Row;
        string key = output.OpKey;
        var settled = _escrow.Settle(row.EscrowId, crafterId, key + ":pay", out var items);
        if (!settled.Applied)
            throw new InvalidOperationException($"contract {contractId}: the checked settlement was refused ({settled.Refusal})");
        var fee = _fees.Quote(crafterId, RegionOf?.Invoke(crafterId) ?? 0, FeeType.Contract, row.Gold);
        if (!_fees.Apply(crafterId, fee, key, $"contract:{contractId}").Ok)
            throw new InvalidOperationException($"contract {contractId}: the contract fee exceeds the payment just received");
        var draft = OutputDraft(row, output, items);
        if (draft.Items.Count > 0 && !_mail.Send(crafterId, draft).Accepted)
            throw new InvalidOperationException($"contract {contractId}: the checked output mail was refused");
        long now = _clock.UtcNowMs;
        contract.State = State.Settling;
        contract.ClosingAs = ContractDbState.Completed;
        contract.Fee = fee.Total;
        contract.Output = output;
        var pending = PendingOf(crafterId);
        pending.Writes.Add(new ContractWrite(ContractWriteKind.Complete, contractId, crafterId, key, now, Fee: fee.Total));
        pending.Counters.Add(new PortfolioIncrement(crafterId, row.JobId, output.Success ? row.OutputCategory : (short)0,
            output.Success ? output.Rarity : (short)0, PortfolioOutcome.ContractCompleted, key + ":portfolio", now));
        pending.Settles.Add(contractId);
        RequestSave?.Invoke(crafterId, SaveReason.Class1);
    }

    // ---- The expiry charges (user answers 2026-11-16) ----

    /// <summary>The release key of a contract's expiry (its charge key follows from it, <c>ContractRepository.ChargeKeyOf</c>).</summary>
    public static string ExpireKey(long contractId) => string.Create(CultureInfo.InvariantCulture, $"contractExpire:{contractId}");

    /// <summary>
    /// A crafter's expiry charges to collect: read at its login (pending in the database), or known now because an expiry with work
    /// started committed. Each is collected once, on the next tick the crafter is online (a charge already known is ignored).
    /// </summary>
    public void AttachCharges(long crafterId, IReadOnlyList<ContractChargeRow> charges)
    {
        foreach (var charge in charges)
        {
            if (charge.Amount <= 0 || !_chargesKnown.Add(charge.ChargeKey))
                continue;
            if (!_chargesWaiting.TryGetValue(crafterId, out var list))
                _chargesWaiting.Add(crafterId, list = new List<ContractChargeRow>());
            list.Add(charge);
        }
    }

    /// <summary>Charges waiting for a collection (tests and tools).</summary>
    public int ChargesWaiting => _chargesWaiting.Values.Sum(l => l.Count);

    /// <summary>
    /// Collects the waiting charges of online crafters (phase 7): the gold the crafter holds, up to the amount, taken through
    /// <see cref="InventoryService.TryRemoveGold"/> (reason <c>ContractCancel</c>, a sink; the rest dropped, user answer 2026-11-16)
    /// and the guarded collected row, both in the crafter's save, asked at once. A crafter not online keeps its charges for its next
    /// login (the database keeps them pending).
    /// </summary>
    private void CollectCharges()
    {
        if (_chargesWaiting.Count == 0)
            return;
        _chargeCrafters.Clear();
        _chargeCrafters.AddRange(_chargesWaiting.Keys);
        foreach (long crafter in _chargeCrafters)
        {
            if (!_inventory.IsAttached(crafter) || !_inventory.HasGold(crafter))
                continue;
            var list = _chargesWaiting[crafter];
            _chargesWaiting.Remove(crafter);
            var pending = PendingOf(crafter);
            foreach (var charge in list)
            {
                long taken = ContractMath.ChargeTaken(charge.Amount, _inventory.Gold(crafter));
                if (taken > 0 && !_inventory.TryRemoveGold(crafter, taken, charge.ChargeKey + ":gold", InventoryReason.ContractCancel,
                        $"contract:{charge.ContractId}").Applied)
                    throw new InvalidOperationException($"contract {charge.ContractId}: the expiry charge was checked against the gold and still refused");
                pending.Charges.Add((charge, taken));
            }
            RequestSave?.Invoke(crafter, SaveReason.Class1);
        }
    }

    // ---- The save ----

    public ContractBatch? TakeBatch(long characterId, Guid saveId)
    {
        _pending.Remove(characterId, out var pending);
        var portfolio = Portfolio?.TakePending(characterId);
        if (pending == null && portfolio == null)
            return null;
        pending ??= new Pending();
        _inFlight[characterId] = new InFlight(saveId, pending, portfolio);
        long now = _clock.UtcNowMs;
        return new ContractBatch(pending.Proposals.Select(id => _byId[id].Row).ToList(), pending.Writes.ToList(),
            pending.Counters.Concat(portfolio?.Counters ?? (IEnumerable<PortfolioIncrement>)Array.Empty<PortfolioIncrement>()).ToList(),
            portfolio?.Showcase ?? (IReadOnlyList<ShowcaseWrite>)Array.Empty<ShowcaseWrite>(), portfolio?.Hidden,
            pending.Charges.Select(c => new ContractChargeWrite(c.Row.ChargeKey, c.Taken, now)).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlight.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        _inFlight.Remove(characterId);
        var batch = inFlight.Batch;
        foreach (long id in batch.Proposals)
        {
            if (!_byId.TryGetValue(id, out var contract) || contract.State != State.Proposing)
                continue;
            contract.State = State.Proposed;
            var row = contract.Row;
            _events?.Publish(new ContractProposed(id, row.CustomerId, (byte)row.Kind, row.DefNetId, row.Gold, row.DeadlineHours,
                row.Slots.Where(s => s.Supplier == ContractSupplier.Customer).Sum(s => s.Count), row.OpenUntilUtcMs), row.CustomerId);
            if (contract.Ask is { } ask)
                Deliver(ask, new ContractResult { RequestId = ask.RequestId, Action = ContractAction.Propose, ContractId = id, Gold = row.Gold });
            Notify(row);
        }
        foreach (var write in batch.Writes)
        {
            if (!_byId.TryGetValue(write.ContractId, out var contract))
                continue;
            if (write.Kind == ContractWriteKind.Accept && contract.State == State.Accepting)
            {
                _expiry.Remove((DueOf(contract.Row), write.ContractId));
                contract.Row = contract.Row with
                {
                    CrafterId = write.CrafterId, AcceptedUtcMs = write.AtUtcMs, DeadlineUtcMs = write.DeadlineUtcMs,
                    State = ContractDbState.Accepted,
                };
                _expiry.Add((DueOf(contract.Row), write.ContractId));
                contract.State = State.Accepted;
                contract.AcceptingBy = 0;
                _events?.Publish(new ContractAccepted(write.ContractId, contract.Row.CustomerId, write.CrafterId, write.DeadlineUtcMs),
                    write.CrafterId);
                if (contract.Ask is { } ask)
                    Deliver(ask, new ContractResult
                    {
                        RequestId = ask.RequestId, Action = ContractAction.Accept, ContractId = write.ContractId, Gold = contract.Row.Gold,
                    });
                Notify(contract.Row);
            }
        }
        foreach (long id in batch.Settles)
        {
            if (!_byId.TryGetValue(id, out var contract) || contract.State != State.Settling)
                continue;
            var row = contract.Row;
            Remove(contract);
            if (contract.ClosingAs == ContractDbState.Completed && contract.Output is { } output)
            {
                var item = output.ItemId == null ? null : _inventory.Rules.TryGetItem(output.ItemId, out var def) ? def : null;
                _events?.Publish(new ContractCompleted(id, row.CustomerId, row.CrafterId, (byte)row.Kind, row.DefNetId, row.Gold, contract.Fee,
                    item?.NetId ?? 0, output.Success ? output.Count : 0, output.Rarity, output.Success, output.OpKey), row.CrafterId,
                    RegionOf?.Invoke(row.CrafterId) ?? 0);
            }
            else
            {
                _events?.Publish(new ContractCancelled(id, row.CustomerId, row.CrafterId, row.Gold, true, contract.Cost), row.CrafterId);
                if (contract.Ask is { } ask)
                    Deliver(ask, new ContractResult
                    {
                        RequestId = ask.RequestId, Action = ContractAction.Cancel, ContractId = id, Gold = row.Gold, Cost = contract.Cost,
                    });
            }
            Notify(row);
        }
        foreach (var (charge, taken) in batch.Charges)
        {
            _chargesKnown.Remove(charge.ChargeKey);
            _events?.Publish(new ContractCharged(charge.ContractId, charge.CrafterId, charge.Amount, taken), charge.CrafterId);
        }
        if (inFlight.Portfolio != null)
            Portfolio?.Committed(characterId, inFlight.Portfolio);
    }

    /// <summary>
    /// The character's session ended without its save (a fault or lost ownership): operations no save took are undone in memory
    /// (the escrow, the inventory, the mail and the forge undo their halves); those in a save in flight become unknown until a restart.
    /// </summary>
    public void Forget(long characterId)
    {
        var forgottenCharges = new List<string>();
        if (_pending.TryGetValue(characterId, out var pendingCharges))
            forgottenCharges.AddRange(pendingCharges.Charges.Select(c => c.Row.ChargeKey));
        if (_inFlight.TryGetValue(characterId, out var inFlightCharges))
            forgottenCharges.AddRange(inFlightCharges.Batch.Charges.Select(c => c.Row.ChargeKey));
        if (_pending.Remove(characterId, out var pending))
        {
            foreach (long id in pending.Proposals)
            {
                if (_byId.TryGetValue(id, out var contract))
                {
                    Forget(contract.Ask?.OpKey);
                    Remove(contract);
                }
            }
            foreach (var write in pending.Writes)
            {
                if (!_byId.TryGetValue(write.ContractId, out var contract))
                    continue;
                if (write.Kind == ContractWriteKind.Accept && contract.State == State.Accepting)
                {
                    CountPair(ContractMath.UtcDay(write.AtUtcMs), contract.Row.CustomerId, write.CrafterId, -1);
                    Unindex(_byCrafter, write.CrafterId, write.ContractId);
                    contract.State = State.Proposed;
                    contract.AcceptingBy = 0;
                    Forget(write.OpKey);
                }
                else if (write.Kind == ContractWriteKind.WorkStarted)
                    contract.Row = contract.Row with { WorkStarted = false };
            }
            foreach (long id in pending.Settles)
            {
                if (_byId.TryGetValue(id, out var contract) && contract.State == State.Settling)
                {
                    contract.State = State.Accepted;
                    Forget(contract.Ask?.OpKey);
                }
            }
        }
        if (_inFlight.Remove(characterId, out var inFlight))
        {
            foreach (long id in inFlight.Batch.Proposals.Concat(inFlight.Batch.Settles)
                         .Concat(inFlight.Batch.Writes.Where(w => w.Kind != ContractWriteKind.WorkStarted).Select(w => w.ContractId)))
                if (_byId.TryGetValue(id, out var contract))
                    MarkUnknown(contract);
        }
        // Its charges no save took (or whose save was lost) stay pending in the database: the next login reads them again.
        if (_chargesWaiting.Remove(characterId, out var waiting))
            foreach (var charge in waiting)
                _chargesKnown.Remove(charge.ChargeKey);
        foreach (var charge in forgottenCharges)
            _chargesKnown.Remove(charge);
        Portfolio?.Forget(characterId);
    }

    // ---- Helpers ----

    private bool StartClose(Contract contract, ContractDbState state, string opKey)
    {
        var refusal = _escrow.ReleaseToMail(contract.Row.EscrowId, contract.Row.CustomerId, (short)MailKind.ContractReturn, opKey,
            new EscrowHolderClose((short)state));
        if (refusal != EscrowRefusal.None)
            return false;
        contract.State = State.Closing;
        contract.ClosingAs = state;
        _expiry.Remove((DueOf(contract.Row), contract.Row.ContractId)); // closing is final: never due again
        Notify(contract.Row);
        return true;
    }

    private static MailDraft ReturnDraft(ContractRow row, IReadOnlyList<EscrowItemRow> items, long gold, string opKey) =>
        new(row.CustomerId, MailKind.ContractReturn, MailSubjects.ContractReturn, Array.Empty<MailArg>(),
            items.Select(Attachment).ToList(), gold, opKey, $"contract:{row.ContractId}");

    /// <summary>The customer's mail of a completion: the output, and a failed conversion's kept units (R-5.4-02: no refund otherwise).</summary>
    private static MailDraft OutputDraft(ContractRow row, ContractForgeOutput output, IReadOnlyList<EscrowItemRow> escrowed)
    {
        var attachments = new List<MailAttachment>();
        if (output.Success && output.ItemId != null && output.Count > 0)
            attachments.Add(new MailAttachment(output.ItemId, output.Rarity, output.Count, output.Stolen, false, 0, output.PropertiesJson));
        if (!output.Success && output.InputsKept)
            attachments.AddRange(escrowed.Select(Attachment));
        return new MailDraft(row.CustomerId, MailKind.ContractOutput, MailSubjects.ContractOutput, new[]
        {
            new MailArg("contract", MailArgKind.Number, row.ContractId),
        }, attachments, 0, output.OpKey + ":mail", $"contract:{row.ContractId}");
    }

    private static MailAttachment Attachment(EscrowItemRow i) =>
        new(i.DefId, (byte)i.Rarity, i.Count, i.Stolen, i.NonTradable, i.InstanceId, i.PropertiesJson, i.StolenFrom, i.StolenAtMs);

    private static ContractStatus Status(Contract contract) => contract.State switch
    {
        State.Proposing or State.Proposed or State.Accepting => ContractStatus.Proposed,
        State.Accepted => ContractStatus.Accepted,
        State.InProgress or State.Settling => ContractStatus.InProgress,
        State.Closing => contract.ClosingAs == ContractDbState.Expired ? ContractStatus.Expired : ContractStatus.Cancelled,
        _ => ContractStatus.Failed,
    };

    private static long DueOf(ContractRow row) => row.State == ContractDbState.Proposed ? row.OpenUntilUtcMs : row.DeadlineUtcMs;

    /// <summary>Open contracts of a customer (counted against <c>maxOpenPerCustomer</c>).</summary>
    private int CountOpen(long customer) => Of(_byCustomer.GetValueOrDefault(customer)).Count(c => c.State != State.Unknown);

    /// <summary>Contracts a crafter holds (accepting, accepted, in progress, settling).</summary>
    private int CountActive(long crafter) => Of(_byCrafter.GetValueOrDefault(crafter))
        .Count(c => c.State is State.Accepting or State.Accepted or State.InProgress or State.Settling);

    private int PairCount(long day, long a, long b)
    {
        var (low, high) = ContractMath.PairOf(a, b);
        return _pairs.GetValueOrDefault((day, low, high));
    }

    private void CountPair(long day, long a, long b, int delta)
    {
        var (low, high) = ContractMath.PairOf(a, b);
        int count = _pairs.GetValueOrDefault((day, low, high)) + delta;
        if (count > 0)
            _pairs[(day, low, high)] = count;
        else
            _pairs.Remove((day, low, high));
    }

    private void Notify(ContractRow row)
    {
        Changed?.Invoke(row.CustomerId);
        if (row.CrafterId != 0)
            Changed?.Invoke(row.CrafterId);
        if (_byId.TryGetValue(row.ContractId, out var contract) && contract.AcceptingBy != 0)
            Changed?.Invoke(contract.AcceptingBy);
    }

    private IEnumerable<Contract> Of(HashSet<long>? ids) =>
        ids == null ? Array.Empty<Contract>() : ids.Where(_byId.ContainsKey).Select(id => _byId[id]);

    private void Add(Contract contract)
    {
        var row = contract.Row;
        _byId[row.ContractId] = contract;
        _byEscrow[row.EscrowId] = row.ContractId;
        _expiry.Add((DueOf(row), row.ContractId));
        IndexOf(_byCustomer, row.CustomerId).Add(row.ContractId);
        if (row.CrafterId != 0)
            IndexOf(_byCrafter, row.CrafterId).Add(row.ContractId);
    }

    private void Remove(Contract contract)
    {
        var row = contract.Row;
        _byId.Remove(row.ContractId);
        _byEscrow.Remove(row.EscrowId);
        _expiry.Remove((DueOf(row), row.ContractId));
        Unindex(_byCustomer, row.CustomerId, row.ContractId);
        if (row.CrafterId != 0)
            Unindex(_byCrafter, row.CrafterId, row.ContractId);
    }

    /// <summary>A save lost with its character or a release that met another: never touched again by this run (a restart reloads it).</summary>
    private void MarkUnknown(Contract contract)
    {
        contract.State = State.Unknown;
        _expiry.Remove((DueOf(contract.Row), contract.Row.ContractId));
    }

    private static HashSet<long> IndexOf(Dictionary<long, HashSet<long>> index, long key)
    {
        if (!index.TryGetValue(key, out var ids))
            index.Add(key, ids = new HashSet<long>());
        return ids;
    }

    private static void Unindex(Dictionary<long, HashSet<long>> index, long key, long id)
    {
        if (index.TryGetValue(key, out var ids) && ids.Remove(id) && ids.Count == 0)
            index.Remove(key);
    }

    private void Deliver(in ContractAsk ask, ContractResult answer)
    {
        if (_answers.ContainsKey(ask.OpKey))
            _answers[ask.OpKey] = answer;
        Answer?.Invoke(ask, answer);
    }

    private static ContractResult Refuse(in ContractAsk ask, ContractAction action, ContractReason reason, long contractId = 0) =>
        new() { RequestId = ask.RequestId, Action = action, Reason = reason, ContractId = contractId };

    /// <summary>True for an operation key seen before: <paramref name="first"/> its answer, or null while it still saves.</summary>
    private bool Repeated(string opKey, out ContractResult? first) => _answers.TryGetValue(opKey, out first);

    private void Remember(string opKey, ContractResult? answer)
    {
        if (!_answers.TryAdd(opKey, answer))
            return;
        _answerOrder.Enqueue(opKey);
        while (_answerOrder.Count > RememberedAnswers)
            _answers.Remove(_answerOrder.Dequeue());
    }

    private void Forget(string? opKey)
    {
        if (opKey != null)
            _answers.Remove(opKey);
    }

    private Pending PendingOf(long characterId)
    {
        if (!_pending.TryGetValue(characterId, out var pending))
            _pending.Add(characterId, pending = new Pending());
        return pending;
    }

    private enum State : byte
    {
        /// <summary>The proposal's save has not committed yet.</summary>
        Proposing,
        Proposed,
        /// <summary>An acceptance's save has not committed yet.</summary>
        Accepting,
        Accepted,
        /// <summary>A forge session of the crafter works on it.</summary>
        InProgress,
        /// <summary>A completion or a crafter's cancel is saving.</summary>
        Settling,
        /// <summary>A customer's cancel or an expiry is releasing to mail.</summary>
        Closing,
        Unknown,
    }

    private sealed class Contract(ContractRow row)
    {
        public ContractRow Row { get; set; } = row;
        public State State { get; set; }
        public ContractAsk? Ask { get; set; }
        public ContractAsk? CloseAsk { get; set; }
        public ContractDbState ClosingAs { get; set; }
        public long AcceptingBy { get; set; }
        public long AcceptingAtMs { get; set; }
        public long Fee { get; set; }
        public long Cost { get; set; }
        public ContractForgeOutput? Output { get; set; }
    }

    private sealed class Pending
    {
        public List<long> Proposals { get; } = new();
        public List<ContractWrite> Writes { get; } = new();
        public List<PortfolioIncrement> Counters { get; } = new();
        /// <summary>Completions and crafter cancels (their rows are in <see cref="Writes"/>).</summary>
        public List<long> Settles { get; } = new();
        /// <summary>Expiry charges collected (their gold taken in this save; user answers 2026-11-16).</summary>
        public List<(ContractChargeRow Row, long Taken)> Charges { get; } = new();
    }

    private sealed record InFlight(Guid SaveId, Pending Batch, PortfolioPending? Portfolio);
}
