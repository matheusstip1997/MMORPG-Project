namespace Shared.Data.Tables
{
    /// <summary>
    /// The loot table of one monster type (R-4.3-01, spec 4.3 implementation "LootTable rows (monster type → entries with
    /// weight, rarity, count range)"; catalog 8.5 <c>LootTables</c>). Each of <see cref="Rolls"/> rolls picks one
    /// <see cref="LootTableEntry"/> of the table, or nothing, by weight; the server rolls with the world's seeded loot
    /// generator (architecture rule 6). A monster without a table drops nothing.
    /// </summary>
    [Table("LootTableDef", 1)]
    public sealed class LootTableDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        /// <summary>The monster type that drops this table; one table per monster type.</summary>
        [Ref(typeof(MonsterDef))] public string MonsterId { get; set; } = string.Empty;

        /// <summary>Independent rolls per kill; each one can add one stack.</summary>
        [Range(1, 10)] public int Rolls { get; set; }

        /// <summary>Weight of "no drop" in each roll, next to the entries' weights.</summary>
        [Range(0, 1_000_000)] public int NothingWeight { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            foreach (var other in NetIds.Rows<LootTableDef>(set))
            {
                if (!ReferenceEquals(other, this) && other.MonsterId == MonsterId)
                {
                    errors.Add(nameof(MonsterId), $"'{MonsterId}' also has the loot table '{other.Id}'");
                    break;
                }
            }
            if (!set.TryGet(typeof(LootTableEntry), out _))
                return;
            int entries = 0;
            foreach (var entry in NetIds.Rows<LootTableEntry>(set))
            {
                if (entry.TableId == Id)
                    entries++;
            }
            if (entries == 0)
                errors.Add(nameof(Id), "has no LootTableEntry rows");
        }
    }
}
