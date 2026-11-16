namespace GameServer.Events;

// Contracts and the portfolio (spec 5.4, 8.1; WP-M4-10), published by ContractService once the operation committed (rule 5).
// DefNetId is the MoldDef or RecipeDef net id of Kind (ForgeKind: 1 mold, 2 recipe).

/// <summary>A proposal committed (spec 8.1 <c>ContractProposed</c>): the gold and the customer's units are in escrow.</summary>
public readonly record struct ContractProposed(long ContractId, long CustomerId, byte Kind, int DefNetId, long Gold, int DeadlineHours,
    int UnitsHeld, long OpenUntilUtcMs) : IGameEvent;

/// <summary>A crafter's acceptance committed (spec 8.1 <c>ContractAccepted</c>): the deadline runs from now.</summary>
public readonly record struct ContractAccepted(long ContractId, long CustomerId, long CrafterId, long DeadlineUtcMs) : IGameEvent;

/// <summary>
/// A contract's forge finish committed (spec 8.1 <c>ContractCompleted</c>): the output went to the customer by mail, the
/// <see cref="Gold"/> to the crafter less the contract <see cref="Fee"/> (destroyed); <see cref="Success"/> false for a failed
/// conversion (no output; quality risk is the customer's, R-5.4-02).
/// </summary>
public readonly record struct ContractCompleted(long ContractId, long CustomerId, long CrafterId, byte Kind, int DefNetId, long Gold,
    long Fee, int OutputItemNetId, int OutputCount, byte Rarity, bool Success, string OpKey) : IGameEvent;

/// <summary>
/// A contract expired (spec 8.1 <c>ContractExpired</c>): an open proposal or an accepted contract past its deadline; everything went
/// back to the customer by mail (<see cref="CrafterId"/> 0 for a proposal no crafter accepted). <see cref="Charged"/> is what the
/// crafter owes for it (user answer 2026-11-16: an expiry after work started costs the cancel cost), 0 when free.
/// </summary>
public readonly record struct ContractExpired(long ContractId, long CustomerId, long CrafterId, long Gold, long Charged = 0) : IGameEvent;

/// <summary>
/// A crafter's expiry charge was collected (spec 8.1 <c>ContractCharged</c>; user answers 2026-11-16): <see cref="Taken"/> of
/// <see cref="Amount"/> destroyed from its gold in its own save (reason <c>ContractCancel</c>, a sink); the rest is dropped.
/// </summary>
public readonly record struct ContractCharged(long ContractId, long CrafterId, long Amount, long Taken) : IGameEvent;

/// <summary>
/// A contract was cancelled (spec 8.1 <c>ContractCancelled</c>): by its customer (<see cref="ByCrafter"/> false, free) or its
/// crafter (<see cref="Cost"/> destroyed from the crafter's gold after work started); everything went back to the customer by mail.
/// </summary>
public readonly record struct ContractCancelled(long ContractId, long CustomerId, long CrafterId, long Gold, bool ByCrafter, long Cost)
    : IGameEvent;

/// <summary>A portfolio changed (spec 8.1 <c>PortfolioChanged</c>): its counters, showcase or hide flag committed.</summary>
public readonly record struct PortfolioChanged(long CharacterId) : IGameEvent;
