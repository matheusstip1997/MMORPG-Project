namespace Shared.Data.Tables
{
    /// <summary>
    /// The exp a character needs to go from <see cref="Level"/> to the next level (R-3.6-01), and the base exp of an
    /// even-level monster at that level (a <see cref="MonsterDef"/> without its own <c>baseExp</c> gives this). Every number
    /// is a placeholder (user decision 2026-10-27, spec 3.6 "M2 placeholder curves"), generated from the documented shape
    /// <c>monsterBaseExp = 10 + 10 x level</c>, <c>expToNext = monsterBaseExp x (8 + 0.6 x level)</c>: about 8 kills per
    /// level at level 1, 17 at 15 and 26 at 30. One row per level from 1 to <c>ProgressionSettings.maxLevel - 1</c>, in
    /// order, never decreasing.
    /// </summary>
    [Table("ExpCurve", 1)]
    public sealed class ExpCurve : ISetValidatableRow
    {
        [Range(1, 10_000)] public int Level { get; set; }

        [Range(1, 1_000_000_000)] public int ExpToNext { get; set; }

        [Range(1, 1_000_000_000)] public int MonsterBaseExp { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            var rows = NetIds.Rows<ExpCurve>(set);
            int index = rows.IndexOf(this);
            if (Level != index + 1)
                errors.Add(nameof(Level), $"row {index} must be level {index + 1}: one row per level from 1, in order");
            if (index > 0 && rows[index - 1].ExpToNext > ExpToNext)
                errors.Add(nameof(ExpToNext), $"{ExpToNext} is below the previous level's {rows[index - 1].ExpToNext}");
            if (index == rows.Count - 1 && ProgressionSettings.Find(set) is { } settings && Level != settings.MaxLevel - 1)
                errors.Add(nameof(Level), $"the last row must be level {settings.MaxLevel - 1} (ProgressionSettings.maxLevel - 1), not {Level}");
        }
    }
}
