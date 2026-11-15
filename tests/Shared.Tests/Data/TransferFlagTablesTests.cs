using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="TransferFlagSettings"/> and <see cref="ItemValueBand"/> (spec 4.5 R-4.5-02, 8.5; M4 decision 17; WP-M4-15): the
/// thresholds and the scan's pace in range, a look-back of at least a day and one interval; a value band per category and tier
/// once, never for sell-only items (they use PriceBase), and a tier-0 band for every tradable category; the committed rows load.
/// </summary>
public class TransferFlagTablesTests
{
    private static TransferFlagSettings Settings() => new()
    {
        GoldPerPairDay = 5_000, ValuePerPairDay = 10_000, ScanIntervalMs = 30_000, ScanBatchMax = 500, LookbackMs = 172_800_000,
    };

    private static List<ItemValueBand> Bands() =>
    [
        new() { Id = "material.t0", Category = ItemCategory.Material, Tier = 0, GoldPerUnit = 5 },
        new() { Id = "equipment.t0", Category = ItemCategory.Equipment, Tier = 0, GoldPerUnit = 60 },
        new() { Id = "consumable.t0", Category = ItemCategory.Consumable, Tier = 0, GoldPerUnit = 10 },
        new() { Id = "moldOrTool.t0", Category = ItemCategory.MoldOrTool, Tier = 0, GoldPerUnit = 40 },
    ];

    private static List<string> Errors(TransferFlagSettings settings, IEnumerable<ItemValueBand> bands)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(TransferFlagSettings), 1, [settings]));
        set.Add(new ConfigTable(typeof(ItemValueBand), 1, bands.Cast<object>().ToList()));
        return TableValidator.Validate(set).Select(e => e.Table + "." + e.Field + ": " + e.Message)
            .Concat(EconomyCoverage.Check(set).Select(e => e.Table + "." + e.Field + ": " + e.Message)).ToList();
    }

    [Fact]
    public void The_placeholder_rows_pass()
    {
        Assert.Empty(Errors(Settings(), Bands()));
    }

    [Fact]
    public void A_look_back_shorter_than_a_day_and_an_interval_is_an_error()
    {
        var settings = Settings();
        settings.LookbackMs = TransferFlagSettings.DayMs; // a day, but not a day plus the 30 s interval
        Assert.Contains(Errors(settings, Bands()), e => e.StartsWith("TransferFlagSettings.LookbackMs", StringComparison.Ordinal));
    }

    [Fact]
    public void Thresholds_below_one_and_a_scan_batch_of_zero_are_errors()
    {
        var settings = Settings();
        settings.GoldPerPairDay = 0;
        settings.ValuePerPairDay = 0;
        settings.ScanBatchMax = 0;
        var errors = Errors(settings, Bands());
        Assert.Contains(errors, e => e.StartsWith("TransferFlagSettings.GoldPerPairDay", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("TransferFlagSettings.ValuePerPairDay", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("TransferFlagSettings.ScanBatchMax", StringComparison.Ordinal));
    }

    [Fact]
    public void A_sell_only_band_a_category_and_tier_twice_and_a_missing_tier_0_band_are_errors()
    {
        var bands = Bands().Where(b => b.Category != ItemCategory.Consumable).ToList();
        bands.Add(new ItemValueBand { Id = "sellOnly.t0", Category = ItemCategory.SellOnly, Tier = 0, GoldPerUnit = 1 });
        bands.Add(new ItemValueBand { Id = "material.again", Category = ItemCategory.Material, Tier = 0, GoldPerUnit = 7 });
        var errors = Errors(Settings(), bands);
        Assert.Contains(errors, e => e.StartsWith("ItemValueBand.Category", StringComparison.Ordinal) && e.Contains("PriceBase", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("ItemValueBand.Tier", StringComparison.Ordinal) && e.Contains("material.again", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no tier-0 row for category 'Consumable'", StringComparison.Ordinal));
    }

    [Fact]
    public void The_committed_rows_load_and_every_item_a_transfer_can_move_has_a_value()
    {
        var set = RepositoryTables();
        Assert.NotNull(TransferFlagSettings.Find(set));
        Assert.Empty(EconomyCoverage.Check(set));
        var values = Shared.Simulation.Economy.ItemValue.From(set)!;
        Assert.NotEmpty(Rows<ItemDef>(set));
        foreach (var item in Rows<ItemDef>(set))
        {
            if (!ItemCategories.IsNonTradable(item.Category))
                Assert.True(values.UnitValue(item.Id, 0) > 0, $"{item.Id} has no value");
        }
    }

    private static IEnumerable<T> Rows<T>(ConfigSet set) => set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>() : [];

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
}
