#nullable enable
using System.Collections.Generic;
using System.Linq;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Dev.Qa
{
    /// <summary>
    /// What the QA run expects an item's inventory tooltip to list (spec 9.13 item stats, 4.1 M3 item model): pure over the
    /// tables and the saved slot, so it has EditMode tests.
    /// </summary>
    public static class QaItemStats
    {
        /// <summary>
        /// The <c>StatDef</c> ids the tooltip names for item <paramref name="itemId"/> in the saved slot
        /// <paramref name="saved"/>: a crafted instance's stats snapshot (protocol 26: stat indexes in <paramref name="stats"/>
        /// file order; lines with no value left out), else the item's <c>ItemStatBonusDef</c> rows. A forged sword has no
        /// such rows, so after <c>equip-crafted</c> the snapshot is what the tooltip shows (wave 1 QA F4).
        /// </summary>
        public static List<string> TooltipStatIds(string itemId, InventorySlotEntry? saved, IReadOnlyList<StatDef> stats,
            IEnumerable<ItemStatBonusDef> bonuses)
        {
            if (saved != null && saved.CraftedStats.Length > 0)
                return saved.CraftedStats
                    .Where(line => line.Stat < stats.Count && (line.Flat != 0 || line.Percent != 0))
                    .Select(line => stats[line.Stat].Id)
                    .Distinct()
                    .ToList();
            return bonuses.Where(row => row.ItemId == itemId).Select(row => row.StatId).Distinct().ToList();
        }

        /// <summary>
        /// Both are crafted instances with the same stats snapshot and enchant level, so swapping one for the other leaves the
        /// own stats as they are (wave 1 QA fix: a second smoke run equips a new forged sword over the first one; both Atk +12).
        /// </summary>
        public static bool SameCraftedBonuses(InventorySlotEntry? a, InventorySlotEntry? b)
        {
            if (a == null || b == null || a.CraftedStats.Length == 0 || a.CraftedStats.Length != b.CraftedStats.Length || a.Enchant != b.Enchant)
                return false;
            for (int i = 0; i < a.CraftedStats.Length; i++)
            {
                var x = a.CraftedStats[i];
                var y = b.CraftedStats[i];
                if (x.Stat != y.Stat || x.Flat != y.Flat || x.Percent != y.Percent)
                    return false;
            }
            return true;
        }
    }
}
