namespace Shared.Data.Tables
{
    /// <summary>
    /// The quest item a monster type drops for a "return a quest item" step (R-5.5-01, R-5.5-03; spec 5.5 "M2 parameters", user
    /// decision 2026-10-27): while such a step of a run is active, each kill of <see cref="MonsterId"/> by the run's owner puts one
    /// <see cref="ItemId"/> in the Quest bag (100 %), until it holds the step's count. Quest items are bound (R-4.1-01) and
    /// consumed on delivery. A monster without a row is never offered for that kind. Placeholders in data.
    /// </summary>
    [Table("QuestItemDrop", 1)]
    public sealed class QuestItemDrop : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(MonsterDef))] public string MonsterId { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        /// <summary>A quest item a receiving container takes (the Quest bag), and one row per monster.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null)
            {
                if (item.Category != ItemCategory.Quest)
                    errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; a quest drop must be a quest item");
                else if (ContainerDef.FindReceiver(set, item.Category) == null)
                    errors.Add(nameof(ItemId), "no receiving container takes quest items (the Quest bag)");
            }
            var rows = NetIds.Rows<QuestItemDrop>(set);
            foreach (var other in rows)
            {
                if (!ReferenceEquals(other, this) && other.MonsterId == MonsterId && rows.IndexOf(other) < rows.IndexOf(this))
                {
                    errors.Add(nameof(MonsterId), $"'{MonsterId}' already drops '{other.ItemId}' in row '{other.Id}'");
                    return;
                }
            }
        }
    }
}
