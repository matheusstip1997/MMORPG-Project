namespace Shared.Data.Tables
{
    /// <summary>
    /// Telemetry pipeline v1 (spec 7.2, R-7.2-01). Records go through a bounded queue that drops when full
    /// (architecture rule 10); a background writer drains it. Values in data/tables/TelemetrySettings.json are placeholders.
    /// </summary>
    [Table("TelemetrySettings", 1)]
    [Singleton]
    public sealed class TelemetrySettings : IValidatableRow
    {
        /// <summary>Records the queue holds before new records are dropped. Must be a power of two.</summary>
        [Range(64, 4_194_304)] public int QueueCapacity { get; set; }

        /// <summary>Records the writer hands to the sink in one call.</summary>
        [Range(1, 65_536)] public int MaxBatchRecords { get; set; }

        /// <summary>The writer drains the queue at least this often, even when no tick asks it to (phase 12).</summary>
        [Range(10, 60_000)] public int FlushIntervalMs { get; set; }

        /// <summary>A telemetry file is closed and a new one started once it grows past this size.</summary>
        [Range(65_536, 1_073_741_824)] public int MaxFileBytes { get; set; }

        public void Validate(RowErrors errors)
        {
            if ((QueueCapacity & (QueueCapacity - 1)) != 0)
                errors.Add(nameof(QueueCapacity), "Must be a power of two.");
            if (MaxBatchRecords > QueueCapacity)
                errors.Add(nameof(MaxBatchRecords), "Must not be larger than QueueCapacity.");
        }
    }
}
