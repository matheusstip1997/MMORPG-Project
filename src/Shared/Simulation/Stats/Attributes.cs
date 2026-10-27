using System;
using Shared.Data.Tables;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// The primary attributes of R-3.4-01 (spec 3.4 "M2 primary attributes", user decision 2026-10-27): they come from the
    /// class and the level only, with no free points. <see cref="ClassDef"/> holds each attribute at level 1 (whole points)
    /// and its growth per later level in hundredths: <c>attribute(level) = atLevel1 + floor(growth x (level - 1) / 100)</c>.
    /// Pure integer math, shared by the server and client tooltips.
    /// </summary>
    public static class Attributes
    {
        public const int Count = 4;
        public const int Str = 0;
        public const int Dex = 1;
        public const int Sta = 2;
        public const int Int = 3;

        /// <summary>One attribute at <paramref name="level"/> (levels below 1 count as 1). Never below <paramref name="atLevel1"/>.</summary>
        public static int AtLevel(int atLevel1, int growthHundredths, int level)
        {
            long steps = Math.Max(0, level - 1);
            long value = atLevel1 + steps * Math.Max(0, growthHundredths) / 100;
            return (int)Math.Min(int.MaxValue, value);
        }

        /// <summary>Attribute <paramref name="attribute"/> (<see cref="Str"/> ... <see cref="Int"/>) of a class at a level.</summary>
        public static int Of(ClassDef classDef, int attribute, int level)
        {
            if (classDef == null)
                throw new ArgumentNullException(nameof(classDef));
            switch (attribute)
            {
                case Str: return AtLevel(classDef.Str, classDef.StrGrowth, level);
                case Dex: return AtLevel(classDef.Dex, classDef.DexGrowth, level);
                case Sta: return AtLevel(classDef.Sta, classDef.StaGrowth, level);
                case Int: return AtLevel(classDef.Int, classDef.IntGrowth, level);
                default: throw new ArgumentOutOfRangeException(nameof(attribute));
            }
        }

        /// <summary>Sets the four attribute bases of <paramref name="stats"/> for a class at a level (the block recomputes on read).</summary>
        public static void SetBases(StatBlock stats, AttributeFormulaRules formula, ClassDef classDef, int level)
        {
            if (stats == null)
                throw new ArgumentNullException(nameof(stats));
            if (formula == null)
                throw new ArgumentNullException(nameof(formula));
            for (int attribute = 0; attribute < Count; attribute++)
                stats.SetBase(formula.AttributeStat(attribute), Of(classDef, attribute, level));
        }
    }
}
