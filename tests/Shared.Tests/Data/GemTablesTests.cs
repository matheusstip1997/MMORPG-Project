using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The gem tables (spec 5.3 M3 implementation WP-M3-9): <see cref="GemDef"/>, <see cref="GemSocketOdds"/> and
/// <see cref="GemCoverage"/>: one test per validator rule, and the committed rows.
/// </summary>
public class GemTablesTests
{
    private static ItemDef Material(string id, int netId, int tier, params string[] tags) => new()
    {
        Id = id, NetId = netId, Category = ItemCategory.Material, MaxStack = 99, VisualId = "drop", NameId = "item.name", Tier = tier,
        MaterialTags = tags,
    };

    private static GemDef Gem(string id, GemKind kind, int tier) =>
        new() { Id = id, Kind = kind, Tier = tier, StatId = "atk", Flat = 4 };

    private static GemSocketOdds Odds(string id, int tier) => new()
    {
        Id = id, Tier = tier, JobId = "jeweler", SuccessPercent = 80, MinJobLevel = 1, RemovalCostItemId = "rough", RemovalCostCount = 2,
    };

    private sealed class Rows
    {
        public List<ItemDef> Items = new()
        {
            Material("rough", 1, 1, "roughGem"), Material("ruby", 2, 1, "gem"), Material("ruby2", 3, 2, "gem"),
            Material("ore", 4, 1, "ore"),
        };
        public List<GemDef> Gems = new() { Gem("ruby", GemKind.Ruby, 1), Gem("ruby2", GemKind.Ruby, 2) };
        public List<GemSocketOdds> Odds = new() { GemTablesTests.Odds("t1", 1), GemTablesTests.Odds("t2", 2) };

        public ConfigSet Build()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(ItemDef), 2, Items.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(MaterialTierDef), 1, new object[]
            {
                new MaterialTierDef { Id = "t1", Tier = 1, NameId = "tier" }, new MaterialTierDef { Id = "t2", Tier = 2, NameId = "tier" },
            }));
            set.Add(new ConfigTable(typeof(GemDef), 1, Gems.Cast<object>().ToList()));
            set.Add(new ConfigTable(typeof(GemSocketOdds), 1, Odds.Cast<object>().ToList()));
            return set;
        }
    }

    private static List<ValidationError> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Concat(GemCoverage.Check(set)).ToList();

    private static bool Has(List<ValidationError> errors, string table, string field) =>
        errors.Any(e => e.Table == table && e.Field == field);

    [Fact]
    public void The_minimal_set_is_valid() => Assert.Empty(Errors(new Rows().Build()));

    [Fact]
    public void Every_cut_gem_has_a_gem_row()
    {
        var rows = new Rows();
        rows.Items.Add(Material("sapphire", 5, 1, "gem"));
        Assert.True(Has(Errors(rows.Build()), "GemDef", "Id"));
    }

    [Fact]
    public void A_gem_row_names_a_material_tagged_gem()
    {
        var rows = new Rows();
        rows.Gems.Add(Gem("ore", GemKind.Emerald, 1));
        Assert.True(Has(Errors(rows.Build()), "GemDef", "Id"));
    }

    [Fact]
    public void A_gem_row_has_its_item_tier()
    {
        var rows = new Rows();
        rows.Gems[1].Tier = 1;
        Assert.True(Has(Errors(rows.Build()), "GemDef", "Tier"));
    }

    [Fact]
    public void One_gem_per_kind_and_tier()
    {
        var rows = new Rows();
        rows.Items.Add(Material("ruby1b", 5, 1, "gem"));
        rows.Gems.Add(Gem("ruby1b", GemKind.Ruby, 1));
        Assert.True(Has(Errors(rows.Build()), "GemDef", "Kind"));
    }

    [Fact]
    public void A_gem_has_a_kind_and_a_stat_value()
    {
        var rows = new Rows();
        rows.Gems[0].Kind = GemKind.None;
        rows.Gems[1].Flat = 0;
        var errors = Errors(rows.Build());
        Assert.True(Has(errors, "GemDef", "Kind"));
        Assert.True(Has(errors, "GemDef", "Flat"));
    }

    [Fact]
    public void A_gem_tier_needs_its_odds_row()
    {
        var rows = new Rows();
        rows.Odds.RemoveAt(1);
        Assert.True(Has(Errors(rows.Build()), "GemDef", "Tier"));
    }

    [Fact]
    public void Odds_tiers_are_unique_and_contiguous_from_one()
    {
        var rows = new Rows();
        rows.Odds[1].Tier = 1;
        Assert.True(Has(Errors(rows.Build()), "GemSocketOdds", "Tier"));
        rows = new Rows();
        rows.Odds.RemoveAt(0);
        Assert.True(Has(Errors(rows.Build()), "GemSocketOdds", "Tier"));
    }

    [Fact]
    public void The_removal_cost_is_a_material_within_one_stack()
    {
        var rows = new Rows();
        rows.Items.Add(new ItemDef
        {
            Id = "coin", NetId = 9, Category = ItemCategory.SellOnly, MaxStack = 99, VisualId = "drop", NameId = "item.name",
        });
        rows.Odds[0].RemovalCostItemId = "coin";
        rows.Odds[1].RemovalCostCount = 99;
        rows.Items[0].MaxStack = 10;
        var errors = Errors(rows.Build());
        Assert.True(Has(errors, "GemSocketOdds", "RemovalCostItemId"));
        Assert.True(Has(errors, "GemSocketOdds", "RemovalCostCount"));
    }

    [Fact]
    public void The_committed_rows_cover_three_kinds_by_three_tiers_and_never_star_gems()
    {
        var set = Shared.Tests.Simulation.Crafting.ForgeMathTests.LoadRepository();
        Assert.Empty(GemCoverage.Check(set));
        var gems = NetIds.Rows<GemDef>(set).ToList();
        Assert.Equal(9, gems.Count);
        Assert.Equal(3, gems.Select(g => g.Kind).Distinct().Count());
        Assert.All(gems, g => Assert.InRange(g.Tier, 1, 3));
        var odds = NetIds.Rows<GemSocketOdds>(set).OrderBy(o => o.Tier).ToList();
        Assert.Equal(new[] { 1, 2, 3 }, odds.Select(o => o.Tier));
        Assert.All(odds, o => Assert.Equal("jeweler", o.JobId));
    }
}
