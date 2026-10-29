namespace Shared.Data.Tables
{
    /// <summary>
    /// The dynamic demand loop (R-4.2-03): decayed kill counters per (monster type, region) drive a respawn delay scale and a
    /// reward multiplier through a slow control loop. Counters are normalized by active players (at least
    /// <see cref="MinActivePlayers"/>, so one player at 10-15 online does not swing them). Outputs are clamped and move at
    /// most <see cref="MaxStepChangePercent"/> per step; spawns never stop (the scale is capped, and the delay has a floor).
    /// Placeholders in data.
    /// </summary>
    [Table("DemandSettings", 1)]
    [Singleton]
    public sealed class DemandSettings : IValidatableRow
    {
        /// <summary>One control step every this many ms of world time (spec 4.2: about once a minute).</summary>
        [Range(1_000, 3_600_000)] public int ControlIntervalMs { get; set; }

        /// <summary>The demand state is saved this often (volatile class, R-7.1-01).</summary>
        [Range(1_000, 600_000)] public int PersistIntervalMs { get; set; }

        /// <summary>The active player count used to normalize never goes below this.</summary>
        [Range(1, 100_000)] public int MinActivePlayers { get; set; }

        /// <summary>Each control step keeps this many thousandths of every kill counter.</summary>
        [Range(0, 1_000)] public int KillRetentionPermille { get; set; }

        /// <summary>The decayed kills of one type in one region per active player (thousandths) at which the ratio is 1.0.</summary>
        [Range(1, 1_000_000_000)] public int ExpectedKillsPerPlayerMilli { get; set; }

        /// <summary>Clamp of the respawn delay scale, whole percent of the base delay.</summary>
        [Range(1, 10_000)] public int SpawnDelayMinPercent { get; set; }

        [Range(1, 10_000)] public int SpawnDelayMaxPercent { get; set; }

        /// <summary>Clamp of the reward multiplier (outpost runs, WP-M2-6), whole percent.</summary>
        [Range(1, 10_000)] public int RewardMinPercent { get; set; }

        [Range(1, 10_000)] public int RewardMaxPercent { get; set; }

        /// <summary>Smoothing: an output moves at most this many percentage points per control step.</summary>
        [Range(1, 10_000)] public int MaxStepChangePercent { get; set; }

        /// <summary>A scaled respawn delay is never shorter than this.</summary>
        [Range(0, 86_400_000)] public int MinRespawnMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (SpawnDelayMinPercent > 100 || SpawnDelayMaxPercent < 100)
                errors.Add(nameof(SpawnDelayMinPercent), "the spawn delay clamp must include 100 (the neutral start)");
            if (RewardMinPercent > 100 || RewardMaxPercent < 100)
                errors.Add(nameof(RewardMinPercent), "the reward clamp must include 100 (the neutral start)");
        }
    }
}
