using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The forge tables (spec 5.3 M3 implementation, WP-M3-6): <see cref="MoldDef"/>, <see cref="MoldSlotDef"/>,
/// <see cref="RecipeDef"/>, <see cref="RecipeInputDef"/>, <see cref="ForgeActionDef"/>, <see cref="QualityCaps"/>,
/// <see cref="QualityThresholds"/>, <see cref="ConversionOdds"/> and <see cref="ForgeCoverage"/>: one test per validator
/// rule, and the committed rows.
/// </summary>
public class ForgeTablesTests
{
    private static ItemDef Item(string id, int netId, ItemCategory category, int tier = 0, params string[] tags) => new()
    {
        Id = id, NetId = netId, Category = category, MaxStack = ItemCategories.IsInstance(category) ? 1 : 99, VisualId = "drop",
        NameId = "item.name", Tier = tier, MaterialTags = tags,
    };

    private static ItemDef Gear(string id, int netId)
    {
        var item = Item(id, netId, ItemCategory.Equipment);
        item.EquipSlots = new[] { "weaponMain" };
        return item;
    }

    private static MoldDef Mold(string id = "mold", int netId = 1, string output = "sword") => new()
    {
        Id = id, NetId = netId, JobId = "smith", Station = NpcInteraction.Forge, MinJobLevel = 1, Tier = 1, OutputItemId = output,
        ForgeActionIds = new[] { "hit" },
    };

    private static RecipeDef Recipe(string id = "refine", int netId = 101, RecipeType type = RecipeType.Refine, string output = "ingot",
        int tier = 1) => new()
    {
        Id = id, NetId = netId, Type = type, Station = NpcInteraction.Forge, JobId = "smith", MinJobLevel = 1, Tier = tier,
        OutputItemId = output, OutputCount = 1,
    };

    private sealed class Rows
    {
        public List<ItemDef> Items = new()
        {
            Item("ore", 1, ItemCategory.Material, 1, "ore"), Item("ore2", 2, ItemCategory.Material, 2, "ore"),
            Item("ingot", 3, ItemCategory.Material, 1, "ingot"), Gear("sword", 4),
            Item("mold", 5, ItemCategory.MoldOrTool), Item("pickaxe", 6, ItemCategory.MoldOrTool), Item("loot", 7, ItemCategory.SellOnly),
        };
        public List<MoldDef> Molds = new() { Mold() };
        public List<MoldSlotDef> Slots = new()
        {
            new MoldSlotDef { Id = "mold.blade", MoldId = "mold", NameId = "slot", Tags = new[] { "ingot" }, MinTier = 1, Count = 2 },
        };
        public List<RecipeDef> Recipes = new() { Recipe() };
        public List<RecipeInputDef> Inputs = new() { new RecipeInputDef { Id = "refine.ore", RecipeId = "refine", ItemId = "ore", Count = 2 } };
        public List<QualityCaps> Caps = new()
        {
            new QualityCaps { Id = "materials", Cap = 1 }, new QualityCaps { Id = "actions", Cap = 1 }, new QualityCaps { Id = "jobTree", Cap = 1 },
            new QualityCaps { Id = "minigame", Cap = 1 }, new QualityCaps { Id = "serverRoll", Cap = 1 }, new QualityCaps { Id = "smallTerm", Cap = 1 },
        };
        public List<QualityThresholds> Thresholds = new()
        {
            new QualityThresholds { Id = "a", MinQuality = 0, RarityId = "common", StatBonusPercent = 0 },
            new QualityThresholds { Id = "b", MinQuality = 100, RarityId = "rare", StatBonusPercent = 20 },
        };
        public List<ConversionOdds> Odds = new() { new ConversionOdds { Id = "t2", ToTier = 2, SuccessPercent = 70, DailyCap = 5 } };

