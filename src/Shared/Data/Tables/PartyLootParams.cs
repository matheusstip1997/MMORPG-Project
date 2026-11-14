namespace Shared.Data.Tables
{
    /// <summary>Where a party roll reads an item's value for the loot debt (spec 4.3 R-4.3-02). Append only.</summary>
    public enum LootValueSource
    {
        /// <summary>No item has a value: every roll is uniform and no debt grows.</summary>
        None = 0,
        /// <summary>The item's <c>PriceBase.baseGold</c> per unit (M4 decision 2), 0 for an item without a row.</summary>
        PriceBase = 1,
    }

    /// <summary>
    /// Party loot (spec 4.3 R-4.3-02, "M4 party loot", M4 decision 2; WP-M4-6): the lock window of a ground drop (moved from
    /// <c>StartingValuesM1.partyLootLockMs</c>; the killer's party, or the killer alone), the range at the kill that makes a
    /// member eligible without a hit, and the loot debt (its value source, its half-life and the debt at which a member weighs
    /// half a member without debt; <c>Shared.Simulation.Items.PartyLoot</c>). Every number is a placeholder (balance is
    /// deferred). Read by the server and the bots.
    /// </summary>
    [Table("PartyLootParams", 1)]
    [Singleton]
    public sealed class PartyLootParams : IValidatableRow
    {
        /// <summary>A drop is locked to the killer's party (or the killer alone) this long after the kill (30 s, M4 decision 2).</summary>
        [Range(0, 3_600_000)] public int LockMs { get; set; }

        /// <summary>A member that did not hit the victim is eligible when it stood within this distance of the kill.</summary>
        [Range(0, 100_000)] public int KillRangeCm { get; set; }

        /// <summary>A member's loot debt halves this often (10 minutes, M4 decision 2).</summary>
        [Range(1_000, 86_400_000)] public int DebtHalfLifeMs { get; set; }

        /// <summary>The debt (gold) at which a member's weight is half that of a member without debt.</summary>
        [Range(0, 1_000_000_000)] public int DebtHalfWeightGold { get; set; }

        /// <summary>Where an item's value comes from (M4 decision 2: <c>PriceBase</c>).</summary>
        public LootValueSource ValueSource { get; set; }

        public static PartyLootParams? Find(ConfigSet set) =>
            set.TryGet(typeof(PartyLootParams), out var table) && table.Rows.Count == 1 ? (PartyLootParams)table.Rows[0] : null;

        public void Validate(RowErrors errors)
        {
            if (ValueSource != LootValueSource.None && DebtHalfWeightGold <= 0)
                errors.Add(nameof(DebtHalfWeightGold), "must be positive when items have a value (valueSource is not none)");
            if (ValueSource < LootValueSource.None || ValueSource > LootValueSource.PriceBase)
                errors.Add(nameof(ValueSource), $"'{ValueSource}' is not a value source");
        }
    }
}
