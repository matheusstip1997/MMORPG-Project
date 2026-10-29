using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// The economy tables of WP-M2-4 (spec 4.2, 8.5): <see cref="NpcDef"/>, <see cref="NpcVendorStock"/>,
/// <see cref="PriceBase"/>, <see cref="RarityMult"/>, <see cref="DemandCurve"/>, the singletons, and
/// <see cref="EconomyCoverage"/>; the committed data passes.
/// </summary>
public class EconomyTablesTests
{
    private static ItemDef Item(string id, int netId, ItemCategory category) => new()
    {
        Id = id, NetId = netId, Category = category, MaxStack = 20, VisualId = "drop", NameId = "item.name",
    };

    private static NpcDef Npc(string id, int netId, NpcKind kind, params NpcInteraction[] interactions) => new()
    {
        Id = id, NetId = netId, Kind = kind, Interactions = interactions, VisualId = "npc", NameId = "npc.name", InteractRangeCm = 300,
    };

    private static ConfigSet Set(IEnumerable<NpcDef>? npcs = null, IEnumerable<NpcVendorStock>? stock = null,
        IEnumerable<PriceBase>? prices = null, IEnumerable<RarityMult>? mults = null, IEnumerable<DemandCurve>? curves = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ItemDef), 2, new object[]
        {
            Item("horn", 1, ItemCategory.SellOnly), Item("ash", 2, ItemCategory.Material), Item("potion", 3, ItemCategory.Consumable),
        }));
        set.Add(new ConfigTable(typeof(RarityDef), 2, new object[] { new RarityDef { Id = "common", Tier = 0, StatPercent = 100 } }));
        set.Add(new ConfigTable(typeof(ContainerDef), 2, new object[]
        {
            new ContainerDef
            {
                Id = ContainerDef.NormalBag, Code = 1, Slots = 30,
                Accepts = new[] { ItemCategory.Material, ItemCategory.SellOnly, ItemCategory.Consumable },
                Kind = ContainerKind.Bag, ReceivesNewItems = true,
            },
        }));
        set.Add(new ConfigTable(typeof(NpcDef), 1, (npcs ?? new[]
        {
            Npc("outpost", 1, NpcKind.Outpost, NpcInteraction.Sell), Npc("vendor", 2, NpcKind.Vendor, NpcInteraction.Buy),
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(NpcVendorStock), 1, (stock ?? new[]
        {
            new NpcVendorStock { Id = "vendor.potion", NpcId = "vendor", ItemId = "potion", PriceGold = 25 },
        }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(PriceBase), 1,
            (prices ?? new[] { new PriceBase { ItemId = "horn", BaseGold = 10 } }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(RarityMult), 1,
            (mults ?? new[] { new RarityMult { RarityId = "common", PricePercent = 100 } }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(DemandCurve), 1, (curves ?? new[]
        {
            Curve(DemandCurve.SpawnDelay), Curve(DemandCurve.Reward), Curve(DemandCurve.Price),
        }).Cast<object>().ToList()));
        return set;
    }

    private static DemandCurve Curve(string id, int[]? ratios = null, int[]? percents = null) =>
        new() { Id = id, RatioPermille = ratios ?? new[] { 0, 1000 }, Percent = percents ?? new[] { 100, 100 } };

    private static List<string> Errors(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Code != ValidationCodes.BrokenRef).Select(e => e.Table + ": " + e.Message)
            .Concat(EconomyCoverage.Check(set).Select(e => e.Table + ": " + e.Message)).ToList();

    [Fact]
    public void The_committed_economy_tables_pass()
    {
        var result = RepositoryTables();
        Assert.Empty(EconomyCoverage.Check(result));
        Assert.Equal(new[] { "m1.outpost", "m1.vendor" }, NetIdsOf<NpcDef>(result).Select(n => n.Id));
    }

    [Fact]
    public void A_consistent_set_passes()
    {
        Assert.Empty(Errors(Set()));
    }

    [Fact]
    public void An_npc_offers_only_what_its_kind_allows()
    {
        var errors = Errors(Set(npcs: new[]
        {
            Npc("outpost", 1, NpcKind.Outpost, NpcInteraction.Buy), Npc("vendor", 2, NpcKind.Vendor, NpcInteraction.Buy, NpcInteraction.Buy),
            Npc("empty", 3, NpcKind.Vendor),
        }));

        Assert.Contains(errors, e => e.Contains("Outpost does not offer Buy"));
        Assert.Contains(errors, e => e.Contains("lists Buy twice"));
        Assert.Contains(errors, e => e.Contains("at least one interaction"));
    }

    [Fact]
    public void Npc_net_ids_are_unique_and_a_vendor_needs_stock()
    {
        var errors = Errors(Set(
            npcs: new[] { Npc("outpost", 1, NpcKind.Outpost, NpcInteraction.Sell), Npc("vendor", 1, NpcKind.Vendor, NpcInteraction.Buy) },
            stock: Array.Empty<NpcVendorStock>()));

        Assert.Contains(errors, e => e.Contains("NetId") || e.Contains("also used"));
        Assert.Contains(errors, e => e.Contains("no NpcVendorStock row"));
    }

    [Fact]
    public void Vendors_sell_consumables_once_per_item_and_only_if_they_offer_buy()
    {
        var errors = Errors(Set(stock: new[]
        {
            new NpcVendorStock { Id = "a", NpcId = "vendor", ItemId = "potion", PriceGold = 25 },
            new NpcVendorStock { Id = "b", NpcId = "vendor", ItemId = "potion", PriceGold = 30 },
            new NpcVendorStock { Id = "c", NpcId = "vendor", ItemId = "ash", PriceGold = 5 },
            new NpcVendorStock { Id = "d", NpcId = "outpost", ItemId = "potion", PriceGold = 5 },
        }));

        Assert.Contains(errors, e => e.Contains("also sold"));
        Assert.Contains(errors, e => e.Contains("consumables"));
        Assert.Contains(errors, e => e.Contains("does not offer buy"));
    }

    [Fact]
    public void Every_sell_only_item_and_only_those_have_a_base_price()
    {
        var missing = Errors(Set(prices: Array.Empty<PriceBase>()));
        var wrong = Errors(Set(prices: new[] { new PriceBase { ItemId = "horn", BaseGold = 10 }, new PriceBase { ItemId = "ash", BaseGold = 3 } }));

        Assert.Contains(missing, e => e.Contains("sell-only item 'horn'"));
        Assert.Contains(wrong, e => e.Contains("only sell-only items"));
    }

    [Fact]
    public void Every_rarity_has_a_price_multiplier_and_all_three_curves_exist()
    {
        var errors = Errors(Set(mults: Array.Empty<RarityMult>(), curves: new[] { Curve(DemandCurve.Price) }));

        Assert.Contains(errors, e => e.Contains("rarity 'common'"));
        Assert.Contains(errors, e => e.Contains("'spawnDelay' curve"));
        Assert.Contains(errors, e => e.Contains("'reward' curve"));
    }

    [Theory]
    [InlineData(new[] { 0 }, new[] { 100 }, "at least two points")]
    [InlineData(new[] { 0, 1000 }, new[] { 100 }, "one value per ratio point")]
    [InlineData(new[] { 10, 1000 }, new[] { 100, 100 }, "must start at 0")]
    [InlineData(new[] { 0, 1000, 1000 }, new[] { 100, 90, 80 }, "strictly ascending")]
    [InlineData(new[] { 0, 1000 }, new[] { 100, 0 }, "from 1 to 100000")]
    public void A_curve_needs_ascending_points_from_zero_and_a_percent_each(int[] ratios, int[] percents, string message)
    {
        var errors = Errors(Set(curves: new[] { Curve(DemandCurve.SpawnDelay), Curve(DemandCurve.Reward), Curve(DemandCurve.Price, ratios, percents) }));

        Assert.Contains(errors, e => e.Contains(message));
    }

    [Fact]
    public void The_clamps_must_include_the_neutral_start_and_the_floor_stay_below_the_maximum()
    {
        var demand = new RowErrors();
        new DemandSettings { SpawnDelayMinPercent = 110, SpawnDelayMaxPercent = 200, RewardMinPercent = 50, RewardMaxPercent = 90 }.Validate(demand);
        var prices = new RowErrors();
        new PriceFloorRecovery { FloorPercent = 90, MaxPercent = 80 }.Validate(prices);

        Assert.Equal(2, demand.Items.Count);
        Assert.Single(prices.Items);
    }

    private static ConfigSet RepositoryTables()
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

    private static List<T> NetIdsOf<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : new List<T>();
}
