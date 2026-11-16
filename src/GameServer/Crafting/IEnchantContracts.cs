using GameServer.Persistence;
using Shared.Protocol;

namespace GameServer.Crafting;

/// <summary>
/// What an enchant contract's attempt reads when it starts (spec 5.4, 5.3 R-5.3-04; WP-M4-10 part C): the customer who gets the
/// item back, the item as its escrow holds it (<see cref="Item"/>: the instance with its id, rarity, flags and properties) and its
/// level, and the stones of the terms: who supplies them (the customer's are in the same escrow, <see cref="CustomerStones"/>) and
/// which and how many.
/// </summary>
public sealed record EnchantContractTerms(long ContractId, long CustomerId, EscrowItemRow Item, int Level, ContractSupplier StonesSupplier,
    int StoneNetId, int StoneCount, IReadOnlyList<EscrowItemRow> CustomerStones);

/// <summary>An enchant contract's attempt for its settlement (WP-M4-10 part C): the operation key and the level before and after.</summary>
public sealed record EnchantContractOutput(string OpKey, int FromLevel, int ToLevel);

/// <summary>
/// The enchant contracts as the enchanting sees them (implemented by <c>Contracts/ContractService</c>, the only writer of contracts;
/// user answers 2026-11-16: one attempt per contract, whatever its outcome). An attempt opens only for the contract's crafter while
/// the contract is accepted and before its deadline; it never spans ticks: the request that opens it settles it (the escrow's gold to
/// the crafter less the contract fee, the customer's stones consumed, the item back to the customer by mail as the same instance at
/// its new level, the contract completed), all in the crafter's one save. A refusal after <see cref="OpenEnchant"/> moves nothing.
/// </summary>
public interface IEnchantContracts
{
    /// <summary>Reads the terms of an accepted enchant contract of <paramref name="crafterId"/> (nothing changes).</summary>
    EnchantReason OpenEnchant(long crafterId, long contractId, out EnchantContractTerms? terms);

    /// <summary>
    /// Checks a settlement before anything moves (the contract still waits, the gold fits the crafter, the mail is valid).
    /// <see cref="EnchantReason.None"/> when <see cref="Settle"/> will go through.
    /// </summary>
    EnchantReason PrepareSettle(long crafterId, long contractId, EnchantContractOutput output);

    /// <summary>
    /// Settles the contract in the crafter's save, after the enchanting consumed the crafter's own reserved stones (if the terms
    /// name the crafter): the escrow's gold to the crafter less the contract fee, the customer's stones consumed, the item mailed to
    /// the customer at <see cref="EnchantContractOutput.ToLevel"/>, the contract completed and counted in the crafter's portfolio.
    /// </summary>
    void Settle(long crafterId, long contractId, EnchantContractOutput output);
}
