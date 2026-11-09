using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="FeeRates"/> (spec 4.2 R-4.2-05, 8.5; WP-M4-1): percents within 0..100, min &lt;= default &lt;= max, the id a fee
/// type, one row per fee type (<see cref="EconomyCoverage"/>); the fee volume fields of <see cref="EconomySimScenario"/>; the
/// committed data passes with the decision's default rates.
/// </summary>
public class FeeTablesTests
{
    private static FeeRates Row(string id, int defaultPercent = 5, int min = 0, int max = 15, int destroyed = 50) => new()
    {
        Id = id, DefaultPercent = defaultPercent, MinPercent = min, MaxPercent = max, DestroyedSharePercent = destroyed,
    };

    private static List<string> Errors(params FeeRates[] rows)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(FeeRates), 1, rows.Cast<object>().ToList()));
        return TableValidator.Validate(set).Select(e => e.Field + ": " + e.Message)
            .Concat(EconomyCoverage.Check(set).Select(e => e.Field + ": " + e.Message)).ToList();
    }

    [Fact]
    public void One_row_per_fee_type_within_bounds_passes()
    {
        Assert.Empty(Errors(Row("listing", 2), Row("saleTax"), Row("contract")));
    }

    [Fact]
    public void A_missing_fee_type_is_an_error()
    {
        var errors = Errors(Row("listing", 2), Row("saleTax"));
        Assert.Contains(errors, e => e.Contains("no row for fee type 'contract'", StringComparison.Ordinal));
    }

    [Fact]
    public void An_id_that_is_not_a_fee_type_is_an_error()
    {
        var errors = Errors(Row("listing", 2), Row("saleTax"), Row("contract"), Row("tax"));
        Assert.Contains(errors, e => e.Contains("'tax' is not a fee type", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, 6, 15, "MinPercent")]
    [InlineData(16, 0, 15, "MaxPercent")]
    public void The_default_rate_must_lie_within_the_owner_bounds(int defaultPercent, int min, int max, string field)
    {
        var errors = Errors(Row("listing", 2), Row("saleTax", defaultPercent, min, max), Row("contract"));
        Assert.Contains(errors, e => e.StartsWith(field, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(101, 0, 101, 50)]
    [InlineData(5, -1, 15, 50)]
    [InlineData(5, 0, 15, 101)]
    public void Percents_stay_within_0_and_100(int defaultPercent, int min, int max, int destroyed)
    {
        Assert.NotEmpty(Errors(Row("listing", 2), Row("saleTax", defaultPercent, min, max, destroyed), Row("contract")));
    }

    [Fact]
    public void The_committed_fee_rates_are_the_decisions_defaults()
    {
        var set = RepositoryTables();
        Assert.Empty(EconomyCoverage.Check(set));
        Assert.True(set.TryGet(typeof(FeeRates), out var table));
        var rates = table.Rows.Cast<FeeRates>().ToDictionary(r => r.Id, r => r.DefaultPercent);
        // M4 decision 3 (placeholders): listing fee 2 %, sale tax 5 %, contract fee 5 %.
        Assert.Equal(new Dictionary<string, int> { ["listing"] = 2, ["saleTax"] = 5, ["contract"] = 5 }, rates);
    }

    [Theory]
    [InlineData(2, 0, 0, 500, 100, "tradeValueMaxGold")] // max below min
    [InlineData(2, 0, 0, 0, 0, "tradeValueMaxGold")] // fees played with no value
    public void A_scenarios_fee_volume_needs_a_value_range(int listings, int trades, int contracts, int min, int max, string field)
    {
        var row = new EconomySimScenario
        {
            Id = "s", Players = 1, Hours = 1, Runs = 1, KillsPerPlayerMinuteMilli = 1000, KillsPerVisit = 1, SampleMinutes = 10,
            SettleWindowPercent = 50, ListingsPerPlayerHour = listings, TradesPerPlayerHour = trades,
            ContractsPerPlayerHour = contracts, TradeValueMinGold = min, TradeValueMaxGold = max,
        };
        var errors = new RowErrors();
        row.Validate(errors);
        Assert.Contains(errors.Items, e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase));
        Assert.True(row.PlaysFees());
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
}
