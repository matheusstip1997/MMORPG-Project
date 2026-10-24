namespace Shared.Simulation.Stats
{
    /// <summary>
    /// <c>StatDef</c> ids that code reads by name: the inputs of damage formula v1 (R-3.4-04), the resource meters
    /// (R-3.4-03) and flinch (R-3.3-07). Every other stat is data only. <see cref="CombatConfig.TryBuild"/> fails when one is missing.
    /// </summary>
    public static class StatIds
    {
        public const string Atk = "atk";
        public const string Matk = "matk";
        public const string Def = "def";
        public const string Mdef = "mdef";
        public const string PhysPenPercent = "physPenPercent";
        public const string PhysPenFlat = "physPenFlat";
        public const string MagicPenPercent = "magicPenPercent";
        public const string MagicPenFlat = "magicPenFlat";
        public const string CritRate = "critRate";
        public const string CritDamage = "critDamage";
        public const string MaxHp = "maxHp";
        public const string MaxMp = "maxMp";
        public const string MaxStamina = "maxStamina";
        public const string HpRegen = "hpRegen";
        public const string MpRegen = "mpRegen";
        public const string StaminaRegen = "staminaRegen";

        /// <summary>Step 7 (R-3.4-04): the attacker's damage dealt, whole percents added to its hits (buffs and debuffs, spec 3.7).</summary>
        public const string DamageDealtPercent = "damageDealtPercent";

        /// <summary>Step 7: the defender's damage taken, whole percents (Magic Barrier: -20).</summary>
        public const string DamageTakenPercent = "damageTakenPercent";

        /// <summary>R-3.3-07: a hit flinches when its flinch power is at least this (base from ClassDef or MonsterDef).</summary>
        public const string FlinchResist = "flinchResist";

        /// <summary>All of the above, in one list for validation.</summary>
        public static readonly string[] Required =
        {
            Atk, Matk, Def, Mdef, PhysPenPercent, PhysPenFlat, MagicPenPercent, MagicPenFlat, CritRate, CritDamage,
            MaxHp, MaxMp, MaxStamina, HpRegen, MpRegen, StaminaRegen, DamageDealtPercent, DamageTakenPercent, FlinchResist,
        };
    }
}
