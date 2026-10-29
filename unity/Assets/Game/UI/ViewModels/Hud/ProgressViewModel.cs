#nullable enable
using System;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Game.UI.ViewModels.Hud
{
    /// <summary>
    /// The own level, exp, rank and skill points (spec 3.6, 9.3 <c>exp_bar</c>, <c>exp_note</c> and the resource panel line;
    /// WP-M2-3), from <see cref="ProgressionState"/>. Display only: the server computes every number.
    /// </summary>
    public sealed class ProgressViewModel : ViewModel
    {
        public bool HasValues { get; private set; }
        public int Level { get; private set; }
        public long Exp { get; private set; }

        /// <summary>Exp the current level needs; 0 at the level cap (the bar shows full).</summary>
        public long ExpToNext { get; private set; }

        public Rank Rank { get; private set; }
        public int SkillPoints { get; private set; }

        /// <summary>The band modifier of R-3.6-03 in percent (100 = x1.0); the note shows when it is below 100.</summary>
        public int BandPercent { get; private set; } = 100;

        public void Set(ProgressionState state)
        {
            if (HasValues && state.Level == Level && state.Exp == Exp && state.ExpToNext == ExpToNext && state.Rank == Rank
                && state.SkillPoints == SkillPoints && state.BandPercent == BandPercent)
                return;
            HasValues = true;
            Level = state.Level;
            Exp = state.Exp;
            ExpToNext = state.ExpToNext;
            Rank = state.Rank;
            SkillPoints = state.SkillPoints;
            BandPercent = state.BandPercent;
            NotifyChanged();
        }

        public void Clear()
        {
            if (!HasValues)
                return;
            HasValues = false;
            NotifyChanged();
        }

        /// <summary>Exp bar fill 0 to 1: full at the level cap, empty before the first message.</summary>
        public float ExpFraction =>
            !HasValues ? 0f : ExpToNext <= 0 ? 1f : Math.Max(0f, Math.Min(1f, Exp / (float)ExpToNext));

        /// <summary>
        /// Whole percent of the level's exp, rounded down (WP-M2-11 <c>exp_label</c>): 100 at the level cap, 0 before the
        /// first message. Integer math.
        /// </summary>
        public int Percent =>
            !HasValues ? 0 : ExpToNext <= 0 ? 100 : (int)Math.Max(0L, Math.Min(100L, Exp * 100L / ExpToNext));

        /// <summary>The exp note shows while the band modifier is below x1.0 (spec 9.3 <c>exp_note</c>).</summary>
        public bool ShowNote => HasValues && BandPercent < 100;

        /// <summary>The multiplier with two decimals, for the note ("0.36"); integer math, culture-independent.</summary>
        public string MultiplierText => $"{BandPercent / 100}.{BandPercent % 100:00}";
    }
}
