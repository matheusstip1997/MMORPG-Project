namespace Shared.Data.Tables
{
    /// <summary>
    /// The dynamic sell multiplier's safety nets and recovery (R-4.2-02, spec 12.5). The multiplier follows how many units of
    /// an item were sold lately (a decayed counter per item, normalized by active players, mapped through the
    /// <c>price</c> <see cref="DemandCurve"/>; user decision 2026-10-27: sales only), applied per unit sequentially and
    /// clamped to [<see cref="FloorPercent"/>, <see cref="MaxPercent"/>]. It recovers as the counter decays once per demand
    /// control step. Placeholders in data.
    /// </summary>
    [Table("PriceFloorRecovery", 1)]
    [Singleton]
    public sealed class PriceFloorRecovery : IValidatableRow
    {
        /// <summary>The dynamic multiplier never goes below this whole percent (the floor price).</summary>
        [Range(1, 100)] public int FloorPercent { get; set; }

        /// <summary>The dynamic multiplier never goes above this whole percent.</summary>
        [Range(1, 1_000)] public int MaxPercent { get; set; }

        /// <summary>Recovery: each control step keeps this many thousandths of every item's decayed sales counter.</summary>
        [Range(0, 1_000)] public int SalesRetentionPermille { get; set; }

        /// <summary>
        /// The decayed units sold per active player (in thousandths of a unit) at which the ratio is 1.0 (the curve's
        /// 1000 permille point).
        /// </summary>
        [Range(1, 1_000_000_000)] public int ExpectedSalesPerPlayerMilli { get; set; }

        public void Validate(RowErrors errors)
        {
            if (FloorPercent > MaxPercent)
                errors.Add(nameof(FloorPercent), "must not be above maxPercent");
        }
    }
}
