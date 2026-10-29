using System;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Simulation.Progression
{
    /// <summary>
    /// The exp tables of spec 3.6 as arrays (<c>ProgressionSettings</c>, <c>ExpCurve</c>, <c>ExpBandCurves</c>,
    /// <c>RankModifier</c>, <c>RankLevelRanges</c>), built once from a validated config set. Immutable, so the world thread
    /// and tools share one. All the numbers are placeholders (spec 3.6 "M2 placeholder curves").
    /// </summary>
    public sealed class ProgressionCurves
    {
        private readonly int[] _expToNext;
        private readonly int[] _monsterBaseExp;
        private readonly int[] _bandMin = new int[Ranks.Count];
        private readonly int[] _bandMax = new int[Ranks.Count];
        private readonly int[] _rankPercents;
        private readonly int _firstRankDiff;
        private readonly ExpBandCurves _band;

        private ProgressionCurves(ProgressionSettings settings, int[] expToNext, int[] monsterBaseExp, ExpBandCurves band,
            int firstRankDiff, int[] rankPercents)
        {
            MaxLevel = settings.MaxLevel;
            SkillPointsPerLevel = settings.SkillPointsPerLevel;
            AttributePointsPerLevel = settings.AttributePointsPerLevel;
            _expToNext = expToNext;
            _monsterBaseExp = monsterBaseExp;
            _band = band;
            _firstRankDiff = firstRankDiff;
            _rankPercents = rankPercents;
        }

        /// <summary>Placeholder level cap (<c>ProgressionSettings.maxLevel</c>).</summary>
        public int MaxLevel { get; }

        /// <summary>R-3.5-04: skill points per level.</summary>
        public int SkillPointsPerLevel { get; }

        /// <summary>R-3.4-08: free attribute points per level from level 2.</summary>
        public int AttributePointsPerLevel { get; }

        /// <summary>
        /// The curves of a config set that passed <see cref="TableValidator"/>. Returns null with <paramref name="error"/>
        /// when a table is missing or the curve does not cover every level below the cap.
        /// </summary>
        public static ProgressionCurves? TryBuild(ConfigSet set, out string? error)
        {
            error = null;
            var settings = ProgressionSettings.Find(set);
            if (settings == null || !set.TryGet(typeof(ExpCurve), out var curve) || !set.TryGet(typeof(ExpBandCurves), out var band)
                || band.Rows.Count != 1 || !set.TryGet(typeof(RankModifier), out var modifiers) || modifiers.Rows.Count == 0
                || !set.TryGet(typeof(RankLevelRanges), out var ranges))
            {
                error = "the progression tables (ProgressionSettings, ExpCurve, ExpBandCurves, RankModifier, RankLevelRanges) are not all loaded";
                return null;
            }
            if (curve.Rows.Count != settings.MaxLevel - 1)
            {
                error = $"ExpCurve has {curve.Rows.Count} rows, ProgressionSettings.maxLevel {settings.MaxLevel} needs {settings.MaxLevel - 1}";
                return null;
            }
            var expToNext = new int[curve.Rows.Count];
            var baseExp = new int[curve.Rows.Count];
            for (int i = 0; i < curve.Rows.Count; i++)
            {
                var row = (ExpCurve)curve.Rows[i];
                expToNext[i] = row.ExpToNext;
                baseExp[i] = row.MonsterBaseExp;
            }
            var percents = new int[modifiers.Rows.Count];
            for (int i = 0; i < percents.Length; i++)
                percents[i] = ((RankModifier)modifiers.Rows[i]).Percent;
            var curves = new ProgressionCurves(settings, expToNext, baseExp, (ExpBandCurves)band.Rows[0],
                ((RankModifier)modifiers.Rows[0]).RankDiff, percents);
            foreach (var row in ranges.Rows)
            {
                var range = (RankLevelRanges)row;
                curves._bandMin[(int)range.Rank] = range.MinLevel;
                curves._bandMax[(int)range.Rank] = range.MaxLevel;
            }
            return curves;
        }

        /// <summary>Exp from <paramref name="level"/> to the next one; 0 at the cap.</summary>
        public long ExpToNext(int level) => level >= 1 && level < MaxLevel ? _expToNext[level - 1] : 0;

        /// <summary>Base exp of an even-level monster at <paramref name="level"/> (clamped to the curve).</summary>
        public int MonsterBaseExp(int level) => _monsterBaseExp[Math.Max(1, Math.Min(level, _monsterBaseExp.Length)) - 1];

        /// <summary>A monster's base exp: its own <c>baseExp</c>, else the curve's at its level (spec 3.6).</summary>
        public int BaseExpOf(MonsterDef monster) => monster.BaseExp > 0 ? monster.BaseExp : MonsterBaseExp(monster.Level);

        /// <summary>The band modifier of a character, in parts per million (R-3.6-03).</summary>
        public long BandPpm(int level, Rank rank) => ExpMath.BandPpm(level, _bandMin[(int)rank], _bandMax[(int)rank],
            _band.BelowBonusPercentPerLevel, _band.BelowBonusCapPercent, _band.AboveMultiplierPercentPerLevel);

        /// <summary>The rank modifier of a monster's rank against a character's (R-3.6-03), in percent.</summary>
        public int RankPercent(Rank player, Rank monster) => ExpMath.RankPercent((int)monster - (int)player, _firstRankDiff, _rankPercents);

        /// <summary>Exp a character gets for a kill (R-3.6-03), before the cap.</summary>
        public long Gain(MonsterDef monster, int level, Rank rank) =>
            ExpMath.Gain(BaseExpOf(monster), RankPercent(rank, monster.Rank), BandPpm(level, rank));

        /// <summary>Adds exp and takes the level-ups it pays for (<see cref="ExpMath.Apply"/>). Returns the levels gained.</summary>
        public int Apply(ref int level, ref long exp, long gain) => ExpMath.Apply(ref level, ref exp, gain, _expToNext, MaxLevel);
    }
}
