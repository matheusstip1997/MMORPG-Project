using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// Item model v3 (spec 4.1 M3 item model, WP-M3-2): the <see cref="ItemDef"/> fields tier, materialTags, gemSlots and jobId,
/// and the tables <see cref="MaterialTierDef"/>, <see cref="MaterialTagDef"/>, <see cref="MaterialStatDef"/> and
/// <see cref="ToolDef"/>: one test per validator rule, and the committed rows.
/// </summary>
public class MaterialTablesTests
{
    private static ItemDef Item(string id, int netId, ItemCategory category, int tier = 0, params string[] tags) => new()
    {
        Id = id, NetId = netId, Category = category, MaxStack = ItemCategories.IsInstance(category) ? 1 : 99, VisualId = "drop",
        NameId = "item.name", Tier = tier, MaterialTags = tags,
    };

    private static ItemDef Ore(int tier = 1) => Item("ore", 1, ItemCategory.Material, tier, "ore");

    private static ItemDef Pickaxe() => Item("pickaxe", 2, ItemCategory.MoldOrTool);

    private static ConfigSet Set(IEnumerable<ItemDef>? items = null, IEnumerable<MaterialTierDef>? tiers = null,
        IEnumerable<MaterialStatDef>? stats = null, IEnumerable<ToolDef>? tools = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ItemDef), 2, (items ?? new[] { Ore(), Pickaxe() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MaterialTierDef), 1, (tiers ?? new[]
        {
            new MaterialTierDef { Id = "t1", Tier = 1, NameId = "tier" }, new MaterialTierDef { Id = "t2", Tier = 2, NameId = "tier" },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MaterialTagDef), 1, new object[] { new MaterialTagDef { Id = "ore", NameId = "tag" } }));
        set.Add(new ConfigTable(typeof(StatDef), 1, new object[] { new StatDef { Id = "def", Min = 0, Max = 1000 } }));
        set.Add(new ConfigTable(typeof(MaterialStatDef), 1, (stats ?? new[]
        {
            new MaterialStatDef { Id = "ore.def", ItemId = "ore", StatId = "def", Flat = 2 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ToolDef), 1, (tools ?? new[]
        {
            new ToolDef { Id = "pickaxe", ItemId = "pickaxe", ToolType = ToolType.Pickaxe, Tier = 1 },
        }).Cast<object>().ToList()));
        return set;
    }

    /// <summary>The rule errors (references to tables these sets leave out, like UiStrings, are not the point here).</summary>
    private static List<ValidationError> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).ToList();

    private static bool Has(List<ValidationError> errors, string table, string field) =>
        errors.Any(e => e.Table == table && e.Field == field);

    [Fact]
    public void A_consistent_set_passes()
    {
        Assert.Empty(Errors(Set()));
    }

    [Fact]
    public void A_material_with_a_tier_needs_a_tag_and_a_tagged_material_a_tier()
    {
        Assert.True(Has(Errors(Set(items: [Item("ore", 1, ItemCategory.Material, 1), Pickaxe()])), "ItemDef", nameof(ItemDef.MaterialTags)));
        Assert.True(Has(Errors(Set(items: [Item("ore", 1, ItemCategory.Material, 0, "ore"), Pickaxe()])), "ItemDef", nameof(ItemDef.Tier)));
        // A material that is no craft input yet (the monster drops until decision 6) has neither.
        Assert.Empty(Errors(Set(items: [Ore(), Pickaxe(), Item("ash", 3, ItemCategory.Material)])));
    }

    [Fact]
    public void A_tag_is_listed_once()
    {
        Assert.True(Has(Errors(Set(items: [Item("ore", 1, ItemCategory.Material, 1, "ore", "ore"), Pickaxe()])), "ItemDef",
            nameof(ItemDef.MaterialTags)));
    }

    [Fact]
    public void Only_materials_have_a_tier_or_tags()
    {
        var tiered = Pickaxe();
        tiered.Tier = 1;
        Assert.True(Has(Errors(Set(items: [Ore(), tiered])), "ItemDef", nameof(ItemDef.Tier)));
        var tagged = Pickaxe();
        tagged.MaterialTags = ["ore"];
        Assert.True(Has(Errors(Set(items: [Ore(), tagged])), "ItemDef", nameof(ItemDef.MaterialTags)));
    }

    [Fact]
    public void A_material_tier_is_on_the_ladder()
    {
        Assert.True(Has(Errors(Set(items: [Ore(tier: 3), Pickaxe()])), "ItemDef", nameof(ItemDef.Tier)));
    }

    [Fact]
    public void Gem_slots_and_the_enchanting_job_are_for_equipment_only()
    {
        var gems = Pickaxe();
        gems.GemSlots = 1;
        Assert.True(Has(Errors(Set(items: [Ore(), gems])), "ItemDef", nameof(ItemDef.GemSlots)));
        var job = Pickaxe();
        job.JobId = "blacksmith";
        Assert.True(Has(Errors(Set(items: [Ore(), job])), "ItemDef", nameof(ItemDef.JobId)));
    }

