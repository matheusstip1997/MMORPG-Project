namespace Shared.Data.Tables
{
    /// <summary>
    /// The rarity multiplier of the outpost sell price (R-4.2-02), one row per <see cref="RarityDef"/> row
    /// (<see cref="EconomyCoverage"/>). Kept apart from <c>RarityDef</c> (whose stat percent scales gear, spec 4.1) so the
    /// economy can be tuned on its own. Placeholders in data.
    /// </summary>
    [Table("RarityMult", 1)]
    public sealed class RarityMult
    {
        /// <summary>The rarity id.</summary>
        [TableId, Ref(typeof(RarityDef))] public string RarityId { get; set; } = string.Empty;

        /// <summary>Whole percent of the base price (100 = the base).</summary>
        [Range(1, 100_000)] public int PricePercent { get; set; }
    }
}
