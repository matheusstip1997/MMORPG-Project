using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The enchant tables (spec 5.3 M3 decision 10, WP-M3-8): <see cref="EnchantOdds"/>, <see cref="EnchantBonus"/>,
/// <see cref="EnchantMaterial"/> and <see cref="EnchantCoverage"/>: one test per validator rule.
/// </summary>
public class EnchantTablesTests
{
    private static ItemDef Item(string id, int netId, ItemCategory category, int tier = 0, string job = "", params string[] tags) => new()
    {
        Id = id, NetId = netId, Category = category, MaxStack = ItemCategories.IsInstance(category) ? 1 : 99, VisualId = "drop",
        NameId = "item.name", Tier = tier, MaterialTags = tags, JobId = job,
        EquipSlots = category == ItemCategory.Equipment ? new[] { "weaponMain" } : Array.Empty<string>(),
    };

    private sealed class Rows
    {
        public List<ItemDef> Items = new()
        {
            Item("stone", 1, ItemCategory.Material, 1, "", "enchantStone"), Item("ore", 2, ItemCategory.Material, 1, "", "ore"),
            Item("sword", 3, ItemCategory.Equipment), Item("ring", 4, ItemCategory.Equipment, 0, "jeweler"), Item("mold", 5, ItemCategory.MoldOrTool),
        };
        public List<MoldDef> Molds = new()
        {
            new MoldDef { Id = "mold", NetId = 1, JobId = "smith", Station = NpcInteraction.Forge, MinJobLevel = 1, Tier = 1, OutputItemId = "sword" },
            new MoldDef { Id = "ringMold", NetId = 2, JobId = "jeweler", Station = NpcInteraction.JewelerBench, MinJobLevel = 1, Tier = 1 },
        };
        public List<EnchantOdds> Odds = Enumerable.Range(0, EnchantLimits.MaxLevel)
            .Select(l => new EnchantOdds { Id = "l" + l, Level = l, PlusPercent = 50, SamePercent = l == 0 ? 50 : 30, MinusPercent = l == 0 ? 0 : 20 })
            .ToList();
        public List<EnchantBonus> Bonus = Enumerable.Range(1, EnchantLimits.MaxLevel)
            .Select(l => new EnchantBonus { Id = "l" + l, Level = l, StatPercent = l * 10 }).ToList();
        public List<EnchantMaterial> Materials = new() { new EnchantMaterial { Id = "t1", Tier = 1, ItemId = "stone", Count = 1 } };

        public ConfigSet Build()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(ItemDef), 2, Items.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(MaterialTierDef), 1, new object[] { new MaterialTierDef { Id = "t1", Tier = 1, NameId = "tier" } }));
            set.Add(new ConfigTable(typeof(MoldDef), 1, Molds.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(EnchantOdds), 1, Odds.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(EnchantBonus), 1, Bonus.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(EnchantMaterial), 1, Materials.Cast<object>().ToList()));
            return set;
        }
    }

    /// <summary>The enchant rule errors (references to tables these sets leave out are not the point here).</summary>
    private static List<ValidationError> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef)
            .Where(e => e.Table is "EnchantOdds" or "EnchantBonus" or "EnchantMaterial" || (e.Table == "ItemDef" && e.Field == nameof(ItemDef.JobId)))
            .Concat(EnchantCoverage.Check(set)).ToList();

    private static bool Has(List<ValidationError> errors, string table, string field) =>
        errors.Any(e => e.Table == table && e.Field == field);

    [Fact]
    public void The_minimal_set_is_valid() => Assert.Empty(Errors(new Rows().Build()));

    [Fact]
    public void The_odds_of_a_level_sum_to_100()
    {
        var rows = new Rows();
        rows.Odds[3].PlusPercent = 60;
        Assert.True(Has(Errors(rows.Build()), "EnchantOdds", "PlusPercent"));
    }

    [Fact]
    public void Level_zero_cannot_go_down()
    {
        var rows = new Rows();
        rows.Odds[0].SamePercent = 40;
        rows.Odds[0].MinusPercent = 10;
        Assert.True(Has(Errors(rows.Build()), "EnchantOdds", "MinusPercent"));
    }

    [Fact]
    public void Every_level_can_rise()
    {
        var rows = new Rows();
        rows.Odds[4].PlusPercent = 0;
        rows.Odds[4].SamePercent = 80;
        Assert.True(Has(Errors(rows.Build()), "EnchantOdds", "PlusPercent"));
    }

    [Fact]
    public void The_odds_ladder_is_complete_and_unique()
    {
        var rows = new Rows();
        rows.Odds.RemoveAt(5);
        Assert.True(Has(Errors(rows.Build()), "EnchantOdds", "Level"));
        rows = new Rows();
        rows.Odds[5].Level = 4;
        Assert.True(Has(Errors(rows.Build()), "EnchantOdds", "Level"));
    }

    [Fact]
    public void The_bonus_ladder_is_complete_and_never_falls()
    {
        var rows = new Rows();
        rows.Bonus.RemoveAt(8);
        Assert.True(Has(Errors(rows.Build()), "EnchantBonus", "Level"));
        rows = new Rows();
        rows.Bonus[5].StatPercent = 5;
        Assert.True(Has(Errors(rows.Build()), "EnchantBonus", "StatPercent"));
    }

    [Fact]
    public void Every_tier_has_a_material_and_it_is_a_stone()
    {
        var rows = new Rows();
        rows.Materials.Clear();
        Assert.True(Has(Errors(rows.Build()), "EnchantMaterial", "Tier"));
        rows = new Rows();
        rows.Materials[0].ItemId = "ore";
        Assert.True(Has(Errors(rows.Build()), "EnchantMaterial", "ItemId"));
    }

    [Fact]
    public void Every_equipment_item_has_a_job()
    {
        var rows = new Rows();
        rows.Items.Add(Item("boots", 6, ItemCategory.Equipment));
        Assert.True(Has(Errors(rows.Build()), "ItemDef", "JobId"));
    }

    [Fact]
    public void An_item_job_agrees_with_its_mold()
    {
        var rows = new Rows();
        rows.Items[2].JobId = "jeweler"; // the sword's mold is the smith's
        Assert.True(Has(Errors(rows.Build()), "ItemDef", "JobId"));
    }

    [Fact]
    public void A_job_enchants_at_one_station()
    {
        var rows = new Rows();
        rows.Molds.Add(new MoldDef { Id = "mold2", NetId = 3, JobId = "smith", Station = NpcInteraction.JewelerBench, MinJobLevel = 1, Tier = 1 });
        rows.Items.Add(Item("mold2", 7, ItemCategory.MoldOrTool));
        Assert.True(Has(Errors(rows.Build()), "MoldDef", "Station"));
    }
}
