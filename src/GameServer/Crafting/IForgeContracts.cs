using GameServer.Persistence;
using Shared.Protocol;

namespace GameServer.Crafting;

/// <summary>
/// What a contract's forge session reads when it starts (spec 5.4, 5.3 "the session reads inputs from reserved/escrowed items";
/// WP-M4-10): the customer who gets the output, the terms' slots in input order and the units the customer's escrow holds for them.
/// </summary>
public sealed record ContractSessionTerms(long ContractId, long CustomerId, IReadOnlyList<ContractSlotRow> Slots);

/// <summary>
/// A contract session's result for its settlement (WP-M4-10): the output (null for a failed conversion) as one mail attachment for
/// the customer, and whether a failed conversion kept its inputs (the customer's escrowed units then ride back in the same mail).
/// </summary>
public sealed record ContractForgeOutput(string? ItemId, byte Rarity, int Count, bool Stolen, string? PropertiesJson, bool Success,
    bool InputsKept, string OpKey, int Quality);

/// <summary>
/// The contracts as the forge sees them (implemented by <c>Contracts/ContractService</c>, the only writer of contracts). A session
/// with a contract is <b>owned by the contract</b>: it opens only for the contract's crafter while the contract is accepted and
/// before its deadline, its customer's inputs come from the contract's escrow (never through the crafter's bag, R-5.4-02), and its
/// finish settles the contract in the crafter's save (<see cref="PrepareSettle"/>, then <see cref="Settle"/>).
/// </summary>
public interface IForgeContracts
{
    /// <summary>Opens the contract for a session of <paramref name="crafterId"/> (the contract becomes in progress).</summary>
    ForgeReason Open(long crafterId, long contractId, ForgeKind kind, int defNetId, out ContractSessionTerms? terms);

    /// <summary>The session ended without a result (cancel, disconnect, station left, combat, deadline): the contract is accepted again.</summary>
    void Closed(long contractId);

    /// <summary>
    /// Checks a settlement before anything moves (the gold fits the crafter, the contract still waits for this session, the mail
    /// is valid). <see cref="ForgeReason.None"/> when <see cref="Settle"/> will go through; the session keeps running otherwise.
    /// </summary>
    ForgeReason PrepareSettle(long crafterId, long contractId, ContractForgeOutput output);

    /// <summary>
    /// Settles the contract in the crafter's save, after the forge consumed the crafter's own reserved inputs: the escrow's gold to
    /// the crafter less the contract fee, the escrowed units consumed (or mailed back when a failed conversion kept them), the
    /// output to the customer by mail, the contract completed and counted in the crafter's portfolio.
    /// </summary>
    void Settle(long crafterId, long contractId, ContractForgeOutput output);
}