        public ConfigSet Build()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(ItemDef), 2, Items.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(MaterialTierDef), 1, new object[]
            {
                new MaterialTierDef { Id = "t1", Tier = 1, NameId = "tier" }, new MaterialTierDef { Id = "t2", Tier = 2, NameId = "tier" },
            }));
            set.Add(new ConfigTable(typeof(ToolDef), 1, new object[]
            {
                new ToolDef { Id = "pickaxe", ItemId = "pickaxe", ToolType = ToolType.Pickaxe, Tier = 1 },
            }));
            set.Add(new ConfigTable(typeof(RarityDef), 2, new object[]
            {
                new RarityDef { Id = "common", Tier = 0, StatPercent = 100 }, new RarityDef { Id = "rare", Tier = 2, StatPercent = 125 },
            }));
            set.Add(new ConfigTable(typeof(ForgeActionDef), 1, new object[]
            {
                new ForgeActionDef { Id = "hit", NetId = 1, NameId = "a", QualityMin = 0, QualityMax = 10 },
            }));
            set.Add(new ConfigTable(typeof(MoldDef), 1, Molds.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(MoldSlotDef), 1, Slots.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(RecipeDef), 1, Recipes.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(RecipeInputDef), 1, Inputs.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(QualityCaps), 1, Caps.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(QualityThresholds), 1, Thresholds.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(ConversionOdds), 1, Odds.Cast<object>().ToList()));
            return set;
        }
    }

    /// <summary>The rule errors (references to tables these sets leave out, like UiStrings and JobDef, are not the point here).</summary>
    private static List<ValidationError> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Concat(ForgeCoverage.Check(set)).ToList();

    private static bool Has(List<ValidationError> errors, string table, string field) =>
        errors.Any(e => e.Table == table && e.Field == field);

    [Fact]
    public void The_minimal_set_is_valid() => Assert.Empty(Errors(new Rows().Build()));

    [Fact]
    public void Every_mold_item_has_a_mold_row()
    {
        var rows = new Rows();
        rows.Items.Add(Item("mold2", 8, ItemCategory.MoldOrTool));
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "Id"));
    }

    [Fact]
    public void A_tool_is_not_a_mold()
    {
        var rows = new Rows();
        rows.Molds.Add(Mold("pickaxe", 2));
        rows.Slots.Add(new MoldSlotDef { Id = "p.head", MoldId = "pickaxe", NameId = "slot", Tags = new[] { "ore" }, MinTier = 1, Count = 1 });
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "Id"));
    }

    [Fact]
    public void A_mold_makes_equipment_or_a_tool()
    {
        var rows = new Rows();
        rows.Molds[0].OutputItemId = "ingot";
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "OutputItemId"));
        rows.Molds[0].OutputItemId = "pickaxe"; // a tool: the user's tool molds
        Assert.False(Has(Errors(rows.Build()), "MoldDef", "OutputItemId"));
    }

    [Fact]
    public void A_craftable_mold_has_slots_and_a_mold_with_slots_an_output()
    {
        var rows = new Rows();
        rows.Slots.Clear();
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "OutputItemId"));
        rows = new Rows();
        rows.Molds[0].OutputItemId = string.Empty;
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "OutputItemId"));
        rows.Slots.Clear(); // known but not craftable yet (the Jeweler molds until WP-M3-9)
        Assert.Empty(Errors(rows.Build()));
    }

    [Fact]
    public void A_mold_lists_a_station_and_each_action_once()
    {
        var rows = new Rows();
        rows.Molds[0].Station = NpcInteraction.Sell;
        rows.Molds[0].ForgeActionIds = new[] { "hit", "hit" };
        var errors = Errors(rows.Build());
        Assert.True(Has(errors, "MoldDef", "Station"));
        Assert.True(Has(errors, "MoldDef", "ForgeActionIds"));
    }

    [Fact]
    public void Molds_and_recipes_share_one_net_id_namespace()
    {
        var rows = new Rows();
        rows.Recipes[0].NetId = 1;
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "NetId"));
    }

    [Fact]
    public void A_slot_names_a_tag_once_and_a_tier_on_the_ladder()
    {
        var rows = new Rows();
        rows.Slots[0].Tags = new[] { "ingot", "ingot" };
        rows.Slots[0].MinTier = 5;
        var errors = Errors(rows.Build());
        Assert.True(Has(errors, "MoldSlotDef", "Tags"));
        Assert.True(Has(errors, "MoldSlotDef", "MinTier"));
        rows.Slots[0].Tags = Array.Empty<string>();
        Assert.True(Has(Errors(rows.Build()), "MoldSlotDef", "Tags"));
    }

    [Fact]
    public void A_mold_has_at_most_eight_slots()
    {
        var rows = new Rows();
        for (int i = 0; i < ForgeStations.MaxInputs; i++)
            rows.Slots.Add(new MoldSlotDef { Id = "extra" + i, MoldId = "mold", NameId = "slot", Tags = new[] { "ore" }, MinTier = 1, Count = 1 });
        Assert.True(Has(Errors(rows.Build()), "MoldSlotDef", "MoldId"));
    }

    [Fact]
    public void A_slot_takes_tagged_materials_of_its_tier_or_above()
    {
        var slot = new MoldSlotDef { Id = "s", MoldId = "m", NameId = "n", Tags = new[] { "ore", "grip" }, MinTier = 2, Count = 1 };
        Assert.True(slot.Takes(Item("ore2", 1, ItemCategory.Material, 2, "ore")));
        Assert.False(slot.Takes(Item("ore1", 1, ItemCategory.Material, 1, "ore")));
        Assert.False(slot.Takes(Item("ingot", 1, ItemCategory.Material, 3, "ingot")));
    }

    [Fact]
    public void A_recipe_has_inputs_and_an_output_of_its_type()
    {
        var rows = new Rows();
        rows.Inputs.Clear();
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "Id"));
        rows = new Rows();
        rows.Recipes[0].OutputItemId = "sword";
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "OutputItemId"));
        rows = new Rows();
        rows.Recipes.Add(Recipe("moldRecipe", 102, RecipeType.Mold, "pickaxe"));
        rows.Inputs.Add(new RecipeInputDef { Id = "m.i", RecipeId = "moldRecipe", ItemId = "ingot", Count = 4 });
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "OutputItemId")); // a tool, not a mold
    }

    [Fact]
    public void A_conversion_goes_from_its_tier_to_the_next_with_odds()
    {
        var rows = new Rows();
        rows.Recipes.Add(Recipe("convert", 102, RecipeType.Convert, "ore2"));
        rows.Inputs.Add(new RecipeInputDef { Id = "c.i", RecipeId = "convert", ItemId = "ore", Count = 5 });
        Assert.Empty(Errors(rows.Build()));
        rows.Odds.Clear();
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "OutputItemId"));
        rows = new Rows();
        rows.Recipes.Add(Recipe("convert", 102, RecipeType.Convert, "ore", tier: 1)); // T1 to T1
        rows.Inputs.Add(new RecipeInputDef { Id = "c.i", RecipeId = "convert", ItemId = "ore", Count = 5 });
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "OutputItemId"));
        rows = new Rows();
        rows.Recipes.Add(Recipe("convert", 102, RecipeType.Convert, "ore2"));
        rows.Inputs.Add(new RecipeInputDef { Id = "c.i", RecipeId = "convert", ItemId = "ore2", Count = 5 }); // input of tier 2
        Assert.True(Has(Errors(rows.Build()), "RecipeDef", "Tier"));
    }

    [Fact]
    public void A_recipe_input_is_a_material_listed_once()
    {
        var rows = new Rows();
        rows.Inputs[0].ItemId = "loot";
        Assert.True(Has(Errors(rows.Build()), "RecipeInputDef", "ItemId"));
        rows = new Rows();
        rows.Inputs.Add(new RecipeInputDef { Id = "refine.ore2", RecipeId = "refine", ItemId = "ore", Count = 1 });
        Assert.True(Has(Errors(rows.Build()), "RecipeInputDef", "ItemId"));
    }

    [Fact]
    public void Every_quality_component_has_one_cap()
    {
        var rows = new Rows();
        rows.Caps.RemoveAt(5);
        Assert.True(Has(Errors(rows.Build()), "QualityCaps", "Id"));
        rows = new Rows();
        rows.Caps[0].Id = "luck";
        Assert.True(Has(Errors(rows.Build()), "QualityCaps", "Id"));
    }

    [Fact]
    public void Thresholds_ascend_from_zero_and_cover_every_rarity()
    {
        var rows = new Rows();
        rows.Thresholds[0].MinQuality = 10;
        Assert.True(Has(Errors(rows.Build()), "QualityThresholds", "MinQuality"));
        rows = new Rows();
        rows.Thresholds[1].MinQuality = 0;
        Assert.True(Has(Errors(rows.Build()), "QualityThresholds", "MinQuality"));
        rows = new Rows();
        rows.Thresholds.Reverse();
        rows.Thresholds[0].MinQuality = 0;
        rows.Thresholds[1].MinQuality = 100;
        Assert.True(Has(Errors(rows.Build()), "QualityThresholds", "RarityId")); // rare before common
        rows = new Rows();
        rows.Thresholds.RemoveAt(1);
        Assert.True(Has(Errors(rows.Build()), "QualityThresholds", "RarityId")); // no band for rare
    }

    [Fact]
    public void Conversion_odds_are_one_per_tier_and_fall_as_the_tier_rises()
    {
        var rows = new Rows();
        rows.Odds.Add(new ConversionOdds { Id = "t2b", ToTier = 2, SuccessPercent = 50, DailyCap = 5 });
        Assert.True(Has(Errors(rows.Build()), "ConversionOdds", "ToTier"));
        rows = new Rows();
        rows.Odds.Add(new ConversionOdds { Id = "t3", ToTier = 3, SuccessPercent = 50, DailyCap = 5 }); // no tier 3 on this ladder
        Assert.True(Has(Errors(rows.Build()), "ConversionOdds", "ToTier"));
    }

    [Fact]
    public void The_committed_forge_rows_cover_every_mold_and_grip()
    {
        var set = Simulation.Crafting.ForgeMathTests.LoadRepository();
        Assert.Empty(ForgeCoverage.Check(set));
        var items = set.TryGet(typeof(ItemDef), out var table) ? table.Rows.Cast<ItemDef>().ToDictionary(i => i.Id) : new();
        foreach (var grip in new[] { ("m1.cyclopsHide", 1), ("m2.wolfSinew", 2), ("m2.bruteHide", 3) })
        {
            Assert.Contains("grip", items[grip.Item1].MaterialTags); // decision 6
            Assert.Equal(grip.Item2, items[grip.Item1].Tier);
        }
        var molds = set.TryGet(typeof(MoldDef), out var moldTable) ? moldTable.Rows.Cast<MoldDef>().ToList() : new();
        Assert.Contains(molds, m => m.Id == "m3.moldPickaxeT2" && m.OutputItemId == "m3.pickaxeT2"); // the user's tool molds
        Assert.Contains(molds, m => m.Id == "m3.moldPickaxeT3" && m.OutputItemId == "m3.pickaxeT3");
    }
}
