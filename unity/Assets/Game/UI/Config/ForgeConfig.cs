#nullable enable
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Crafting;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the forge window (spec 9.1: no user-facing text in code; spec 5.3, 9.13; WP-M3-6). An EditMode
    /// test checks that each constant has a row; slot and action names are their <c>MoldSlotDef.nameId</c> and
    /// <c>ForgeActionDef.nameId</c>, item names their <c>ItemDef.nameId</c>.
    /// </summary>
    public static class ForgeTextIds
    {
        public const string Title = "forge.title";
        public const string Close = "forge.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "forge.closeLabel";
        public const string Choose = "forge.choose";
        /// <summary>Job level {level}.</summary>
        public const string MinLevel = "forge.minLevel";
        public const string Empty = "forge.empty";
        /// <summary>{slot}: {item} x{count}.</summary>
        public const string Input = "forge.input";
        /// <summary>{slot}: no material (needs {count}).</summary>
        public const string InputMissing = "forge.inputMissing";
        public const string Next = "forge.next";
        public const string Start = "forge.start";
        /// <summary>Actions {used}/{max}.</summary>
        public const string Actions = "forge.actions";
        /// <summary>Quality {min} to {max} ({low} to {high}).</summary>
        public const string Quality = "forge.quality";
        public const string Finish = "forge.finish";
        public const string Cancel = "forge.cancel";
        public const string Waiting = "forge.waiting";
        public const string RecipeSlot = "forge.recipeSlot";
        public const string ToastMade = "forge.toast.made";
        public const string ToastConversionFailed = "forge.toast.conversionFailed";
        public const string ToastInputsKept = "forge.toast.inputsKept";
        /// <summary>WP-M3-14 (spec 5.3 M3 decision 13): the Recipes tab, the mold slot and the material slots, the picker.</summary>
        public const string TabRecipes = "forge.tab.recipes";
        /// <summary>Mold: {item}.</summary>
        public const string MoldSlot = "forge.moldSlot";
        public const string MoldEmpty = "forge.moldEmpty";
        /// <summary>{slot}: drag a material here or click to choose.</summary>
        public const string SlotEmpty = "forge.slotEmpty";
        public const string Clear = "forge.clear";
        public const string ClearLabel = "forge.clearLabel";
        /// <summary>Choose: {slot}.</summary>
        public const string PickTitle = "forge.pick.title";
        public const string PickEmpty = "forge.pick.empty";
        public const string PickCancel = "forge.pick.cancel";
        /// <summary>{item} x{count}.</summary>
        public const string PickEntry = "forge.pick.entry";
        public const string PickMoldsEmpty = "forge.pick.moldsEmpty";
        public const string PickMold = "forge.pick.mold";
        /// <summary>The stat preview's title and its lines: {stat} {low} to {high}, the percent form.</summary>
        public const string StatsTitle = "forge.stats.title";
        public const string StatRange = "forge.stats.range";
        public const string StatRangePercent = "forge.stats.rangePercent";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, Choose, MinLevel, Empty, Input, InputMissing, Next, Start, Actions, Quality, Finish, Cancel, Waiting,
            RecipeSlot, ToastMade, ToastConversionFailed, ToastInputsKept, TabRecipes, MoldSlot, MoldEmpty, SlotEmpty, Clear, ClearLabel,
            PickTitle, PickEmpty, PickCancel, PickEntry, PickMoldsEmpty, PickMold, StatsTitle, StatRange, StatRangePercent,
        };
    }

    /// <summary>
    /// The config tables the forge window reads (spec 5.3, 9.13; WP-M3-6), from a loaded and validated <see cref="ConfigSet"/>:
    /// the forge rules (the same <see cref="ForgeRules"/> the server checks a start with), the rarity names and the Normal
    /// bag's code. The window only describes; the server decides every input, roll and item.
    /// </summary>
    public sealed class ForgeConfig
    {
        public ForgeConfig(ForgeRules? rules, byte normalBag, string[] rarityNameIds, byte moldContainer = 0)
        {
            Rules = rules;
            NormalBag = normalBag;
            RarityNameIds = rarityNameIds;
            MoldContainer = moldContainer;
        }

        /// <summary>The forge tables; null when they are not loaded (the window then lists nothing).</summary>
        public ForgeRules? Rules { get; }

        public byte NormalBag { get; }

        /// <summary>The Safe bag's equipment area (the container that receives molds), where the mold slot's mold must be.</summary>
        public byte MoldContainer { get; }

        /// <summary>The <c>UiStrings</c> id of each rarity, by <c>RarityDef.tier</c>.</summary>
        public string[] RarityNameIds { get; }

        public string RarityNameId(int tier) => tier >= 0 && tier < RarityNameIds.Length ? RarityNameIds[tier] : string.Empty;

        public static ForgeConfig From(ConfigSet set)
        {
            var names = new string[256];
            int highest = 0;
            if (set.TryGet(typeof(RarityDef), out var rarities))
            {
                foreach (var row in rarities.Rows)
                {
                    var rarity = (RarityDef)row;
                    names[rarity.Tier] = "rarity." + rarity.Id;
                    highest = System.Math.Max(highest, rarity.Tier);
                }
            }
            var trimmed = new string[highest + 1];
            for (int i = 0; i < trimmed.Length; i++)
                trimmed[i] = names[i] ?? string.Empty;
            var bag = ContainerDef.Find(set, ContainerDef.NormalBag);
            var molds = ContainerDef.FindReceiver(set, ItemCategory.MoldOrTool);
            return new ForgeConfig(ForgeRules.From(set), (byte)(bag?.Code ?? 1), trimmed, (byte)(molds?.Code ?? 0));
        }
    }
}
