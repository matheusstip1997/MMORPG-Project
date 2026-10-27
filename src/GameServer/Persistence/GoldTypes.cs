namespace GameServer.Persistence;

/// <summary>Where gold went (spec 4.2 faucets and sinks). Stored in <c>gold_ledger.flow</c>: append only, never renumber.</summary>
public enum GoldFlow : short
{
    /// <summary>Gold created (R-4.2-01: outpost sales, the only faucet in M2).</summary>
    Faucet = 0,
    /// <summary>Gold destroyed (R-4.2-04: NPC vendor purchases, fees later).</summary>
    Sink = 1,
    /// <summary>Gold moved between owners (trade, escrow; later milestones).</summary>
    Move = 2,
}

/// <summary>One <c>gold_ledger</c> row: one gold change of one operation.</summary>
public readonly record struct GoldEntry(string OpKey, long Delta, long BalanceAfter, GoldFlow Flow, string Reason, string Source);

/// <summary>
/// The gold part of an <see cref="InventoryBatch"/>: the balance after the batch and its ledger rows. Written in the same
/// transaction as the rest of the batch and the character row (rule 5).
/// </summary>
public sealed record GoldBatch(long GoldAfter, IReadOnlyList<GoldEntry> Entries);
