namespace Shared.Simulation.Stats
{
    public enum DamageKind : byte
    {
        /// <summary>Atk against Def, physical penetration.</summary>
        Physical = 0,

        /// <summary>Magic atk against Magic def, magic penetration.</summary>
        Magic = 1,
    }

    /// <summary>
    /// Everything damage formula v1 needs for one hit (R-3.4-04), already read from the stat blocks
    /// (<see cref="DamageCalc.Gather"/>) or set by the caller (skill, buffs, parry). Plain numbers so the client can
    /// build one for tooltip estimates. Out-of-range values are clamped by the formula, never trusted.
    /// </summary>
    public struct DamageInput
    {
        /// <summary>Step 1: the attacker's Atk or Magic atk.</summary>
        public int Power;

        /// <summary>Step 1: skill multiplier in whole percents (100 = x1); clamped to [0, MaxSkillMultiplierPercent].</summary>
        public int SkillMultiplierPercent;

        /// <summary>Step 1: flat damage added by the skill (may be negative; raw damage never goes below 0).</summary>
        public int SkillFlat;

        /// <summary>Step 2: the defender's Def or Magic def.</summary>
        public int Defense;

        /// <summary>Step 2: the attacker's penetration percent, clamped to [0, 100].</summary>
        public int PenetrationPercent;

        /// <summary>Step 2: the attacker's flat penetration (negative values count as 0).</summary>
        public int PenetrationFlat;

        /// <summary>Steps 3 and 4.</summary>
        public Rank AttackerRank;

        public Rank DefenderRank;

        /// <summary>Step 4: arena ruleset flag (R-6.6-05); the rank factor becomes 100.</summary>
        public bool NeutralRankFactor;

        /// <summary>Step 5: element index in <see cref="CombatConfig"/>, or <see cref="DamageCalc.NoElement"/>.</summary>
        public int Element;

        /// <summary>Step 5: the defender's resistance to <see cref="Element"/> in whole percents (before the caps).</summary>
        public int ResistPercent;

        /// <summary>Step 6: the attacker's crit rate bonus on top of the base chance.</summary>
        public int CritRatePercent;

        /// <summary>Step 6: the attacker's crit damage bonus on top of the base crit damage.</summary>
        public int CritDamageBonusPercent;

        /// <summary>
        /// Step 7: buffs and debuffs that change damage directly, as one summed percent (negative lowers damage),
        /// clamped to [-100, MaxExtraPercent]. TODO(open): which effects feed it comes with the status effect list (3.7).
        /// </summary>
        public int ExtraPercent;

        /// <summary>Step 7: parry reduction of an uncolored attack (R-3.3-04), clamped to [0, 100].</summary>
        public int ParryReductionPercent;
    }

    /// <summary>The outcome of one hit. The crit roll is kept so the server can log it with the RNG seed (rule 6).</summary>
    public readonly struct DamageResult
    {
        public DamageResult(int damage, bool isCrit, int critRoll, int critChancePercent)
        {
            Damage = damage;
            IsCrit = isCrit;
            CritRoll = critRoll;
            CritChancePercent = critChancePercent;
        }

        /// <summary>Final damage in whole points, at least 0.</summary>
        public int Damage { get; }

        public bool IsCrit { get; }

        /// <summary>The roll in [0, 100); the hit crits when it is below <see cref="CritChancePercent"/>.</summary>
        public int CritRoll { get; }

        /// <summary>The capped chance the roll was compared with.</summary>
        public int CritChancePercent { get; }
    }
}
