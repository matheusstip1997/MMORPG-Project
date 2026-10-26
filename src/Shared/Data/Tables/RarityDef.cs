namespace Shared.Data.Tables
{
    /// <summary>
    /// Item rarity tiers (R-4.1-01, R-4.3-01; catalog 8.5). <see cref="Tier"/> is what item rows and messages store.
    /// TODO(open): the tier list and names are not in the spec yet; M1 has one placeholder row, <c>common</c> (tier 0).
    /// </summary>
    [Table("RarityDef", 1)]
    public sealed class RarityDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>Stored in <c>item_stacks.rarity</c> and sent in inventory messages; unique, never renumbered.</summary>
        [Range(0, 255)] public int Tier { get; set; }

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
