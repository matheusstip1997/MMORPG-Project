using System;
using System.Collections.Generic;
using Shared.Data.Tables;
using Shared.Simulation.Items;
using Shared.Util;

namespace Shared.Simulation.Crafting
{
    /// <summary>The outcome of one enchant attempt (R-5.3-04: +1, no change or -1).</summary>
    public enum EnchantOutcome : sbyte
    {
        Down = -1,
        Same = 0,
        Up = 1,
    }

    /// <summary>The weights of one attempt in whole percents, after the job tree's bonus; they sum to 100.</summary>
    public readonly struct EnchantWeights
    {
        public EnchantWeights(int plus, int same, int minus)
        {
            Plus = plus;
            Same = same;
            Minus = minus;
        }

        public int Plus { get; }
        public int Same { get; }
        public int Minus { get; }

        public override string ToString() => $"+1 {Plus}% / 0 {Same}% / -1 {Minus}%";
    }

    /// <summary>
    /// Enchanting as pure integer math (spec 5.3 R-5.3-04, 3.4; WP-M3-8; architecture rule 6): the weights of an attempt with
    /// the job tree's bonus, the outcome of a draw, the level it leads to (never below +0 or above +9), and the stat lines an
    /// enchanted item adds (read at stat time, never baked into the item). Shared by the server, the window and the
    /// economy simulator.
    /// </summary>
    public static class EnchantMath
    {
        /// <summary>The <see cref="Pcg32Rng"/> stream an attempt's draw comes from (the seed is logged with the operation).</summary>
        public const ulong DrawStream = 0x45534545UL; // "ESEE"

        /// <summary>
        /// The weights of <paramref name="odds"/> with <paramref name="bonusPoints"/> percentage points moved to +1 (the job
        /// tree's <c>enchantOddsPercent</c>, already capped by <c>JobBonusCap</c>), taken from -1 first, then from no change
        /// (user answer, WP-M3-8 plan); never more than both hold. A negative bonus counts as 0.
        /// </summary>
        public static EnchantWeights Weights(int plusPercent, int samePercent, int minusPercent, int bonusPoints)
        {
            int left = Math.Max(0, bonusPoints);
            int fromMinus = Math.Min(left, minusPercent);
            left -= fromMinus;
            int fromSame = Math.Min(left, samePercent);
            return new EnchantWeights(plusPercent + fromMinus + fromSame, samePercent - fromSame, minusPercent - fromMinus);
        }

        public static EnchantWeights Weights(EnchantOdds odds, int bonusPoints) =>
            Weights(odds.PlusPercent, odds.SamePercent, odds.MinusPercent, bonusPoints);

        /// <summary>The draw of an attempt: 0..99 from its seed (one number, so the logged seed replays it).</summary>
        public static int Draw(ulong seed) => new Pcg32Rng(seed, DrawStream).NextInt(100);

        /// <summary>The outcome of <paramref name="draw"/> (0..99): +1 below the plus weight, then no change, then -1.</summary>
        public static EnchantOutcome Outcome(in EnchantWeights weights, int draw)
        {
            if (draw < weights.Plus)
                return EnchantOutcome.Up;
            return draw < weights.Plus + weights.Same ? EnchantOutcome.Same : EnchantOutcome.Down;
        }

        /// <summary>The level after <paramref name="outcome"/> from <paramref name="level"/>, within +0..+9 (R-5.3-04).</summary>
        public static int Apply(int level, EnchantOutcome outcome) =>
            Math.Clamp(level + (int)outcome, 0, EnchantLimits.MaxLevel);

        /// <summary>
        /// The enchant lines of an item at a level whose <c>EnchantBonus.statPercent</c> is <paramref name="statPercent"/>:
        /// each stat line times the percent / 100, truncated toward zero like the rarity scale
        /// (<see cref="ItemStatBonus.Scale"/>). Lines that come to nothing are left out. Appends to <paramref name="into"/>.
        /// </summary>
        public static void AddBonus(IReadOnlyList<ItemStatBonus> lines, int statPercent, List<ItemStatBonus> into)
        {
            if (statPercent <= 0)
                return;
            for (int i = 0; i < lines.Count; i++)
            {
                var bonus = lines[i].Scale(statPercent);
                if (bonus.Flat != 0 || bonus.Percent != 0)
                    into.Add(bonus);
            }
        }
    }
}
