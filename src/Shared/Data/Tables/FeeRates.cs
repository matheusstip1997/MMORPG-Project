using Shared.Simulation.Economy;

namespace Shared.Data.Tables
{
    /// <summary>
    /// One fee type of <c>FeeService</c> (spec 4.2 R-4.2-05, M4 decision 3; WP-M4-1): one row per <see cref="FeeType"/>
    /// (<see cref="EconomyCoverage"/>). The default rate applies in an unowned territory, where the whole fee is destroyed;
    /// owners set their rate within <see cref="MinPercent"/>..<see cref="MaxPercent"/> and an owned fee is split by
    /// <see cref="DestroyedSharePercent"/> (territories are M8: nothing owns a region before them). Placeholders in data.
    /// </summary>
    [Table("FeeRates", 1)]
    public sealed class FeeRates : IValidatableRow
    {
        /// <summary>The fee type's id: <c>listing</c>, <c>saleTax</c> or <c>contract</c>.</summary>
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Whole percent of the amount (the asking price, the sale price, the contract gold) in an unowned territory.</summary>
        [Range(0, 100)] public int DefaultPercent { get; set; }

        /// <summary>The lowest rate an owner may set (M8).</summary>
        [Range(0, 100)] public int MinPercent { get; set; }

        /// <summary>The highest rate an owner may set (M8).</summary>
        [Range(0, 100)] public int MaxPercent { get; set; }

        /// <summary>In an owned territory, the percent of the fee the Adventurer Guild takes (destroyed); the owner gets the rest.</summary>
        [Range(0, 100)] public int DestroyedSharePercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (!FeeTypes.TryParse(Id, out _))
                errors.Add(nameof(Id), $"'{Id}' is not a fee type (listing, saleTax, contract)");
            if (MinPercent > DefaultPercent)
                errors.Add(nameof(MinPercent), $"{MinPercent} is above defaultPercent {DefaultPercent}");
            if (DefaultPercent > MaxPercent)
                errors.Add(nameof(MaxPercent), $"{MaxPercent} is below defaultPercent {DefaultPercent}");
        }
    }
}
