using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>ClassDef, MonsterDef and SpawnPoints (backlog D5 wiring, F): row rules, cross-row rules and references.</summary>
public class CombatantTablesTests
{
    private static ClassDef Class(string id = "warrior", int netId = 1) => new()
    {
        Id = id, NetId = netId, VisualId = "warrior", HeavyArmor = true,
        MaxHp = 150, MaxMp = 30, HpRegen = 2, MpRegen = 1, Atk = 20, Matk = 5, Def = 15, Mdef = 8,
        PoiseMax = 100, FlinchResist = 20, FlinchDurationPercent = 100, FlinchImmunityMs = 1000,
    };

    private static MonsterDef Monster(string id = "m1.demon", int netId = 1) => new()
    {
        Id = id, NetId = netId, VisualId = "demon", Rank = Rank.F,
        MaxHp = 200, Atk = 12, Def = 5, Mdef = 5, PoiseMax = 60, FlinchResist = 10, FlinchDurationPercent = 100,
        FlinchImmunityMs = 1500,
    };

    private static EntityVisualDef Visual(string id) => new() { Id = id, PrefabAddress = "Visuals/" + id, ScalePercent = 100 };

    private static ConfigSet Set(IEnumerable<ClassDef>? classes = null, IEnumerable<MonsterDef>? monsters = null,
        IEnumerable<SpawnPoints>? spawnPoints = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[] { Visual("warrior"), Visual("demon") }));
        set.Add(new ConfigTable(typeof(ClassDef), 1, (classes ?? new[] { Class() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MonsterDef), 1, (monsters ?? new[] { Monster() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(SpawnPoints), 1,
            (spawnPoints ?? new[] { new SpawnPoints { MarkerId = "m1.spawn.combat.a", MonsterId = "m1.demon" } })
            .Cast<object>().ToList()));
        return set;
    }

    private static List<ValidationError> Validate(ConfigSet set) =>
        TableValidator.Validate(set);

    [Fact]
    public void Valid_rows_pass()
    {
        Assert.Empty(Validate(Set()));
    }

    [Fact]
    public void Net_ids_are_unique_per_table()
    {
        var errors = Validate(Set(classes: new[] { Class("warrior", 1), Class("mage", 1) },
            monsters: new[] { Monster("m1.demon", 7), Monster("m1.alien", 7) }));

        Assert.Equal(2, errors.Count(e => e.Table == "ClassDef" && e.Field == "NetId"));
        Assert.Equal(2, errors.Count(e => e.Table == "MonsterDef" && e.Field == "NetId"));
    }

    [Fact]
    public void A_class_and_a_monster_may_share_a_net_id()
    {
        // Spawn events carry the kind, so the definition id is per kind.
        Assert.Empty(Validate(Set(classes: new[] { Class(netId: 3) }, monsters: new[] { Monster(netId: 3) })));
    }

    [Fact]
    public void Visual_ids_must_exist_in_EntityVisualDef()
    {
        var classDef = Class();
        classDef.VisualId = "nobody";
        var monster = Monster();
        monster.VisualId = "nothing";

        var errors = Validate(Set(classes: new[] { classDef }, monsters: new[] { monster }));

        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Table == "ClassDef" && e.Field == nameof(ClassDef.VisualId));
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Table == "MonsterDef" && e.Field == nameof(MonsterDef.VisualId));
    }

    [Fact]
    public void A_spawn_point_must_name_a_monster_and_a_marker()
    {
        var errors = Validate(Set(spawnPoints: new[]
        {
            new SpawnPoints { MarkerId = "m1.spawn.combat.a", MonsterId = "m1.ghost" },
            new SpawnPoints { MarkerId = " ", MonsterId = "m1.demon" },
        }));

        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(SpawnPoints.MonsterId));
        Assert.Contains(errors, e => e.Table == "SpawnPoints" && e.Field == nameof(SpawnPoints.MarkerId));
    }

    [Fact]
    public void One_spawn_point_per_marker()
    {
        var point = new SpawnPoints { MarkerId = "m1.spawn.combat.a", MonsterId = "m1.demon" };
        var errors = Validate(Set(spawnPoints: new[] { point, new SpawnPoints { MarkerId = point.MarkerId, MonsterId = "m1.demon" } }));

        Assert.Contains(errors, e => e.Code == ValidationCodes.DuplicateId && e.Table == "SpawnPoints");
    }

    [Theory]
    [InlineData(nameof(ClassDef.MaxHp), 0)]
    [InlineData(nameof(ClassDef.PoiseMax), 0)]
    [InlineData(nameof(ClassDef.FlinchDurationPercent), 1_001)]
    [InlineData(nameof(ClassDef.FlinchImmunityMs), 60_001)]
    [InlineData(nameof(ClassDef.NetId), 0)]
    [InlineData(nameof(ClassDef.NetId), 65_536)]
    public void Class_fields_out_of_range_are_refused(string field, int value)
    {
        var classDef = Class();
        typeof(ClassDef).GetProperty(field)!.SetValue(classDef, value);

        Assert.Contains(Validate(Set(classes: new[] { classDef })), e => e.Code == ValidationCodes.OutOfRange && e.Field == field);
    }

    [Fact]
    public void A_monster_rank_must_be_a_rank()
    {
        var monster = Monster();
        monster.Rank = (Rank)42;
        Assert.Contains(Validate(Set(monsters: new[] { monster })), e => e.Field == nameof(MonsterDef.Rank));
    }

    [Fact]
    public void Character_creation_offers_only_ClassDef_classes()
    {
        var set = Set();
        set.Add(new ConfigTable(typeof(CharacterCreationConfig), 2, new object[]
        {
            new CharacterCreationConfig
            {
                NameMinLength = 3, NameMaxLength = 16, NameAllowedCharacters = "abc", NameBlockedWords = Array.Empty<string>(),
                AllowedInitialClasses = new[] { "warrior", "paladin" }, StartLevel = 1, StartHp = 100, StartMp = 50,
            },
        }));

        var errors = TableValidator.Validate(set);

        var error = Assert.Single(errors);
        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
        Assert.Contains("paladin", error.Message);
    }

    [Fact]
    public void The_repository_rows_cover_the_four_root_classes_and_one_spawnable_monster()
    {
        // Spec 3.5 (R-3.5-01) roots; the ids are what character creation writes into characters.class_id.
        var tables = LoadRepository();
        var classes = Rows<ClassDef>(tables).Select(c => c.Id).ToList();
        Assert.Equal(new[] { "warrior", "archer", "mage", "assist" }, classes);
        Assert.Equal(classes, tables.Single<CharacterCreationConfig>().AllowedInitialClasses);
        // Spec 12.7: poise 100 for players.
        Assert.All(Rows<ClassDef>(tables), c => Assert.Equal(tables.Single<StartingValuesM1>().PlayerPoise, c.PoiseMax));
        var spawned = Rows<SpawnPoints>(tables).Select(p => p.MonsterId).Distinct().ToList();
        Assert.Equal(new[] { "m1.demon" }, spawned);
        Assert.Single(Rows<MonsterDef>(tables), m => m.Boss);
    }

    private static List<T> Rows<T>(ConfigSet set)
    {
        Assert.True(set.TryGet(typeof(T), out var table));
        return table.Rows.Cast<T>().ToList();
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
