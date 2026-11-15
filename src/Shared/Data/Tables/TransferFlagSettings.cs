namespace Shared.Data.Tables
{
    /// <summary>
    /// Large one-way transfers flagged (spec 4.5 R-4.5-02, 7.6; M4 decision 17; WP-M4-15): the thresholds per pair of accounts
    /// and UTC day, and the pace of the scan that reads the committed transfers (trades and auction or stall purchases) off the
    /// tick. A flag is a <c>TransferFlagged</c> telemetry record and a row in the admin panel's list: no automatic action, nothing
    /// refused, nothing shown to players. Every number is a placeholder (balance is deferred).
    /// </summary>
    [Table("TransferFlagSettings", 1)]
    [Singleton]
    public sealed class TransferFlagSettings : IValidatableRow
    {
        public const int DayMs = 86_400_000;

        /// <summary>Net gold one account gave the other in one UTC day (gold given minus gold received) at which the pair is flagged.</summary>
        [Range(1, 9_000_000_000_000_000)] public long GoldPerPairDay { get; set; }

        /// <summary>Net value (gold plus the items' <see cref="ItemValueBand"/> value) one account gave the other in one UTC day at which the pair is flagged.</summary>
        [Range(1, 9_000_000_000_000_000)] public long ValuePerPairDay { get; set; }

        /// <summary>How often the scan reads the transfers committed since its last run.</summary>
        [Range(1_000, 3_600_000)] public int ScanIntervalMs { get; set; }

        /// <summary>Most transfers one scan transaction reads (the read stays bounded; the rest wait for the next run).</summary>
        [Range(1, 10_000)] public int ScanBatchMax { get; set; }

        /// <summary>How far back the scan looks for transfers it has not counted yet (at least a day and one interval).</summary>
        [Range(DayMs, 30L * DayMs)] public long LookbackMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (LookbackMs < DayMs + (long)ScanIntervalMs)
                errors.Add(nameof(LookbackMs), $"{LookbackMs} is shorter than a day plus scanIntervalMs {ScanIntervalMs}");
        }

        public static TransferFlagSettings? Find(ConfigSet set) =>
            set.TryGet(typeof(TransferFlagSettings), out var table) && table.Rows.Count == 1 ? (TransferFlagSettings)table.Rows[0] : null;
    }
}
