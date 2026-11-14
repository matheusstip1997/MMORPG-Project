namespace GameServer.Social;

/// <summary>
/// The loot debts of the parties (spec 4.3 R-4.3-02, "M4 party loot", M4 decision 2; WP-M4-6): per party and member, the gold
/// value of what the member won from party rolls, halving every <c>PartyLootParams.debtHalfLifeMs</c>. Kept in memory by
/// <see cref="PartyService"/>, the only writer of party state; a member's debt goes when it leaves the party, a party's when it
/// disbands. Read and written by <c>LootService</c> only, on the world thread.
/// </summary>
public interface IPartyLootDebts
{
    /// <summary>The member's debt in the party at <paramref name="nowMs"/>, decayed; 0 when it has none or is not in the party.</summary>
    long DebtOf(long partyId, long characterId, long nowMs);

    /// <summary>Sets the member's debt as it stands at <paramref name="nowMs"/> (after a roll); ignored for a non-member.</summary>
    void SetDebt(long partyId, long characterId, long debt, long nowMs);
}
