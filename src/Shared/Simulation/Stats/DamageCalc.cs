using System;
using Shared.Data.Tables;
using Shared.Util;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// Damage formula v1 (spec 3.4 R-3.4-04) as pure functions with integer math (architecture rule 6). Lives in
    /// Shared so the client can show tooltip estimates; the server result is authoritative.
    /// <para>
    /// Damage runs in hundredths of a point (<see cref="Scale"/>) through the steps, each rounding down:
    /// 1. raw = power x skill% + flat; 2. effDef = max(0, def x (100 - pen%) - flatPen);
    /// 3. raw x C / (effDef + C), which equals raw x (1 - effDef / (effDef + C)), C by the attacker's rank;
    /// 4. x rank factor%; 5. x (100 - clamp(resist%, min, max))%; 6. on a crit, x (base crit damage% + capped bonus);
    /// 7. x (100 + extra%) x (100 - parry%). The final value rounds half up to whole points and saturates at
    /// <c>int.MaxValue</c>. There is no minimum damage and no variance (optional in the spec) in v1.
    /// </para>
    /// <para>
    /// Every step clamps its inputs, so no input wraps around: the bounds on skill multiplier, extra percent and C
    /// keep <see cref="MulDiv"/> exact, and values above <c>long.MaxValue</c> saturate.
    /// </para>
    /// </summary>
    public static class DamageCalc
    {
        /// <summary>Internal units per damage point.</summary>
        public const int Scale = 100;

        /// <summary>Element value for damage without an element (step 5 is skipped).</summary>
        public const int NoElement = -1;

        /// <summary>Code bound (overflow safety), not a design value: x1000. SkillDef validation will stay below it.</summary>
        public const int MaxSkillMultiplierPercent = 100_000;

        /// <summary>Code bound (overflow safety), not a design value: +10000%.</summary>
        public const int MaxExtraPercent = 10_000;

        /// <summary>Rolls the crit with one draw from <paramref name="rng"/> (always exactly one), then computes.</summary>
        public static DamageResult Compute(in DamageInput input, CombatConfig config, IRng rng)
        {
            if (rng == null)
                throw new ArgumentNullException(nameof(rng));
            return ComputeWithRoll(input, config, rng.NextInt(100));
        }

        /// <summary>
        /// The whole formula for a given crit roll in [0, 100). Deterministic; replays a logged roll, and the client
        /// can show the non-crit (roll 99) and crit (roll 0) estimate.
        /// </summary>
        public static DamageResult ComputeWithRoll(in DamageInput input, CombatConfig config, int critRoll)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (critRoll < 0 || critRoll >= 100)
                throw new ArgumentOutOfRangeException(nameof(critRoll), "must be in [0, 100)");

            long damage = RawScaled(input.Power, input.SkillMultiplierPercent, input.SkillFlat);
            long effectiveDefense = EffectiveDefenseScaled(input.Defense, input.PenetrationPercent, input.PenetrationFlat);
            damage = Mitigate(damage, effectiveDefense, config.MitigationConstant(CheckRank(input.AttackerRank)));

            int rankFactor = input.NeutralRankFactor ? 100 : config.RankFactorPercent(input.AttackerRank, CheckRank(input.DefenderRank));
            damage = ApplyPercent(damage, rankFactor);

            if (input.Element != NoElement)
            {
                if ((uint)input.Element >= (uint)config.ElementCount)
                    throw new ArgumentOutOfRangeException(nameof(input), $"element {input.Element} is not in ElementCaps");
                damage = ApplyPercent(damage, ElementFactorPercent(input.ResistPercent,
                    config.ElementResistMinPercent(input.Element), config.ElementResistMaxPercent(input.Element)));
            }

            int chance = CritChancePercent(config.BaseCritChancePercent, input.CritRatePercent, config.CritChanceCapPercent);
            bool isCrit = critRoll < chance;
            if (isCrit)
                damage = ApplyPercent(damage, CritMultiplierPercent(config.BaseCritDamagePercent, input.CritDamageBonusPercent,
                    config.CritDamageBonusCapPercent));

            damage = ApplyPercent(damage, 100 + Clamp(input.ExtraPercent, -100, MaxExtraPercent));
            damage = ApplyPercent(damage, 100 - Clamp(input.ParryReductionPercent, 0, 100));

            return new DamageResult(ToPoints(damage), isCrit, critRoll, chance);
        }

        /// <summary>
        /// Fills the stat-driven fields of a <see cref="DamageInput"/>: power, defense and penetration by
        /// <paramref name="kind"/>, crit bonuses, and the defender's resistance to <paramref name="element"/>.
        /// The caller sets <see cref="DamageInput.ExtraPercent"/>, <see cref="DamageInput.ParryReductionPercent"/> and
        /// <see cref="DamageInput.NeutralRankFactor"/> afterwards. Expire modifiers on both blocks first.
        /// </summary>
        public static DamageInput Gather(CombatConfig config, StatBlock attacker, Rank attackerRank, StatBlock defender,
            Rank defenderRank, DamageKind kind, int skillMultiplierPercent, int skillFlat, int element)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (attacker == null) throw new ArgumentNullException(nameof(attacker));
            if (defender == null) throw new ArgumentNullException(nameof(defender));
            var ids = config.Ids;
            bool magic = kind == DamageKind.Magic;
            var input = new DamageInput
            {
                Power = attacker.Get(magic ? ids.Matk : ids.Atk),
                SkillMultiplierPercent = skillMultiplierPercent,
                SkillFlat = skillFlat,
                Defense = defender.Get(magic ? ids.Mdef : ids.Def),
                PenetrationPercent = attacker.Get(magic ? ids.MagicPenPercent : ids.PhysPenPercent),
                PenetrationFlat = attacker.Get(magic ? ids.MagicPenFlat : ids.PhysPenFlat),
                AttackerRank = attackerRank,
                DefenderRank = defenderRank,
                Element = element,
                CritRatePercent = attacker.Get(ids.CritRate),
                CritDamageBonusPercent = attacker.Get(ids.CritDamage),
            };
            if (element != NoElement)
            {
                if ((uint)element >= (uint)config.ElementCount)
                    throw new ArgumentOutOfRangeException(nameof(element), $"element {element} is not in ElementCaps");
                input.ResistPercent = defender.Get(config.ElementResistStat(element));
            }
            return input;
        }

        /// <summary>Step 1, in hundredths: power x skill% + flat x 100, at least 0.</summary>
        public static long RawScaled(int power, int skillMultiplierPercent, int skillFlat)
        {
            long raw = (long)Math.Max(0, power) * Clamp(skillMultiplierPercent, 0, MaxSkillMultiplierPercent)
                + (long)skillFlat * Scale;
            return Math.Max(0, raw);
        }

        /// <summary>Step 2, in hundredths: max(0, def x (100 - pen%) - flatPen x 100).</summary>
        public static long EffectiveDefenseScaled(int defense, int penetrationPercent, int penetrationFlat)
        {
            long effective = (long)Math.Max(0, defense) * (100 - Clamp(penetrationPercent, 0, 100))
                - (long)Math.Max(0, penetrationFlat) * Scale;
            return Math.Max(0, effective);
        }

        /// <summary>
        /// Step 3: damage x (1 - effDef / (effDef + C)) = damage x C / (effDef + C), with C in points (scaled here to
        /// match <paramref name="effectiveDefenseScaled"/>). One division, so the only rounding is the final floor.
        /// </summary>
        public static long Mitigate(long damageScaled, long effectiveDefenseScaled, int mitigationConstant)
        {
            if (mitigationConstant < 1 || mitigationConstant > DamageConstants.MaxMitigationConstant)
                throw new ArgumentOutOfRangeException(nameof(mitigationConstant));
            long c = (long)mitigationConstant * Scale;
            return MulDiv(Math.Max(0, damageScaled), c, Math.Max(0, effectiveDefenseScaled) + c);
        }

        /// <summary>Step 5: 100 - clamp(resist, min, max). Above 100 is a weakness, 0 is immunity.</summary>
        public static int ElementFactorPercent(int resistPercent, int minPercent, int maxPercent) =>
            100 - Clamp(resistPercent, minPercent, maxPercent);

        /// <summary>Step 6: clamp(base + bonus, 0, cap).</summary>
        public static int CritChancePercent(int basePercent, int critRatePercent, int capPercent) =>
            (int)Math.Max(0, Math.Min(capPercent, (long)basePercent + critRatePercent));

        /// <summary>Step 6: base crit damage + clamp(bonus, 0, cap), for example 150 + 0 = x1.5.</summary>
        public static int CritMultiplierPercent(int basePercent, int bonusPercent, int bonusCapPercent) =>
            basePercent + Clamp(bonusPercent, 0, bonusCapPercent);

        /// <summary>value x percent / 100, rounded down; negative percents count as 0.</summary>
        public static long ApplyPercent(long valueScaled, int percent) => MulDiv(Math.Max(0, valueScaled), Math.Max(0, percent), 100);

        /// <summary>Hundredths to whole points, halves rounded up, saturating at <c>int.MaxValue</c>.</summary>
        public static int ToPoints(long valueScaled)
        {
            if (valueScaled <= 0)
                return 0;
            if (valueScaled >= (long)int.MaxValue * Scale)
                return int.MaxValue;
            return (int)((valueScaled + Scale / 2) / Scale);
        }

        /// <summary>
        /// floor(a x b / d) for a, b at least 0 and d positive, exact while d x b fits in a long; a result above
        /// <c>long.MaxValue</c> saturates. The callers keep d x b small (percent steps: d = 100; step 3: C bounded).
        /// </summary>
        public static long MulDiv(long a, long b, long d)
        {
            if (a < 0 || b < 0)
                throw new ArgumentOutOfRangeException(a < 0 ? nameof(a) : nameof(b), "must not be negative");
            if (d <= 0)
                throw new ArgumentOutOfRangeException(nameof(d), "must be positive");
            long quotient = a / d;
            long remainder = a % d;
            if (b != 0 && quotient > long.MaxValue / b)
                return long.MaxValue;
            long high = quotient * b;
            long low = remainder * b / d;
            return high > long.MaxValue - low ? long.MaxValue : high + low;
        }

        private static Rank CheckRank(Rank rank)
        {
            if ((uint)rank >= Ranks.Count)
                throw new ArgumentOutOfRangeException(nameof(rank), $"{(int)rank} is not a rank");
            return rank;
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    }
}
