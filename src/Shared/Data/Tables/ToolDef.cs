using System;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A gathering tool (R-5.2-02, R-4.1-03; spec 4.1 M3 item model): its item (a <c>moldOrTool</c> instance), the tool type
    /// and tier it gates nodes by, and a yield bonus. Tools never wear out (R-4.1-03) and count while they sit in the Safe
    /// bag's equipment area: there is no tool equipment slot (user decision 2026-10-30). A <c>moldOrTool</c> item without a
    /// row here is a mold. TODO(WP-M3-6): every such item has one <c>MoldDef</c> row. PLACEHOLDERS. Catalog 8.5.
    /// </summary>
    [Table("ToolDef", 1)]
    public sealed class ToolDef : IValidatableRow, ISetValidatableRow
    {
        [TableId] public string Id { get; set; } = string.Empty;

        [Ref(typeof(ItemDef))] public string ItemId { get; set; } = string.Empty;

        public ToolType ToolType { get; set; }

        /// <summary>A <see cref="MaterialTierDef"/> tier: the highest node tier it harvests.</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        /// <summary>Added to the harvest yield, in percent (R-4.1-03 "may add a yield bonus").</summary>
        [Range(0, 1_000)] public int YieldBonusPercent { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!Enum.IsDefined(typeof(ToolType), ToolType) || ToolType == ToolType.None)
                errors.Add(nameof(ToolType), $"{(int)ToolType} is not a tool type");
        }

        /// <summary>The item is a mold-or-tool item with one tool row, and the tier is on the ladder.</summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, ItemId);
            if (item != null && item.Category != ItemCategory.MoldOrTool)
                errors.Add(nameof(ItemId), $"'{ItemId}' is {item.Category}; tools are moldOrTool items");
            if (MaterialTierDef.ByTier(set, Tier) == null)
                errors.Add(nameof(Tier), $"{Tier} is not a MaterialTierDef tier");
            foreach (var other in NetIds.Rows<ToolDef>(set))
            {
                if (ReferenceEquals(other, this))
                    break;
                if (other.ItemId == ItemId)
                {
                    errors.Add(nameof(ItemId), $"'{ItemId}' already has a tool row ('{other.Id}')");
                    break;
                }
            }
        }

        /// <summary>The tool row of an item, or null (a mold, or not a tool).</summary>
        public static ToolDef? ForItem(ConfigSet set, string itemId)
        {
            foreach (var row in NetIds.Rows<ToolDef>(set))
            {
                if (row.ItemId == itemId)
                    return row;
            }
            return null;
        }
    }
}
