namespace Shared.Data.Tables
{
    /// <summary>Which target the target lock key moves to when a lock is already on (R-2.2-06).</summary>
    public enum TargetCycleOrder : byte
    {
        /// <summary>The nearest valid target not yet locked in this round; when all were, the round restarts at the nearest.</summary>
        Distance = 0,

        /// <summary>The next valid target clockwise around the player from the current one (by angle).</summary>
        Clockwise = 1,
    }

    /// <summary>
    /// The target lock (R-2.2-06, user decision 2026-10-26): a facing aid, client-only. While a lock is on, the facing in
    /// the input frame points at the locked monster instead of the mouse; effects still pick targets with hit shapes (spec
    /// 3.1 Targeting). The key is the <see cref="Hotkeys"/> row <c>targetLock</c>. Placeholders until playtests (backlog K).
    /// </summary>
    [Table("TargetLockSettings", 1)]
    [Singleton]
    public sealed class TargetLockSettings : ISetValidatableRow
    {
        /// <summary>A second press within this time after the first clears the lock (double press) instead of cycling.</summary>
        [Range(100, 1000)] public int DoublePressWindowMs { get; set; }

        /// <summary>The key locks or cycles only to targets within this distance of the player.</summary>
        [Range(100, 100_000)] public int AcquireRangeCm { get; set; }

        /// <summary>The lock breaks when the target is farther than this (at least the acquire range, at most the interest radius).</summary>
        [Range(100, 100_000)] public int BreakRangeCm { get; set; }

        public TargetCycleOrder CycleOrder { get; set; }

        public void Validate(ConfigSet set, RowErrors errors)
        {
            if (BreakRangeCm < AcquireRangeCm)
                errors.Add(nameof(BreakRangeCm), $"{BreakRangeCm} cm is below AcquireRangeCm ({AcquireRangeCm} cm): a new lock would break at once");
            if (set.TryGet(typeof(StartingValuesM1), out var valuesTable) && valuesTable.Rows.Count == 1)
            {
                int interest = ((StartingValuesM1)valuesTable.Rows[0]).InterestRadiusCm;
                if (BreakRangeCm > interest)
                    errors.Add(nameof(BreakRangeCm), $"{BreakRangeCm} cm is beyond StartingValuesM1.InterestRadiusCm ({interest} cm): the target leaves interest first");
            }
            if (set.TryGet(typeof(Hotkeys), out _) && Hotkeys.Find(set, Hotkeys.TargetLock) == null)
                errors.Add(null, $"the target lock needs a Hotkeys row '{Hotkeys.TargetLock}'");
        }
    }
}
