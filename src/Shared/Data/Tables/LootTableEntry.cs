namespace Shared.Data.Tables
{
    /// <summary>
    /// One possible result of a <see cref="LootTableDef"/> roll (R-4.3-01): an item stack of <see cref="RarityId"/> with a
    /// count rolled evenly in <see cref="CountMin"/>..<see cref="CountMax"/>. Flat rows because config tables hold no object
    /// arrays. The entries of a table are weighed in file order, so the same seed gives the same drops.
    /// </summary>
    [Table("LootTableEntry", 1)]
    public sealed class LootTableEntry : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(LootTableDef))] public string TableId { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        [Ref(typeof(RarityDef))] public string RarityId { get; set; } = string.Empty;

        [Range(1, 1_000_000)] public int Weight { get; set; }

        [Range(1, 9_999)] public int CountMin { get; set; }

        [Range(1, 9_999)] public int CountMax { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (CountMax < CountMin)
                errors.Add(nameof(CountMax), "must be at least CountMin");
        }

        /// <summary>
        /// A drop fits in one stack and the Normal bag takes it, so a pickup never meets an item it cannot hold (backlog G:
        /// M1 has only the Normal bag).
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item == null)
                return;
            if (CountMax > item.MaxStack)
                errors.Add(nameof(CountMax), $"{CountMax} is more than '{ItemId}' stacks ({item.MaxStack})");
            var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
            if (bag != null && !bag.Accept(item.Category))
                errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}, which the Normal bag does not take (R-4.1-02)");
        }
    }
}