    [Fact]
    public void Material_tiers_are_unique_and_contiguous_from_1()
    {
        Assert.True(Has(Errors(Set(tiers: [new MaterialTierDef { Id = "t1", Tier = 1, NameId = "tier" }, new MaterialTierDef { Id = "t3", Tier = 3, NameId = "tier" }])),
            "MaterialTierDef", nameof(MaterialTierDef.Tier)));
        Assert.True(Has(Errors(Set(tiers: [new MaterialTierDef { Id = "a", Tier = 1, NameId = "tier" }, new MaterialTierDef { Id = "b", Tier = 1, NameId = "tier" }])),
            "MaterialTierDef", nameof(MaterialTierDef.Tier)));
    }

    [Fact]
    public void A_material_stat_belongs_to_a_craft_material_once_per_stat_and_is_not_zero()
    {
        Assert.True(Has(Errors(Set(stats: [new MaterialStatDef { Id = "p", ItemId = "pickaxe", StatId = "def", Flat = 1 }])),
            "MaterialStatDef", nameof(MaterialStatDef.ItemId)));
        Assert.True(Has(Errors(Set(items: [Item("ore", 1, ItemCategory.Material), Pickaxe()], stats: [new MaterialStatDef { Id = "a", ItemId = "ore", StatId = "def", Flat = 1 }])),
            "MaterialStatDef", nameof(MaterialStatDef.ItemId))); // tier 0: no craft input
        Assert.True(Has(Errors(Set(stats:
            [
                new MaterialStatDef { Id = "a", ItemId = "ore", StatId = "def", Flat = 1 },
                new MaterialStatDef { Id = "b", ItemId = "ore", StatId = "def", Percent = 1 },
            ])), "MaterialStatDef", nameof(MaterialStatDef.StatId)));
        Assert.True(Has(Errors(Set(stats: [new MaterialStatDef { Id = "a", ItemId = "ore", StatId = "def" }])),
            "MaterialStatDef", nameof(MaterialStatDef.Flat)));
    }

    [Fact]
    public void A_tool_is_one_mold_or_tool_item_of_a_type_and_a_tier_on_the_ladder()
    {
        Assert.True(Has(Errors(Set(tools: [new ToolDef { Id = "t", ItemId = "ore", ToolType = ToolType.Pickaxe, Tier = 1 }])),
            "ToolDef", nameof(ToolDef.ItemId)));
        Assert.True(Has(Errors(Set(tools: [new ToolDef { Id = "t", ItemId = "pickaxe", ToolType = ToolType.Pickaxe, Tier = 5 }])),
            "ToolDef", nameof(ToolDef.Tier)));
        Assert.True(Has(Errors(Set(tools: [new ToolDef { Id = "t", ItemId = "pickaxe", ToolType = ToolType.None, Tier = 1 }])),
            "ToolDef", nameof(ToolDef.ToolType)));
        Assert.True(Has(Errors(Set(tools:
            [
                new ToolDef { Id = "a", ItemId = "pickaxe", ToolType = ToolType.Pickaxe, Tier = 1 },
                new ToolDef { Id = "b", ItemId = "pickaxe", ToolType = ToolType.Pickaxe, Tier = 2 },
            ])), "ToolDef", nameof(ToolDef.ItemId)));
    }

    [Fact]
    public void The_committed_rows_have_the_published_m3_items()
    {
        // The ids relayed to WP-M3-1 (starter molds) and wave 1 (materials, the pickaxe); spec 4.1 M3 implementation.
        var set = LoadRepository();
        foreach (var (id, netId, category) in new[]
                 {
                     ("m3.oreT1", 401, ItemCategory.Material), ("m3.oreT2", 402, ItemCategory.Material),
                     ("m3.oreT3", 403, ItemCategory.Material), ("m3.ingotT1", 404, ItemCategory.Material),
                     ("m3.roughGemT1", 405, ItemCategory.Material), ("m3.enchantStone", 406, ItemCategory.Material),
                     ("m3.pickaxeT1", 501, ItemCategory.MoldOrTool), ("m3.moldSword", 601, ItemCategory.MoldOrTool),
                     ("m3.moldChest", 602, ItemCategory.MoldOrTool), ("m3.moldRing", 603, ItemCategory.MoldOrTool),
                     ("m3.moldAmulet", 604, ItemCategory.MoldOrTool),
                 })
        {
            var item = ItemDef.Find(set, id);
            Assert.NotNull(item);
            Assert.Equal((netId, category), (item!.NetId, item.Category));
        }
        Assert.Equal((ToolType.Pickaxe, 1), (ToolDef.ForItem(set, "m3.pickaxeT1")!.ToolType, ToolDef.ForItem(set, "m3.pickaxeT1")!.Tier));
        Assert.Null(ToolDef.ForItem(set, "m3.moldSword")); // a mold
        Assert.Equal(new[] { 1, 2, 3 }, new[] { "m3.oreT1", "m3.oreT2", "m3.oreT3" }.Select(id => ItemDef.Find(set, id)!.Tier));
    }

    private static ConfigSet LoadRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var tables = Path.Combine(directory!.FullName, "data", "tables");
        var errors = new List<ValidationError>();
        var set = ConfigTableReader.LoadAndValidate(ConfigTables.All, name =>
        {
            var path = Path.Combine(tables, name + ".json");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }, errors);
        Assert.Empty(errors);
        return set;
    }
}
