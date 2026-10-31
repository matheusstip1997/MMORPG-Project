using System;
using System.Collections.Generic;

namespace Shared.Data.Tables
{
    /// <summary>
    /// A mold (R-5.3-01; spec 5.3 M3 decisions 6 and 7, WP-M3-6): the reusable mold item it is (the row id, a
    /// <c>moldOrTool</c> item without a <see cref="ToolDef"/> row), the job and job level that may use it, the station it
    /// is used at, its material slots (<see cref="MoldSlotDef"/> rows), the forge actions it allows and the item it makes.
    /// A craft always needs a mold (R-5.3-01), so a craft is a mold row, never a <see cref="RecipeDef"/>. A mold with no
    /// <see cref="OutputItemId"/> is known but not craftable yet (the Jeweler molds until WP-M3-9). <see cref="NetId"/>
    /// shares one namespace with <see cref="RecipeDef.NetId"/> (the forge's definition id on the wire and the job XP
    /// subject). Every number is a placeholder (balance is deferred). Catalog 8.5.
    /// </summary>
    [Table("MoldDef", 1)]
    public sealed class MoldDef : IValidatableRow, ISetValidatableRow
    {
        /// <summary>Largest net id of a mold or a recipe: it travels as 16 bits.</summary>
        public const int MaxNetId = ushort.MaxValue;

        /// <summary>The mold item (<c>moldOrTool</c>, no tool row).</summary>
        [TableId, Ref(typeof(ItemDef))] public string Id { get; set; } = string.Empty;

        /// <summary>Id on the wire, unique across <see cref="MoldDef"/> and <see cref="RecipeDef"/>.</summary>
        [Range(1, MaxNetId)] public int NetId { get; set; }

        /// <summary>The job that uses it (R-5.3-01 "each job creates its own molds").</summary>
        [Ref(typeof(JobDef))] public string JobId { get; set; } = string.Empty;

        /// <summary>Where it is used: <c>forge</c> or <c>jewelerBench</c> (spec 5.3 M3 decision 7).</summary>
        public NpcInteraction Station { get; set; }

        /// <summary>The job level needed (R-5.3-01 "higher-tier molds unlock with job level").</summary>
        [Range(1, 1_000)] public int MinJobLevel { get; set; }

        /// <summary>The difficulty tier of a craft with it (job XP, R-5.1-04).</summary>
        [Range(1, MaterialTierDef.MaxTier)] public int Tier { get; set; }

        /// <summary>What it makes (equipment, or a tool); empty while the mold is not craftable yet.</summary>
        [Optional, Ref(typeof(ItemDef), Optional = true)] public string OutputItemId { get; set; } = string.Empty;

        /// <summary>The forge actions a session with this mold may use (R-5.3-01 "allowed forge actions").</summary>
        [Optional, Ref(typeof(ForgeActionDef))] public string[] ForgeActionIds { get; set; } = Array.Empty<string>();

        public bool IsCraftable() => OutputItemId.Length > 0;

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(Id))
                errors.Add(nameof(Id), "must not be empty");
            if (!ForgeStations.IsStation(Station))
                errors.Add(nameof(Station), $"{Station} is not a crafting station (forge or jewelerBench)");
            for (int i = 0; i < ForgeActionIds.Length; i++)
            {
                if (Array.IndexOf(ForgeActionIds, ForgeActionIds[i]) != i)
                    errors.Add(nameof(ForgeActionIds), $"lists '{ForgeActionIds[i]}' twice");
            }
            if (ForgeActionIds.Length > ForgeStations.MaxActionKinds)
                errors.Add(nameof(ForgeActionIds), $"at most {ForgeStations.MaxActionKinds} actions");
        }

        /// <summary>
        /// The mold item is a mold (no tool row), the output is equipment or a tool, a craftable mold has slots, the job
        /// level is below the job cap and the net id is unique among molds and recipes.
        /// </summary>
        public void Validate(ConfigSet set, RowErrors errors)
        {
            var item = ItemDef.Find(set, Id);
            if (item != null && item.Category != ItemCategory.MoldOrTool)
                errors.Add(nameof(Id), $"'{Id}' is {item.Category}; molds are moldOrTool items");
            if (set.TryGet(typeof(ToolDef), out _) && ToolDef.ForItem(set, Id) != null)
                errors.Add(nameof(Id), $"'{Id}' is a tool (it has a ToolDef row), not a mold");
            if (IsCraftable())
            {
                var output = ItemDef.Find(set, OutputItemId);
                if (output != null && output.Category != ItemCategory.Equipment
                    && !(output.Category == ItemCategory.MoldOrTool && set.TryGet(typeof(ToolDef), out _)
                         && ToolDef.ForItem(set, OutputItemId) != null))
                    errors.Add(nameof(OutputItemId), $"'{OutputItemId}' is {output.Category}; a mold makes equipment or a tool");
                if (set.TryGet(typeof(MoldSlotDef), out _) && Slots(set, Id).Count == 0)
                    errors.Add(nameof(OutputItemId), "a craftable mold needs at least one MoldSlotDef row");
            }
            else if (set.TryGet(typeof(MoldSlotDef), out _) && Slots(set, Id).Count > 0)
                errors.Add(nameof(OutputItemId), "a mold with slots needs an output item");
            var settings = JobSettings.Find(set);
            if (settings != null && MinJobLevel > settings.MaxLevel)
                errors.Add(nameof(MinJobLevel), $"{MinJobLevel} is above the job level cap {settings.MaxLevel}");
            NetIds.CheckUnique<MoldDef>(set, this, NetId, row => row.NetId, row => row.Id, errors);
            foreach (var recipe in NetIds.Rows<RecipeDef>(set))
            {
                if (recipe.NetId == NetId)
                    errors.Add(nameof(NetId), $"{NetId} is also the net id of recipe '{recipe.Id}'");
            }
        }

        public static MoldDef? Find(ConfigSet set, string id) => NetIds.Find<MoldDef>(set, id, row => row.Id);

        /// <summary>The mold's slot rows in file order (the order of a forge start's inputs).</summary>
        public static List<MoldSlotDef> Slots(ConfigSet set, string moldId)
        {
            var slots = new List<MoldSlotDef>();
            foreach (var row in NetIds.Rows<MoldSlotDef>(set))
            {
                if (row.MoldId == moldId)
                    slots.Add(row);
            }
            return slots;
        }
    }

    /// <summary>The crafting stations of spec 5.3 M3 decision 7 and the forge's limits on the wire.</summary>
    public static class ForgeStations
    {
        /// <summary>Most input rows (mold slots or recipe inputs) a forge start names.</summary>
        public const int MaxInputs = 8;

        /// <summary>Most forge actions a mold allows.</summary>
        public const int MaxActionKinds = 8;

        /// <summary>Most actions a forge session may hold (the <see cref="ForgeSettings.MaxActions"/> bound).</summary>
        public const int MaxSessionActions = 32;

        public static bool IsStation(NpcInteraction interaction) =>
            interaction == NpcInteraction.Forge || interaction == NpcInteraction.JewelerBench;
    }
}
