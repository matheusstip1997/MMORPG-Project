namespace Shared.Data.Tables
{
    /// <summary>
    /// One possible yield of a resource node's harvest (spec 5.2 R-5.2-01, R-5.2-02; WP-M3-5): the rows with a
    /// <see cref="NodeDef.YieldTableId"/> as <see cref="TableId"/> are the node's yield table. A harvest picks one entry by
    /// <see cref="Weight"/> among those the harvester's job level reaches (<see cref="MinJobLevel"/>), then a count evenly in
    /// <see cref="CountMin"/>..<see cref="CountMax"/> before the bonuses (<c>Simulation/Gathering/HarvestMath</c>). Gem
    /// finds and enchant stones are entries like the ore (R-5.2-01 "gems may be found while mining"). Flat rows, weighed in
    /// file order, so the same seed gives the same yield. Every number in data is a placeholder.
    /// </summary>
    [Table("NodeYieldEntry", 1)]
    public sealed class NodeYieldEntry : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Highest <see cref="MinJobLevel"/>: the job level cap is <see cref="JobSettings.MaxLevel"/>.</summary>
        public const int MaxJobLevel = 1_000;

        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The yield table this entry belongs to (a <see cref="NodeDef.YieldTableId"/>).</summary>
        public string TableId { get; set; } = string.Empty;

        /// <summary>A stackable item (a material in M3) that a receiving container takes.</summary>
        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        [Ref(typeof(RarityDef))] public string RarityId { get; set; } = string.Empty;

        [Range(1, 1_000_000)] public int Weight { get; set; }

        [Range(1, 9_999)] public int CountMin { get; set; }

        [Range(1, 9_999)] public int CountMax { get; set; }

        /// <summary>The entry is in the roll only from this job level of the node's job (spec 5.2 "yield tied to job level").</summary>
        [Range(1, MaxJobLevel)] public int MinJobLevel { get; set; } = 1;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (string.IsNullOrWhiteSpace(TableId))
                errors.Add(nameof(TableId), "must name a yield table");
            if (CountMax < CountMin)
                errors.Add(nameof(CountMax), "must be at least CountMin");
        }

        /// <summary>
        /// The item is a stackable item whose base count fits one stack and that a receiving container takes (the harvest goes
        /// to the bag a pickup would, R-4.1-02); the entry's table belongs to a node; the minimum job level is reachable.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null)
            {
                if (item.MaxStack <= 1)
                    errors.Add(nameof(ItemId), $"'{ItemId}' does not stack; a harvest yields stackable items");
                else if (CountMax > item.MaxStack)
                    errors.Add(nameof(CountMax), $"{CountMax} is more than '{ItemId}' stacks ({item.MaxStack})");
                if (set.TryGet(typeof(ContainerDef), out _) && ContainerDef.FindReceiver(set, item.Category) == null)
                    errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}, which no container with receivesNewItems takes (R-4.1-02)");
            }
            if (set.TryGet(typeof(NodeDef), out var nodes))
            {
                bool used = false;
                foreach (var row in nodes.Rows)
                    used |= ((NodeDef)row).YieldTableId == TableId;
                if (!used)
                    errors.Add(nameof(TableId), $"'{TableId}' is the yield table of no NodeDef row");
            }
            if (JobSettings.Find(set) is { } jobs && MinJobLevel > jobs.MaxLevel)
                errors.Add(nameof(MinJobLevel), $"{MinJobLevel} is above the job level cap ({jobs.MaxLevel})");
        }
    }
}
