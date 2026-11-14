using Shared.Data;
using Shared.Data.Tables;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="ListingSettings"/> (spec 4.5, 8.5; M4 decision 6; WP-M4-7): the durations ascending and at least a minute, at most
/// the wire's count; the buckets' bursts cover a period; the highest unit price within the gold cap; and the committed row holds
/// the user's answer (12, 24 or 48 hours, 10 active listings, 20 rows a page). The auctioneer row offers the auction only.
/// </summary>
public class ListingTablesTests
{
    private static ListingSettings Row() => new()
    {
        DurationsMs = [43_200_000, 86_400_000, 172_800_000], MaxActivePerCharacter = 10, MaxUnitPrice = 1_000, PageSize = 20,
        SearchItemIdsMax = 32, SearchesPerPeriod = 5, SearchPeriodMs = 5_000, SearchBurst = 10, WritesPerPeriod = 10,
        WritePeriodMs = 5_000, WriteBurst = 20, MaxQueuedRequests = 16, ExpiryBatch = 50, OwnerHistoryMax = 16,
    };

    private static List<string> Errors(ListingSettings row, long maxGold = 1_000_000)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(ListingSettings), 1, [row]));
        set.Add(new ConfigTable(typeof(EconomySettings), 1, [new EconomySettings { MaxGold = maxGold }]));
        return TableValidator.Validate(set).Where(e => e.Table == "ListingSettings").Select(e => e.Field + ": " + e.Message).ToList();
    }

    [Fact]
    public void The_decision_row_passes()
    {
        Assert.Empty(Errors(Row()));
    }

    [Theory]
    [InlineData(new[] { 86_400_000, 43_200_000 })] // not ascending
    [InlineData(new[] { 43_200_000, 43_200_000 })] // twice
    [InlineData(new[] { 30_000 })] // under a minute
    [InlineData(new int[0])] // none
    public void Durations_are_ascending_each_once_and_at_least_a_minute(int[] durations)
    {
        var row = Row();
        row.DurationsMs = durations;
        Assert.Contains(Errors(row), e => e.StartsWith("DurationsMs", StringComparison.Ordinal));
    }

    [Fact]
    public void A_burst_below_its_rate_and_a_unit_price_above_the_gold_cap_are_errors()
    {
        var row = Row();
        row.SearchBurst = 4;
        row.WriteBurst = 9;
        row.MaxUnitPrice = 2_000_000;
        var errors = Errors(row);
        Assert.Contains(errors, e => e.StartsWith("SearchBurst", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("WriteBurst", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("MaxUnitPrice", StringComparison.Ordinal));
    }

    [Fact]
    public void The_committed_row_holds_the_users_answer_and_the_auctioneer_offers_the_auction()
    {
        var set = RepositoryTables();
        var row = set.Single<ListingSettings>();
        Assert.Equal([43_200_000, 86_400_000, 172_800_000], row.DurationsMs); // 12, 24 or 48 hours (decision 6)
        Assert.Equal((10, 20), (row.MaxActivePerCharacter, row.PageSize));
        var auctioneer = NpcDef.Find(set, "m1.auctioneer");
        Assert.NotNull(auctioneer);
        Assert.Equal(NpcKind.Auctioneer, auctioneer!.Kind);
        Assert.Equal([NpcInteraction.Auction], auctioneer.Interactions);
        Assert.True(NpcDef.Allows(NpcKind.Auctioneer, NpcInteraction.Auction));
        Assert.False(NpcDef.Allows(NpcKind.Auctioneer, NpcInteraction.Sell));
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
