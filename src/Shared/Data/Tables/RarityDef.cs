namespace Shared.Data.Tables
{
    /// <summary>
    /// Item rarity tiers (R-4.1-01, R-4.3-01; catalog 8.5). <see cref="Tier"/> is what item rows and messages store. The
    /// tiers (user decision 2026-10-27, spec 4.1 M2 item model): <c>common</c> 0, <c>uncommon</c> 1, <c>rare</c> 2,
    /// <c>epic</c> 3, <c>legendary</c> 4; M2 drops use the first three, the forge (M3) reaches the others.
    /// </summary>
    [Table("RarityDef", 2)]
    public sealed class RarityDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Stored in <c>item_stacks.rarity</c> and sent in inventory messages; unique, never renumbered.</summary>
        [Range(0, 255)] public int Tier { get; set; }

        /// <summary>
        /// Scales the stat bonuses of an equipment instance of this rarity, in percent of its <see cref="ItemDef"/> row's
        /// (<see cref="ItemStatBonusDef"/>): an uncommon sword is the common sword at this percent, never a second row.
        /// PLACEHOLDERS 100, 110, 125, 150, 200.
        /// </summary>
        [Range(1, 1_000)] public int StatPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            foreach (var other in NetIds.Rows<RarityDef>(set))
            {
                if (!ReferenceEquals(other, this) && other.Tier == Tier)
                {
                    errors.Add(nameof(Tier), $"{Tier} is also used by '{other.Id}'");
                    return;
                }
            }
        }

        public static RarityDef? Find(ConfigSet set, string id) => NetIds.Find<RarityDef>(set, id, row => row.Id);
    }
}
