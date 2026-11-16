using Shared.Protocol;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Which entity states a full snapshot carries first (spec 1.7 snapshots bullet, R-2.3-03; user decision 2026-11-16,
    /// option A). Only read when interest does not fit one datagram: then the observer's own entity goes first, then the
    /// critical entities (an action within <see cref="TelegraphRadiusCm"/>, the observer's lock target, its party members,
    /// and any entity not sent for <see cref="MaxStaleMs"/>), then the rest by (ticks since sent + 1) x the kind's
    /// <see cref="SnapshotKindWeight"/> x the distance weight, ties by entity id. Every number is a placeholder.
    /// </summary>
    [Table("SnapshotPriority", 1)]
    [Singleton]
    public sealed class SnapshotPriority : IValidatableRow, ISetValidatableRow
    {
        /// <summary>
        /// An entity in an action (startup, channel, active, recovery: its telegraph and its hit, R-3.1-05) at most this far from
        /// the observer on the ground is critical. Not above <c>StartingValuesM1.interestRadiusCm</c>.
        /// </summary>
        [Range(0, 100_000)] public int TelegraphRadiusCm { get; set; }

        /// <summary>Entities at most this far from the observer on the ground weigh 100 percent; farther ones <see cref="FarWeightPercent"/>.</summary>
        [Range(0, 100_000)] public int NearRadiusCm { get; set; }

        /// <summary>The distance weight of an entity farther than <see cref="NearRadiusCm"/>, in percent.</summary>
        [Range(1, 100)] public int FarWeightPercent { get; set; }

        /// <summary>
        /// An entity whose state no snapshot carried for this long becomes critical, so none starves while the critical
        /// entities fit. At least one snapshot interval.
        /// </summary>
        [Range(1, 60_000)] public int MaxStaleMs { get; set; }

        public void Validate(RowErrors errors)
        {
        }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (set.TryGet(typeof(StartingValuesM1), out var values) && values.Rows.Count == 1
                && values.Rows[0] is StartingValuesM1 starting)
            {
                if (TelegraphRadiusCm > starting.InterestRadiusCm)
                    errors.Add(nameof(TelegraphRadiusCm), $"{TelegraphRadiusCm} is above StartingValuesM1.interestRadiusCm {starting.InterestRadiusCm}");
                if (NearRadiusCm > starting.InterestRadiusCm)
                    errors.Add(nameof(NearRadiusCm), $"{NearRadiusCm} is above StartingValuesM1.interestRadiusCm {starting.InterestRadiusCm}");
                if (starting.SnapshotRateHz > 0 && (long)MaxStaleMs * starting.SnapshotRateHz < 1000)
                    errors.Add(nameof(MaxStaleMs), $"{MaxStaleMs} is shorter than one snapshot interval at StartingValuesM1.snapshotRateHz {starting.SnapshotRateHz}");
            }
            // Every entity kind a snapshot can carry needs its weight, once.
            var seen = new bool[(int)EntityKind.LootDrop + 1];
            if (set.TryGet(typeof(SnapshotKindWeight), out var weights))
            {
                foreach (var row in weights.Rows)
                {
                    if (row is SnapshotKindWeight weight && SnapshotKindWeight.TryParseKind(weight.Id, out var kind))
                        seen[(int)kind] = true;
                }
            }
            for (int kind = 1; kind < seen.Length; kind++)
            {
                if (!seen[kind])
                    errors.Add("SnapshotKindWeight", $"no row for entity kind {SnapshotKindWeight.IdOf((EntityKind)kind)}");
            }
        }

        public static SnapshotPriority? Find(ConfigSet set) =>
            set.TryGet(typeof(SnapshotPriority), out var table) && table.Rows.Count == 1 ? (SnapshotPriority)table.Rows[0] : null;
    }
}
