namespace Shared.Data.Tables
{
    /// <summary>
    /// The job skill-tree rules (spec 5.1 R-5.1-04; <c>docs/design/job-trees.md</c> decision 2; WP-M3-7): row
    /// <c>n</c> of every job tree opens at job level <see cref="RowMinJobLevel"/>[n]. Placeholders (1, 5, 10, 15).
    /// </summary>
    [Table("JobTreeSettings", 1)]
    [Singleton]
    public sealed class JobTreeSettings : IValidatableRow, ISetValidatableRow
    {
        /// <summary>The job level that opens each row, index row; rising.</summary>
        public int[] RowMinJobLevel { get; set; } = System.Array.Empty<int>();

        public void Validate(RowErrors errors)
        {
            if (RowMinJobLevel.Length == 0)
                errors.Add(nameof(RowMinJobLevel), "must list at least one row");
            for (int i = 0; i < RowMinJobLevel.Length; i++)
            {
                if (RowMinJobLevel[i] < 1)
                    errors.Add(nameof(RowMinJobLevel), $"row {i} opens at {RowMinJobLevel[i]}, below job level 1");
                else if (i > 0 && RowMinJobLevel[i] <= RowMinJobLevel[i - 1])
                    errors.Add(nameof(RowMinJobLevel), $"row {i} opens at {RowMinJobLevel[i]}, not after row {i - 1} ({RowMinJobLevel[i - 1]})");
            }
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (JobSettings.Find(set) is not { } jobs)
                return;
            for (int i = 0; i < RowMinJobLevel.Length; i++)
            {
                if (RowMinJobLevel[i] > jobs.MaxLevel)
                    errors.Add(nameof(RowMinJobLevel), $"row {i} opens at {RowMinJobLevel[i]}, above JobSettings.maxLevel {jobs.MaxLevel}");
            }
        }

        /// <summary>The job level that opens <paramref name="row"/>; int.MaxValue for a row the settings do not have.</summary>
        public int MinJobLevel(int row) => row >= 0 && row < RowMinJobLevel.Length ? RowMinJobLevel[row] : int.MaxValue;

        public static JobTreeSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(JobTreeSettings), out var table) && table.Rows.Count == 1 ? (JobTreeSettings)table.Rows[0] : null;
    }
}
