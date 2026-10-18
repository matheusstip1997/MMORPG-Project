namespace Shared.Data.Tables
{
    /// <summary>
    /// Milestone 1 starting values (spec 12.7). Every number is a placeholder to tune during M1.
    /// Units: milliseconds, centimeters, whole percents; integers only (architecture rule 6).
    /// Ranges are sanity bounds for the validator, not design rules.
    /// </summary>
    [Table("StartingValuesM1", 1)]
    [Singleton]
    public sealed class StartingValuesM1 : IValidatableRow
    {
        // Network (spec 1.6, 1.7, 2.2)
        [Range(1, 120)] public int TickRateHz { get; set; }
        [Range(1, 120)] public int SnapshotRateHz { get; set; }
        [Range(0, 1000)] public int InterpolationDelayMs { get; set; }
        /// <summary>Architecture rule 12: lag compensation is capped at 200 ms.</summary>
        [Range(0, 200)] public int LagCompensationCapMs { get; set; }
        [Range(0, 200)] public int PvpAttackerRewindMs { get; set; }
        [Range(1, 10)] public int InputRedundancy { get; set; }

        // Movement
        [Range(1, 10_000)] public int MoveSpeedCmPerSec { get; set; }
        [Range(0, 500)] public int MountSpeedBonusPercent { get; set; }

        // Stamina (spec 3.1); no heavy attacks
        [Range(1, 10_000)] public int StaminaPool { get; set; }
        [Range(0, 10_000)] public int StaminaRegenPerSec { get; set; }
        [Range(0, 60_000)] public int StaminaRegenDelayMs { get; set; }
        [Range(0, 10_000)] public int NormalAttackStaminaCost { get; set; }
        /// <summary>Bounds for per-skill costs; the real cost of each skill belongs in SkillDef.</summary>
        [Range(0, 10_000)] public int SkillStaminaCostMin { get; set; }
        [Range(0, 10_000)] public int SkillStaminaCostMax { get; set; }
        [Range(0, 10_000)] public int DodgeStaminaCost { get; set; }

        // Dodge and parry (spec 3.3)
        [Range(1, 10)] public int DodgeCharges { get; set; }
        [Range(0, 60_000)] public int DodgeRechargeMs { get; set; }
        [Range(1, 5_000)] public int DodgeDurationMs { get; set; }
        [Range(0, 5_000)] public int DodgeIFramesMs { get; set; }
        [Range(0, 5_000)] public int DodgeDistanceCm { get; set; }
        [Range(1, 10)] public int ParryCharges { get; set; }
        [Range(0, 60_000)] public int ParryRechargeMs { get; set; }
        [Range(1, 5_000)] public int ParryWindowMs { get; set; }
        [Range(0, 100)] public int UncoloredDamageReductionPercent { get; set; }

        // Poise and crits (spec 3.3, 3.4)
        /// <summary>The spec 12.7 reference value; the poise of a player is its class's <c>ClassDef.poiseMax</c>, set from it.</summary>
        [Range(1, 10_000)] public int PlayerPoise { get; set; }
        [Range(0, 60_000)] public int StaggerImmunityMs { get; set; }
        [Range(0, 100)] public int CritChancePercent { get; set; }
        [Range(100, 1_000)] public int CritDamagePercent { get; set; }

        // World
        [Range(100, 100_000)] public int InterestRadiusCm { get; set; }
        [Range(100, 100_000)] public int GridCellCm { get; set; }

        // Monsters
        [Range(0, 10_000)] public int MonsterAttackStartupMinMs { get; set; }
        [Range(0, 86_400_000)] public int MonsterRespawnMs { get; set; }
        [Range(0, 86_400_000)] public int BossRespawnMs { get; set; }

        // Loot (spec 4)
        [Range(0, 3_600_000)] public int PartyLootLockMs { get; set; }
        [Range(0, 3_600_000)] public int DeathBagOwnerWindowMs { get; set; }
        [Range(0, 86_400_000)] public int BagExpiryMs { get; set; }

        // Sessions
        [Range(0, 600_000)] public int LingerOutOfCombatMs { get; set; }
        [Range(0, 600_000)] public int LingerInCombatMs { get; set; }
        [Range(0, 600_000)] public int CombatStateMs { get; set; }

        // Saves (spec 7.1, architecture rule 11)
        [Range(100, 60_000)] public int SaveClass2IntervalMs { get; set; }
        [Range(100, 600_000)] public int VolatileSaveIntervalMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (SkillStaminaCostMin > SkillStaminaCostMax)
                errors.Add(nameof(SkillStaminaCostMin), "must not be greater than SkillStaminaCostMax");
            if (DodgeIFramesMs > DodgeDurationMs)
                errors.Add(nameof(DodgeIFramesMs), "i-frames cannot last longer than the dodge action");
            if (PvpAttackerRewindMs > LagCompensationCapMs)
                errors.Add(nameof(PvpAttackerRewindMs), "must not exceed LagCompensationCapMs");
            // Spec 1.6: snapshots go out on whole ticks. Zero is a range error, reported separately.
            if (SnapshotRateHz > 0 && TickRateHz % SnapshotRateHz != 0)
                errors.Add(nameof(SnapshotRateHz), "must divide TickRateHz");
            if (InterestRadiusCm < GridCellCm)
                errors.Add(nameof(InterestRadiusCm), "must be at least one grid cell");
            // Spec 12.7: monster startup must cover reaction time plus the lag compensation cap.
            if (MonsterAttackStartupMinMs < LagCompensationCapMs)
                errors.Add(nameof(MonsterAttackStartupMinMs), "must be at least LagCompensationCapMs");
        }
    }
}
