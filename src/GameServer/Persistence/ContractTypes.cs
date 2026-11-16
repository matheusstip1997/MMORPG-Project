using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>A contract's state in the database (<c>contracts.state</c>). Stored: append only, never renumber.</summary>
public enum ContractDbState : short
{
    Proposed = 1,
    Accepted = 2,
    Completed = 3,
    Expired = 4,
    Cancelled = 5,
    /// <summary>A backstop for an escrow found missing at load (TODO(open): closed by an admin).</summary>
    Failed = 6,
}

/// <summary>One input slot of a contract's terms as stored (<c>contracts.terms</c>, JSONB array; spec 5.4 R-5.4-01).</summary>
public sealed record ContractSlotRow(ContractSupplier Supplier, int ItemNetId, int Rarity, int Count);

/// <summary>
/// One contract (<c>contracts</c>, spec 5.4, WP-M4-10). The customer's gold and the units of its slots are held in escrow
/// <see cref="EscrowId"/> (the proposal's operation key) from the proposal on. <see cref="CrafterId"/> is 0 until a crafter accepts;
/// <see cref="DeadlineUtcMs"/> runs from the acceptance (user answer 2026-11-15). <see cref="OutputCategory"/> and
/// <see cref="JobId"/> are copied from the definition at the proposal, so the portfolio counters of an expiry written off the
/// world thread need no tables.
/// </summary>
public sealed record ContractRow(long ContractId, string ProposeOpKey, long CustomerId, long CrafterId, string EscrowId, ForgeKind Kind,
    int DefNetId, string DefId, string JobId, short OutputCategory, long Gold, int DeadlineHours, IReadOnlyList<ContractSlotRow> Slots,
    long ProposedUtcMs, long OpenUntilUtcMs, long AcceptedUtcMs, long DeadlineUtcMs, bool WorkStarted, ContractDbState State);

/// <summary>What one guarded update of a contract row writes (in the character save of the operation that changed it).</summary>
public enum ContractWriteKind : byte
{
    /// <summary>A crafter accepted: <c>Proposed → Accepted</c>, guarded by the state.</summary>
    Accept = 1,
    /// <summary>The crafter's first forge start on it: <c>work_started</c> (never cleared; no guard but the state).</summary>
    WorkStarted = 2,
    /// <summary>The forge finish settled it: <c>Accepted → Completed</c>, guarded by the state and the crafter.</summary>
    Complete = 3,
    /// <summary>The crafter cancelled it: <c>Accepted → Cancelled</c>, guarded by the state and the crafter.</summary>
    CrafterCancel = 4,
}

/// <summary>One update of a contract row carried by a character's save.</summary>
public sealed record ContractWrite(ContractWriteKind Kind, long ContractId, long CrafterId, string OpKey, long AtUtcMs, long DeadlineUtcMs = 0,
    long Fee = 0, long Cost = 0);

/// <summary>
/// One portfolio counter increment (R-5.4-04; WP-M4-10): written once per <see cref="OpKey"/> (a <c>portfolio_ops</c> row), so a
/// repeated event counts once.
/// </summary>
public sealed record PortfolioIncrement(long CharacterId, string JobId, short Category, short Rarity, PortfolioOutcome Outcome, string OpKey,
    long AtUtcMs);

/// <summary>A showcase change: a pin writes <see cref="ItemJson"/> at <see cref="Slot"/>, an unpin (<see cref="ItemJson"/> null) deletes it.</summary>
public sealed record ShowcaseWrite(long CharacterId, int Slot, string DefId, int Rarity, string? ItemJson, long AtUtcMs);

/// <summary>
/// The contract part of a character save (WP-M4-10): proposals the character made (with their escrow hold), updates of contract
/// rows (accept, work started, complete, crafter cancel), portfolio increments, showcase changes and the hide flag, all in the
/// same transaction as the items and gold they go with.
/// </summary>
public sealed record ContractBatch(IReadOnlyList<ContractRow> Proposals, IReadOnlyList<ContractWrite> Writes,
    IReadOnlyList<PortfolioIncrement> Counters, IReadOnlyList<ShowcaseWrite> Showcase, bool? Hidden,
    IReadOnlyList<ContractChargeWrite>? Charges = null)
{
    public bool IsEmpty => Proposals.Count == 0 && Writes.Count == 0 && Counters.Count == 0 && Showcase.Count == 0 && Hidden == null
                           && (Charges == null || Charges.Count == 0);
}

/// <summary>
/// A crafter's charge for a contract that expired after work started (<c>contract_charges</c>, WP-M4-10 follow-up; user answers
/// 2026-11-16): written with the expiry's close, collected later in the crafter's save.
/// </summary>
public sealed record ContractChargeRow(string ChargeKey, long ContractId, long CrafterId, long Amount);

/// <summary>
/// A charge as committed, read by its key after an expiry settled (<see cref="ContractRepository.FindChargeAsync"/>): the
/// world's only source for whether an expiry charged and how much (review of PR #263). <see cref="Pending"/>: not collected yet.
/// </summary>
public sealed record ContractChargeFound(ContractChargeRow Row, bool Pending);

/// <summary>A charge's collection carried by the crafter's save: <see cref="Taken"/> of its amount (the rest dropped), guarded by the pending state.</summary>
public sealed record ContractChargeWrite(string ChargeKey, long Taken, long AtUtcMs);

/// <summary>
/// Hands a character's pending contract writes to the save being issued and learns how that save ended. Implemented by
/// <c>Contracts/ContractService</c>; called on the world thread by <see cref="SaveScheduler"/>.
/// </summary>
public interface IContractBatchSource
{
    ContractBatch? TakeBatch(long characterId, Guid saveId);

    void Committed(long characterId, Guid saveId);
}

/// <summary>A guarded contract update found the row changed (accepted, cancelled or expired elsewhere): the save rolls back.</summary>
public sealed class ContractConflictException(long contractId) : Exception($"contract {contractId} changed under an operation")
{
    public long ContractId { get; } = contractId;
}

/// <summary>The open contracts at world start, the highest id ever written, and the pairs that accepted contracts today.</summary>
public sealed record ContractLoad(IReadOnlyList<ContractRow> Open, long MaxContractId,
    IReadOnlyList<(long Customer, long Crafter, long AcceptedUtcMs)> AcceptedToday);

/// <summary>One portfolio as read for a view (off the world thread).</summary>
public sealed record PortfolioRecord(long CharacterId, bool Hidden, IReadOnlyList<PortfolioCounterRow> Counters,
    IReadOnlyList<ShowcaseRow> Showcase);

public sealed record PortfolioCounterRow(string JobId, short Category, short Rarity, PortfolioOutcome Outcome, long Count);

public sealed record ShowcaseRow(int Slot, string DefId, int Rarity, string ItemJson, long PinnedUtcMs);
