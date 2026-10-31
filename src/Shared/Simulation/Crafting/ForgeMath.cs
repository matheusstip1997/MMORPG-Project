using System;
using System.Collections.Generic;
using Shared.Simulation.Items;
using Shared.Util;

namespace Shared.Simulation.Crafting
{
    /// <summary>
    /// Quality to rarity (<c>QualityThresholds</c>, R-5.3-02 "mapped to rarity by config thresholds"): ascending bands, each
    /// a minimum quality, a <c>RarityDef.tier</c> and the stat bonus percent of R-5.3-02 "a quality bonus".
    /// </summary>
    public sealed class QualityBands
    {
        private readonly int[] _minQuality;
        private readonly byte[] _rarity;
        private readonly int[] _bonusPercent;

        public QualityBands(int[] minQuality, byte[] rarityTier, int[] statBonusPercent)
        {
            if (minQuality.Length == 0 || minQuality.Length != rarityTier.Length || minQuality.Length != statBonusPercent.Length)
                throw new ArgumentException("one minimum, rarity and bonus per band, at least one band");
            _minQuality = minQuality;
            _rarity = rarityTier;
            _bonusPercent = statBonusPercent;
        }

        public int Count => _minQuality.Length;

        /// <summary>The band a quality falls in: the last whose minimum it reaches (the first when below every one).</summary>
        public int Band(int quality)
        {
            int band = 0;
            for (int i = 1; i < _minQuality.Length; i++)
            {
                if (quality >= _minQuality[i])
                    band = i;
            }
            return band;
        }

        public byte Rarity(int quality) => _rarity[Band(quality)];

        public int StatBonusPercent(int quality) => _bonusPercent[Band(quality)];
    }

    /// <summary>One material stat line times the units the craft used of it (<c>MaterialStatDef</c>).</summary>
    public readonly struct MaterialStatUse
    {
        public MaterialStatUse(string statId, int flat, int percent, int units)
        {
            StatId = statId;
            Flat = flat;
            Percent = percent;
            Units = units;
        }

        public string StatId { get; }
        public int Flat { get; }
        public int Percent { get; }
        public int Units { get; }
    }

    /// <summary>
    /// The crafted stats snapshot (R-5.3-02 "item stats = materials used + a quality bonus"; spec 5.3 "output is a stats
    /// snapshot"; WP-M3-6): every material stat line times its units, summed per stat in first-seen order, then raised by
    /// the quality band's percent (integer, rounded toward zero). At most <see cref="ItemProperties.MaxCraftedStats"/>
    /// lines; a stat whose sums are both zero is left out.
    /// </summary>
    public static class CraftedStatsCalc
    {
        public static List<ItemStatBonus> Build(IReadOnlyList<MaterialStatUse> uses, int bonusPercent)
        {
            var order = new List<string>();
            var flat = new Dictionary<string, long>(StringComparer.Ordinal);
            var percent = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var use in uses)
            {
                if (!flat.ContainsKey(use.StatId))
                {
                    order.Add(use.StatId);
                    flat[use.StatId] = 0;
                    percent[use.StatId] = 0;
                }
                flat[use.StatId] += (long)use.Flat * use.Units;
                percent[use.StatId] += (long)use.Percent * use.Units;
            }
            var result = new List<ItemStatBonus>();
            foreach (var stat in order)
            {
                int f = Scale(flat[stat], bonusPercent);
                int p = Scale(percent[stat], bonusPercent);
                if (f == 0 && p == 0)
                    continue;
                if (result.Count == ItemProperties.MaxCraftedStats)
                    break;
                result.Add(new ItemStatBonus(stat, f, p));
            }
            return result;
        }

        private static int Scale(long value, int bonusPercent)
        {
            long scaled = value * (100 + bonusPercent) / 100;
            return scaled > int.MaxValue ? int.MaxValue : scaled < int.MinValue ? int.MinValue : (int)scaled;
        }
    }

    /// <summary>The numbers a session's seed draws at Finish (<see cref="ForgeRolls"/>).</summary>
    public readonly struct ForgeRollResult
    {
        public ForgeRollResult(int actions, int serverRoll, int smallTerm, bool success, bool inputsKept = false)
        {
            InputsKept = inputsKept;
            Actions = actions;
            ServerRoll = serverRoll;
            SmallTerm = smallTerm;
            Success = success;
        }

        /// <summary>The sum of the actions' numbers, each drawn in its row's min..max.</summary>
        public int Actions { get; }
        public int ServerRoll { get; }
        public int SmallTerm { get; }
        /// <summary>A conversion's success (R-5.2-04); true for every other kind.</summary>
        public bool Success { get; }

        /// <summary>A failed conversion keeps its inputs (the job tree's keepInputsPercent); false otherwise.</summary>
        public bool InputsKept { get; }
    }

    /// <summary>
    /// Every random number of a forge session, drawn at Finish from its logged seed in one fixed order (architecture rule 6):
    /// a conversion's success roll and its keep-inputs roll first (both drawn for every conversion), then each action in the
    /// order it was used (min..max, inclusive), then the server roll (0..cap) and the small term (-cap..cap). The same seed and
    /// inputs always give the same item, so a dispute can replay it.
    /// </summary>
    public static class ForgeRolls
    {
        /// <summary>The <c>Pcg32Rng</c> stream of forge rolls (the seed is per session).</summary>
        public const ulong Stream = 0x464F524745UL; // "FORGE"

        /// <param name="successPercent">A conversion's success chance; 0 for every other kind (no roll, success).</param>
        /// <param name="keepInputsPercent">The chance a failed conversion keeps its inputs (the job tree's keepInputsPercent).</param>
        public static ForgeRollResult Draw(ulong seed, IReadOnlyList<int> actionMin, IReadOnlyList<int> actionMax, int serverRollCap,
            int smallTermCap, int successPercent, int keepInputsPercent = 0)
        {
            var rng = new Pcg32Rng(seed, Stream);
            bool success = true, kept = false;
            if (successPercent > 0)
            {
                success = rng.RollPercent(successPercent);
                kept = rng.RollPercent(keepInputsPercent) && !success;
            }
            long actions = 0;
            for (int i = 0; i < actionMin.Count; i++)
                actions += rng.NextInt(actionMin[i], actionMax[i] + 1);
            int serverRoll = rng.NextInt(0, serverRollCap + 1);
            int smallTerm = rng.NextInt(-smallTermCap, smallTermCap + 1);
            return new ForgeRollResult(actions > int.MaxValue ? int.MaxValue : (int)actions, serverRoll, smallTerm, success, kept);
        }

        /// <summary>
        /// The UTC day of a time (days since the Unix epoch): the conversion daily cap counts per day from UTC midnight
        /// (spec 5.3 M3 decision 7; time from <c>IClock</c>, rule 6).
        /// </summary>
        public static long UtcDay(long utcMs) => utcMs >= 0 ? utcMs / MsPerDay : (utcMs - MsPerDay + 1) / MsPerDay;

        public const long MsPerDay = 86_400_000L;
    }
}
