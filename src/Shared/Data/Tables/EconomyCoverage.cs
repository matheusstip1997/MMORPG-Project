using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// DataValidator rules across the economy tables (spec 4.2): every sell-only item has a <see cref="PriceBase"/> row,
    /// every rarity a <see cref="RarityMult"/> row, and every <see cref="DemandCurve.Required"/> curve exists. Skipped
    /// quietly when a table is not loaded (the loader reports that).
    /// </summary>
    public static class EconomyCoverage
    {
        public static List<ValidationError> Check(ConfigSet set)
        {
            var errors = new List<ValidationError>();
            if (set.TryGet(typeof(PriceBase), out var prices))
            {
                var priced = new HashSet<string>();
                foreach (var row in prices.Rows)
                    priced.Add(((PriceBase)row).ItemId);
                foreach (var item in NetIds.Rows<ItemDef>(set))
                {
                    if (item.Category == ItemCategory.SellOnly && !priced.Contains(item.Id))
                        errors.Add(Error(typeof(PriceBase), nameof(PriceBase.ItemId), $"no row for sell-only item '{item.Id}'"));
                }
            }
            if (set.TryGet(typeof(RarityMult), out var mults))
            {
                var covered = new HashSet<string>();
                foreach (var row in mults.Rows)
                    covered.Add(((RarityMult)row).RarityId);
                foreach (var rarity in NetIds.Rows<RarityDef>(set))
                {
                    if (!covered.Contains(rarity.Id))
                        errors.Add(Error(typeof(RarityMult), nameof(RarityMult.RarityId), $"no row for rarity '{rarity.Id}'"));
                }
            }
            if (set.TryGet(typeof(DemandCurve), out _))
            {
                foreach (var id in DemandCurve.Required)
                {
                    if (DemandCurve.Find(set, id) == null)
                        errors.Add(Error(typeof(DemandCurve), nameof(DemandCurve.Id), $"no '{id}' curve"));
                }
            }
            return errors;
        }

        private static ValidationError Error(System.Type table, string field, string message) =>
            new ValidationError(ValidationCodes.RuleBroken, TableInfo.For(table).Name, null, field, message);
    }
}
