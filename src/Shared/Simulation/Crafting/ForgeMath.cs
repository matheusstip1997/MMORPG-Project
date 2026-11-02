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

        /// <summary>The lowest quality of band <paramref name="band"/>.</summary>
        public int MinQuality(int band) => _minQuality[band];

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

    /// <summary>The lowest and highest value of one stat a forge can still give (<see cref="CraftedStatsCalc.Range"/>).</summary>
    public readonly struct CraftedStatRange
    {
        public CraftedStatRange(string statId, int flatLow, int flatHigh, int percentLow, int percentHigh)
        {
            StatId = statId;
            FlatLow = flatLow;
            FlatHigh = flatHigh;
            PercentLow = percentLow;
            PercentHigh = percentHigh;
        }

        public string StatId { get; }
        public int FlatLow { get; }
        public int FlatHigh { get; }
        public int PercentLow { get; }
        public int PercentHigh { get; }

        public CraftedStatRange Widen(int flat, int percent) => new CraftedStatRange(StatId, Math.Min(FlatLow, flat),
            Math.Max(FlatHigh, flat), Math.Min(PercentLow, percent), Math.Max(PercentHigh, percent));
    }

    /// <summary>
    /// One material stat line (a <c>MaterialStatDef</c> range per unit, WP-M3-14) and the units the craft used of it.
    /// </summary>
    public readonly struct MaterialStatUse
    {
        public MaterialStatUse(string statId, int flatMin, int flatMax, int percentMin, int percentMax, int units)
        {
            StatId = statId;
            FlatMin = flatMin;
            FlatMax = flatMax;
            PercentMin = percentMin;
            PercentMax = percentMax;
            Units = units;
        }

        public string StatId { get; }
        public int FlatMin { get; }
        public int FlatMax { get; }
        public int PercentMin { get; }
        public int PercentMax { get; }
        public int Units { get; }
    }

    /// <summary>
    /// The crafted stats snapshot (R-5.3-02 "item stats = materials used + a quality bonus"; spec 5.3 "output is a stats
    /// snapshot"; WP-M3-6, ranges WP-M3-14 / M3 decision 13): every material line's range times its units, placed by the
    /// craft's quality (<see cref="Position"/>), summed per stat in first-seen order, then raised by the quality band's
    /// percent (integer, rounded toward zero). No random draw: the quality already holds the seed's rolls and the minigame,
    /// so the same session replays the same stats. At most <see cref="ItemProperties.MaxCraftedStats"/> lines; a stat whose
    /// sums are both zero is left out.
    /// </summary>
    public static class CraftedStatsCalc
    {
        /// <summary>
        /// The value of a range <paramref name="low"/>..<paramref name="high"/> (low &lt;= high) at a quality: with
        /// q = clamp(quality, 0, Q) and Q = <paramref name="rangeQuality"/>, low + ((high - low) * q * 2 + Q) / (2 * Q),
        /// rounded half up (spec 5.3 M3 decision 13: an ingot atk 10..12 at quality 500 of 1000 gives 11).
        /// </summary>
        public static long Position(long low, long high, int quality, int rangeQuality)
        {
            if (rangeQuality <= 0 || high <= low)
                return low;
            long q = quality < 0 ? 0 : quality > rangeQuality ? rangeQuality : quality;
            return low + ((high - low) * q * 2 + rangeQuality) / (2L * rangeQuality);
        }

        public static List<ItemStatBonus> Build(IReadOnlyList<MaterialStatUse> uses, int quality, int rangeQuality, int bonusPercent)
        {
            var result = new List<ItemStatBonus>();
            foreach (var line in Lines(uses, quality, rangeQuality, bonusPercent))
            {
                if (line.Flat == 0 && line.Percent == 0)
                    continue;
                if (result.Count == ItemProperties.MaxCraftedStats)
                    break;
                result.Add(line);
            }
            return result;
        }

        /// <summary>
        /// The lowest and highest value of each stat the item can end with for a quality in
        /// <paramref name="qualityMin"/>..<paramref name="qualityMax"/> (the forge window's preview, never a rolled number):
        /// inside one quality band the value never falls as the quality rises, so the ends of the range and the edges of
        /// every band inside it are the only candidates (exact for negative lines too). Stats in first-seen order.
        /// </summary>
        public static List<CraftedStatRange> Range(IReadOnlyList<MaterialStatUse> uses, int qualityMin, int qualityMax, int rangeQuality,
            QualityBands bands)
        {
            if (qualityMax < qualityMin)
                qualityMax = qualityMin;
            var qualities = new List<int> { qualityMin, qualityMax };
            for (int band = 1; band < bands.Count; band++)
            {
                int edge = bands.MinQuality(band);
                if (edge > qualityMin && edge <= qualityMax)
                {
                    qualities.Add(edge);
                    qualities.Add(edge - 1);
                }
            }
            var result = new List<CraftedStatRange>();
            foreach (int quality in qualities)
            {
                var lines = Lines(uses, quality, rangeQuality, bands.StatBonusPercent(quality));
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    if (i == result.Count)
                        result.Add(new CraftedStatRange(line.StatId, line.Flat, line.Flat, line.Percent, line.Percent));
                    else
                        result[i] = result[i].Widen(line.Flat, line.Percent);
                }
            }
            result.RemoveAll(r => r.FlatLow == 0 && r.FlatHigh == 0 && r.PercentLow == 0 && r.PercentHigh == 0);
            if (result.Count > ItemProperties.MaxCraftedStats)
                result.RemoveRange(ItemProperties.MaxCraftedStats, result.Count - ItemProperties.MaxCraftedStats);
            return result;
        }

        /// <summary>Every stat's line at a quality, zero lines included, in first-seen order.</summary>
        private static List<ItemStatBonus> Lines(IReadOnlyList<MaterialStatUse> uses, int quality, int rangeQuality, int bonusPercent)
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
                flat[use.StatId] += Position((long)use.FlatMin * use.Units, (long)use.FlatMax * use.Units, quality, rangeQuality);
                percent[use.StatId] += Position((long)use.PercentMin * use.Units, (long)use.PercentMax * use.Units, quality, rangeQuality);
            }
            var result = new List<ItemStatBonus>(order.Count);
            foreach (var stat in order)
                result.Add(new ItemStatBonus(stat, Scale(flat[stat], bonusPercent), Scale(percent[stat], bonusPercent)));
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
