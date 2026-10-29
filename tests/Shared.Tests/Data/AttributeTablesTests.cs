using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>
/// The committed stats v2 data (spec 3.4 "M2 primary attributes", WP-M2-2): AttributeFormula, the ClassDef attributes,
/// the new StatDef rows and ConsumptionMeterSettings load and validate, and every class keeps its M1 stats at level 1.
/// </summary>
public class AttributeTablesTests
{
    /// <summary>
    /// PLACEHOLDER parity (WP-M2-2): the M1 values of each class (ClassDef before attributes existed), which the level-1
    /// character must still have so M1 combat and monster tuning are unchanged there. The Warrior K pass may move them on
    /// purpose; update this table with it.
    /// </summary>
    [Theory]
    // class, maxHp, maxMp, atk, matk, def, mdef, critRate
    [InlineData("warrior", 150, 30, 20, 5, 15, 8, 0)]
    [InlineData("archer", 110, 40, 18, 6, 8, 8, 0)]
    [InlineData("mage", 90, 100, 5, 22, 6, 14, 0)]
    [InlineData("assist", 120, 80, 10, 16, 10, 12, 0)]
    public void A_level_1_character_has_its_M1_stats(string classId, int maxHp, int maxMp, int atk, int matk, int def, int mdef, int critRate)
    {
        var set = LoadRepository();
        var errors = new List<ValidationError>();
        var config = CombatConfig.TryBuild(set, errors);
        Assert.True(config != null, string.Join("\n", errors));
        var classDef = ClassDef.Find(set, classId)!;
        var ids = config!.Ids;
        var block = new StatBlock(config.Stats, config.Attributes);
        block.SetBase(ids.MaxHp, classDef.MaxHp);
        block.SetBase(ids.MaxMp, classDef.MaxMp);
        block.SetBase(ids.Atk, classDef.Atk);
        block.SetBase(ids.Matk, classDef.Matk);
        block.SetBase(ids.Def, classDef.Def);
        block.SetBase(ids.Mdef, classDef.Mdef);
        Attributes.SetBases(block, config.Attributes, classDef, level: 1);

        Assert.Equal(new[] { maxHp, maxMp, atk, matk, def, mdef, critRate },
            new[] { block.Get(ids.MaxHp), block.Get(ids.MaxMp), block.Get(ids.Atk), block.Get(ids.Matk), block.Get(ids.Def),
                block.Get(ids.Mdef), block.Get(ids.CritRate) });
    }

    [Fact]
    public void Every_class_grows_from_level_2()
    {
        var set = LoadRepository();
        foreach (var row in set.TryGet(typeof(ClassDef), out var classes) ? classes.Rows.Cast<ClassDef>() : Enumerable.Empty<ClassDef>())
        {
            int grown = 0;
            for (int attribute = 0; attribute < Attributes.Count; attribute++)
                grown += Attributes.Of(row, attribute, 30) - Attributes.Of(row, attribute, 1);
            Assert.True(grown > 0, $"{row.Id} gains no attribute by level 30");
        }
    }

    [Fact]
    public void The_new_StatDef_rows_have_their_caps()
    {
        var stats = LoadRepository().TryGet(typeof(StatDef), out var table) ? table.Rows.Cast<StatDef>().ToDictionary(s => s.Id) : new();
        foreach (var id in StatIds.Attributes)
            Assert.Equal(0, stats[id].Min);
        Assert.Equal(40, stats["cooldownReductionPercent"].Max); // R-3.4-02: cooldown reduction is capped
        Assert.True(stats["moveSpeedPercent"].Max < 1_000);
        Assert.True(stats["attackSpeedPercent"].Max < 1_000);
        Assert.True(stats.Count <= Shared.Protocol.StatsCodec.MaxStatCount);
    }

    [Fact]
    public void Consumption_meters_have_a_cap_and_a_decay()
    {
        var set = LoadRepository();
        var meters = set.Single<ConsumptionMeterSettings>();
        Assert.True(meters.ToxicityMax >= 1 && meters.SatietyMax >= 1);
        Assert.True(meters.ToxicityDecayPerMinute > 0 && meters.SatietyDecayPerMinute > 0);
    }

    /// <summary>The committed data/tables, read with the client's portable reader and validated.</summary>
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
