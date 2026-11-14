using Shared.Data;
using Shared.Data.Tables;
using Shared.World;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="OwnerWindows"/> and <see cref="BagExpiry"/> (spec 4.3 R-4.3-03, 8.5; M4 decision 8; WP-M4-9): one window per area
/// type that drops a bag and none for one that does not, each window shorter than the expiry; the committed rows are the
/// decision's placeholders (60 s, 15 minutes).
/// </summary>
public class LootBagTablesTests
{
    private static RegionDefaults Area(string id, DeathDrop drop) => new() { Id = id, PvpMode = PvpMode.Off, DeathDrop = drop };

    private static BagExpiry Expiry(int expiryMs = 900_000) => new()
    {
        ExpiryMs = expiryMs, MaxBagsPerWorld = 2_000, ExpiryCheckIntervalMs = 1_000, ExpiryRetryMs = 5_000, OwnBagsListedMax = 8,
        MaxQueuedRequests = 4,
    };

    private static List<string> Errors(BagExpiry expiry, params OwnerWindows[] windows)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(RegionDefaults), 1, new object[] { Area("openWorld", DeathDrop.On), Area("normalCity", DeathDrop.Off) }));
        set.Add(new ConfigTable(typeof(BagExpiry), 1, new object[] { expiry }));
        set.Add(new ConfigTable(typeof(OwnerWindows), 1, windows.Cast<object>().ToList()));
        return TableValidator.Validate(set).Select(e => e.Field + ": " + e.Message).ToList();
    }

    [Fact]
    public void One_window_per_dropping_area_type_shorter_than_the_expiry_passes()
    {
        Assert.Empty(Errors(Expiry(), new OwnerWindows { Id = "openWorld", OwnerWindowMs = 60_000 }));
    }

    [Fact]
    public void A_dropping_area_type_without_a_window_is_an_error()
    {
        var errors = Errors(Expiry(), new OwnerWindows { Id = "normalCity", OwnerWindowMs = 60_000 });
        Assert.Contains(errors, e => e.Contains("openWorld drops a loot bag", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("normalCity drops no loot bag", StringComparison.Ordinal));
    }

    [Fact]
    public void A_window_that_outlasts_the_expiry_is_an_error()
    {
        var errors = Errors(Expiry(60_000), new OwnerWindows { Id = "openWorld", OwnerWindowMs = 60_000 });
        Assert.Contains(errors, e => e.StartsWith("OwnerWindowMs", StringComparison.Ordinal));
    }

    [Fact]
    public void The_committed_rows_are_the_decisions_placeholders()
    {
        var set = FeeTablesTestsAccess.RepositoryTables();
        Assert.Equal(900_000, set.Single<BagExpiry>().ExpiryMs);
        Assert.True(set.TryGet(typeof(OwnerWindows), out var table));
        Assert.All(table.Rows.Cast<OwnerWindows>(), w => Assert.Equal(60_000, w.OwnerWindowMs));
        Assert.Contains(table.Rows.Cast<OwnerWindows>(), w => w.Id == "openWorld");
    }
}

/// <summary>The committed tables, loaded and validated (the loader the other table tests use).</summary>
internal static class FeeTablesTestsAccess
{
    public static ConfigSet RepositoryTables()
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
