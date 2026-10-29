using System;
using System.Collections.Generic;
using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Simulation.Stats
{
    /// <summary>
    /// The runtime form of <see cref="AttributeFormula"/> (spec 3.4 "M2 primary attributes"): for each derived stat, a
    /// coefficient per attribute in hundredths, indexed by <see cref="StatRules"/> stat id. A <see cref="StatBlock"/> built
    /// with it adds <c>floor(sum(coefficient x final attribute) / 100)</c> to the stat's base before the modifiers.
    /// Immutable; built once by <see cref="CombatConfig.TryBuild"/>.
    /// </summary>
    public sealed class AttributeFormulaRules
    {
        private readonly int[] _attributeStats = new int[Attributes.Count];
        private readonly bool[] _isAttribute;
        // Coefficients by stat id and attribute (stat count x 4); a stat without a row has none.
        private readonly int[] _coefficients;
        private readonly bool[] _hasRow;

        private AttributeFormulaRules(StatRules stats)
        {
            Stats = stats;
            _isAttribute = new bool[stats.Count];
            _coefficients = new int[stats.Count * Attributes.Count];
            _hasRow = new bool[stats.Count];
        }

        public StatRules Stats { get; }

        /// <summary>Stat id of attribute <paramref name="attribute"/> (<see cref="Attributes.Str"/> ... <see cref="Attributes.Int"/>).</summary>
        public int AttributeStat(int attribute) => _attributeStats[attribute];

        public bool IsAttribute(int statId) => _isAttribute[statId];

        /// <summary>True when the stat has an <see cref="AttributeFormula"/> row.</summary>
        public bool HasRow(int statId) => _hasRow[statId];

        public int Coefficient(int statId, int attribute) => _coefficients[statId * Attributes.Count + attribute];

        /// <summary>What the attributes add to a stat's base: floor(sum(coefficient x attribute) / 100), clamped to int.</summary>
        public int Contribution(int statId, int str, int dex, int sta, int intelligence)
        {
            if (!_hasRow[statId])
                return 0;
            int row = statId * Attributes.Count;
            long sum = (long)_coefficients[row + Attributes.Str] * str + (long)_coefficients[row + Attributes.Dex] * dex
                + (long)_coefficients[row + Attributes.Sta] * sta + (long)_coefficients[row + Attributes.Int] * intelligence;
            long value = StatBlock.FloorDiv(sum, 100);
            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));
        }

        /// <summary>
        /// Builds the formula, adding one error per broken cross-table rule: the four attribute stats exist in
        /// <see cref="StatDef"/>, every row names a stat, and no row names an attribute (an attribute fed by attributes
        /// would be a cycle). Returns null when anything is wrong.
        /// </summary>
        public static AttributeFormulaRules? TryBuild(StatRules stats, IReadOnlyList<AttributeFormula> rows, List<ValidationError> errors)
        {
            if (stats == null)
                throw new ArgumentNullException(nameof(stats));
            if (rows == null)
                throw new ArgumentNullException(nameof(rows));
            int errorCount = errors.Count;
            string table = TableInfo.For(typeof(AttributeFormula)).Name;
            var formula = new AttributeFormulaRules(stats);
            for (int attribute = 0; attribute < Attributes.Count; attribute++)
            {
                if (stats.TryGetId(StatIds.Attributes[attribute], out int statId))
                {
                    formula._attributeStats[attribute] = statId;
                    formula._isAttribute[statId] = true;
                }
                else
                {
                    errors.Add(new ValidationError(ValidationCodes.BrokenRef, TableInfo.For(typeof(StatDef)).Name, null, null,
                        $"attribute stat '{StatIds.Attributes[attribute]}' is required by AttributeFormula (R-3.4-01)"));
                }
            }
            if (errors.Count > errorCount)
                return null;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                if (!stats.TryGetId(row.Id, out int statId))
                {
                    errors.Add(new ValidationError(ValidationCodes.BrokenRef, table, i, nameof(AttributeFormula.Id),
                        $"'{row.Id}' does not exist in StatDef"));
                    continue;
                }
                if (formula._isAttribute[statId])
                {
                    errors.Add(new ValidationError(ValidationCodes.RuleBroken, table, i, nameof(AttributeFormula.Id),
                        $"'{row.Id}' is a primary attribute; attributes are not derived from attributes"));
                    continue;
                }
                if (formula._hasRow[statId])
                {
                    errors.Add(new ValidationError(ValidationCodes.DuplicateId, table, i, nameof(AttributeFormula.Id),
                        $"'{row.Id}' has more than one row"));
                    continue;
                }
                formula._hasRow[statId] = true;
                int at = statId * Attributes.Count;
                formula._coefficients[at + Attributes.Str] = row.Str;
                formula._coefficients[at + Attributes.Dex] = row.Dex;
                formula._coefficients[at + Attributes.Sta] = row.Sta;
                formula._coefficients[at + Attributes.Int] = row.Int;
            }
            return errors.Count > errorCount ? null : formula;
        }
    }
}
