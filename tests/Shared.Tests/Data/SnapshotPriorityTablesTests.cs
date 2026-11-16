using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Tests.Data;

/// <summary>
/// <see cref="SnapshotPriority"/> and <see cref="SnapshotKindWeight"/> (spec 1.7 priority, user decision 2026-11-16): both radii
/// within the interest area, the stale limit at least one snapshot interval, one weight row per entity kind, and the
/// committed rows load.
/// </summary>
public class SnapshotPriorityTablesTests
{
    private static SnapshotPriority Row() => new()
    {
        TelegraphRadiusCm = 1_500, NearRadiusCm = 1_500, FarWeightPercent = 25, MaxStaleMs = 500,
    };

    private static List<SnapshotKindWeight> AllKinds() =>
        Enumerable.Range((int)EntityKind.Player, (int)EntityKind.LootDrop)
            .Select(kind => new SnapshotKindWeight { Id = SnapshotKindWeight.IdOf((EntityKind)kind), WeightPercent = 100 })
            .ToList();

    private static List<string> Errors(SnapshotPriority row, IEnumerable<SnapshotKindWeight>? weights = null,
        int interestRadiusCm = 3_000, int snapshotRateHz = 20)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(SnapshotPriority), 1, [row]));
        set.Add(new ConfigTable(typeof(SnapshotKindWeight), 1, (weights ?? AllKinds()).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1,
            [new StartingValuesM1 { InterestRadiusCm = interestRadiusCm, SnapshotRateHz = snapshotRateHz }]));
        return TableValidator.Validate(set).Where(e => e.Table is "SnapshotPriority" or "SnapshotKindWeight")
            .Select(e => e.Field + ": " + e.Message).ToList();
    }

    [Fact]
    public void A_row_within_the_interest_area_with_every_kind_passes()
    {
        Assert.Empty(Errors(Row()));
    }

    [Fact]
    public void Radii_beyond_the_interest_area_are_errors()
    {
        var row = Row();
        row.TelegraphRadiusCm = 3_001;
        row.NearRadiusCm = 3_001;
        var errors = Errors(row);
        Assert.Contains(errors, e => e.StartsWith("TelegraphRadiusCm", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("NearRadiusCm", StringComparison.Ordinal));
    }

    [Fact]
    public void A_stale_limit_shorter_than_one_snapshot_interval_is_an_error()
    {
        var row = Row();
        row.MaxStaleMs = 40; // 50 ms between snapshots at 20 Hz
        Assert.Contains(Errors(row), e => e.StartsWith("MaxStaleMs", StringComparison.Ordinal));
        row.MaxStaleMs = 50;
        Assert.Empty(Errors(row));
    }

    [Fact]
    public void Every_entity_kind_needs_a_weight_and_a_row_must_name_a_kind()
    {
        var weights = AllKinds().Where(w => w.Id != "shopStall").ToList();
        Assert.Contains(Errors(Row(), weights), e => e.Contains("shopStall", StringComparison.Ordinal));

        weights = AllKinds();
        weights.Add(new SnapshotKindWeight { Id = "none", WeightPercent = 50 });
        Assert.Contains(Errors(Row(), weights), e => e.StartsWith("Id", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("player", EntityKind.Player)]
    [InlineData("shopStall", EntityKind.ShopStall)]
    [InlineData("lootDrop", EntityKind.LootDrop)]
    public void Row_ids_are_the_kinds_in_camel_case(string id, EntityKind kind)
    {
        Assert.True(SnapshotKindWeight.TryParseKind(id, out var parsed));
        Assert.Equal(kind, parsed);
        Assert.Equal(id, SnapshotKindWeight.IdOf(kind));
        Assert.False(SnapshotKindWeight.TryParseKind("Player", out _));
    }

    [Fact]
    public void The_committed_rows_load_with_a_weight_for_every_kind()
    {
        var set = RepositoryTables();
        Assert.NotNull(SnapshotPriority.Find(set));
        Assert.True(set.TryGet(typeof(SnapshotKindWeight), out var weights));
        Assert.Equal((int)EntityKind.LootDrop, weights.Rows.Count);
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
