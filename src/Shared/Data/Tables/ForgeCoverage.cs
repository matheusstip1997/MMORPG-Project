using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// DataValidator rules across the forge tables (spec 5.3 M3 implementation, WP-M3-6): every mold item (a
    /// <c>moldOrTool</c> item without a <see cref="ToolDef"/> row) has exactly one <see cref="MoldDef"/>; every
    /// <see cref="QualityComponent"/> has one <see cref="QualityCaps"/> row; every <see cref="RarityDef"/> has a
    /// <see cref="QualityThresholds"/> row. Skipped quietly per rule when a table is not loaded (the loader reports that).
    /// </summary>
    public static class ForgeCoverage
    {
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (set.TryGet(typeof(MoldDef), out var molds) && set.TryGet(typeof(ToolDef), out _))
            {
                var covered = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in molds.Rows)
                    covered.Add(((MoldDef)row).Id);
                foreach (var item in NetIds.Rows<ItemDef>(set))
                {
                    if (item.Category == ItemCategory.MoldOrTool && ToolDef.ForItem(set, item.Id) == null
                        && !covered.Contains(item.Id))
                        errors.Add(Error(typeof(MoldDef), nameof(MoldDef.Id), $"no row for mold item '{item.Id}'"));
                }
            }
            if (set.TryGet(typeof(QualityCaps), out var caps))
            {
                var seen = new HashSet<QualityComponent>();
                foreach (var row in caps.Rows)
                {
                    if (QualityCaps.TryComponent(((QualityCaps)row).Id, out var component))
                        seen.Add(component);
                }
                foreach (QualityComponent component in Enum.GetValues(typeof(QualityComponent)))
                {
                    if (component != QualityComponent.None && !seen.Contains(component))
                        errors.Add(Error(typeof(QualityCaps), nameof(QualityCaps.Id), $"no row for quality component {component}"));
                }
            }
            if (set.TryGet(typeof(QualityThresholds), out var thresholds))
            {
                var rarities = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in thresholds.Rows)
                    rarities.Add(((QualityThresholds)row).RarityId);
                foreach (var rarity in NetIds.Rows<RarityDef>(set))
                {
                    if (!rarities.Contains(rarity.Id))
                        errors.Add(Error(typeof(QualityThresholds), nameof(QualityThresholds.RarityId),
                            $"no threshold for rarity '{rarity.Id}' (common up to legendary, spec 4.1)"));
                }
            }
            return errors;
        }

        private static ValidationError Error(Type table, string field, string message) =>
            new ValidationError(ValidationCodes.RuleBroken, TableInfo.For(table).Name, null, field, message);
    }
}
