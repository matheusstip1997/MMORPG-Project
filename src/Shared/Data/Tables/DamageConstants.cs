using Shared.Simulation.Stats;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Constants of damage formula v1 (spec 3.4 R-3.4-04, catalog 8.5). The base crit chance and crit damage are the
    /// M1 starting values in <see cref="StartingValuesM1"/> (spec 12.7); this table holds the caps and the mitigation
    /// constant. Every value is a placeholder until real combat numbers exist (spec 10.4).
    /// </summary>
    [Table("DamageConstants", 2)]
    [Singleton]
    public sealed class DamageConstants : IValidatableRow
    {
        /// <summary>Upper bound of a mitigation constant; keeps the integer math of <see cref="DamageCalc"/> exact.</summary>
        public const int MaxMitigationConstant = 100_000;

        /// <summary>
        /// Step 3: mitigation = effDef / (effDef + C). One C per rank, F first and SS last (<see cref="Rank"/>),
        /// looked up by the attacker's rank. Placeholder decision: the spec says only "C scaled by rank".
        /// </summary>
        public int[] MitigationConstantByRank { get; set; } = new int[0];

        /// <summary>Step 6: cap of the base chance plus the attacker's <c>critRate</c>.</summary>
        [Range(0, 100)] public int CritChanceCapPercent { get; set; }

        /// <summary>Step 6: cap of the attacker's <c>critDamage</c> bonus added to the base crit damage.</summary>
        [Range(0, 1_000)] public int CritDamageBonusCapPercent { get; set; }

        /// <summary>Step 4: every <see cref="RankFactor"/> row must lie in these bounds ("bounded").</summary>
        [Range(0, 1_000)] public int RankFactorMinPercent { get; set; }

        [Range(0, 1_000)] public int RankFactorMaxPercent { get; set; }

        /// <summary>
        /// Least damage of a damaging hit (raw above 0) after defense, rank and resistance (R-3.4-06), in whole points;
        /// 0 turns it off. Crit and step 7 apply on top, so a 100% parry or a -100% extra can still reach 0.
        /// </summary>
        [Range(0, 1_000)] public int MinDamagePerHit { get; set; }

        public void Validate(RowErrors errors)
        {
            if (MitigationConstantByRank == null || MitigationConstantByRank.Length != Ranks.Count)
            {
                errors.Add(nameof(MitigationConstantByRank), $"must list one value per rank ({Ranks.Count}, F to SS)");
            }
            else
            {
                foreach (var c in MitigationConstantByRank)
                {
                    if (c < 1 || c > MaxMitigationConstant)
                        errors.Add(nameof(MitigationConstantByRank), $"{c} is outside [1, {MaxMitigationConstant}]");
                }
            }
            if (RankFactorMinPercent > RankFactorMaxPercent)
                errors.Add(nameof(RankFactorMinPercent), "must not be greater than RankFactorMaxPercent");
            // R-6.6-05: the arena makes the rank factor neutral, so 100 must be inside the bounds.
            else if (RankFactorMinPercent > 100 || RankFactorMaxPercent < 100)
                errors.Add(nameof(RankFactorMinPercent), "the bounds must include the neutral factor 100");
        }
    }
}
