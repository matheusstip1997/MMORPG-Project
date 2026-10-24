using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation.Stats;

namespace Shared.Tests.Data;

/// <summary>ClassDef, MonsterDef and SpawnPoints (backlog D5 wiring, F): row rules, cross-row rules and references.</summary>
public class CombatantTablesTests
{
    private static ClassDef Class(string id = "warrior", int netId = 1) => new()
    {
        Id = id, NetId = netId, VisualId = "warrior", ArmorClass = ArmorClass.Heavy,
        MaxHp = 150, MaxMp = 30, HpRegen = 2, MpRegen = 1, Atk = 20, Matk = 5, Def = 15, Mdef = 8,
        PoiseMax = 100, FlinchResist = 20, FlinchDurationPercent = 100, FlinchImmunityMs = 1000, BodyRadiusCm = 40,
    };

    private const int StartupMinMs = 500;

    private static MonsterDef Monster(string id = "m1.demon", int netId = 1) => new()
    {
        Id = id, NetId = netId, VisualId = "demon", Rank = Rank.F,
        MaxHp = 200, Atk = 12, Def = 5, Mdef = 5, PoiseMax = 60, FlinchResist = 10, FlinchDurationPercent = 100,
        FlinchImmunityMs = 1500, BodyRadiusCm = 50,
        Attacks = new[] { "monster.bite" }, MoveSpeedCmPerSec = 350, AggroRangeCm = 800, LeashRangeCm = 1500,
    };

    private static ActionDef Action(string id, int netId, ActionKind kind = ActionKind.Attack, int startupMs = 600) => new()
    {
        Id = id, NetId = netId, Kind = kind, StartupMs = startupMs, ActiveMs = 100, RecoveryMs = 300,
        StaminaCost = kind == ActionKind.Parry ? 0 : 10, ChargePool = ActionDef.PoolOf(kind),
    };

    private static ActionEffectDef Strike(string actionId) => new()
    {
        Id = actionId + ".strike", ActionId = actionId, Kind = EffectKind.Strike, Phase = ActionPhase.Active,
        PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, DamagePercent = 100, DamageKind = EffectDamageKind.Physical,
    };

    private static EntityVisualDef Visual(string id) => new() { Id = id, PrefabAddress = "Visuals/" + id, ScalePercent = 100 };

    private static ConfigSet Set(IEnumerable<ClassDef>? classes = null, IEnumerable<MonsterDef>? monsters = null,
        IEnumerable<SpawnPoints>? spawnPoints = null, IEnumerable<ActionDef>? actions = null,
        IEnumerable<ActionEffectDef>? effects = null)
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(EntityVisualDef), 1, new object[] { Visual("warrior"), Visual("demon") }));
        set.Add(new ConfigTable(typeof(ClassDef), 1, (classes ?? new[] { Class() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(MonsterDef), 1, (monsters ?? new[] { Monster() }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(SpawnPoints), 1,
            (spawnPoints ?? new[] { new SpawnPoints { MarkerId = "m1.spawn.combat.a", MonsterId = "m1.demon" } })
            .Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ActionDef), 2,
            (actions ?? new[] { Action("monster.bite", 201) }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(ActionEffectDef), 2,
            (effects ?? new[] { Strike("monster.bite") }).Cast<object>().ToList()));
        set.Add(new ConfigTable(typeof(StartingValuesM1), 1, new object[] { new StartingValuesM1 { MonsterAttackStartupMinMs = StartupMinMs } }));
        return set;
    }

    // StartingValuesM1 is mostly zeros here: keep only the errors of the tables under test.
    private static List<ValidationError> Validate(ConfigSet set) =>
        TableValidator.Validate(set).Where(e => e.Table != "StartingValuesM1").ToList();

    [Fact]
    public void Valid_rows_pass()
    {
        Assert.Empty(Validate(Set()));
    }

    [Theory]
    [InlineData(ArmorClass.Light)]
    [InlineData(ArmorClass.Medium)]
    [InlineData(ArmorClass.Heavy)]
    public void Every_armor_class_is_valid(ArmorClass armor)
    {
        var row = Class();
        row.ArmorClass = armor;
        Assert.Empty(Validate(Set(classes: [row])));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void An_unknown_armor_class_is_refused(int value)
    {
        // R-3.3-05: light, medium or heavy, set by the class.
        var row = Class();
        row.ArmorClass = (ArmorClass)value;
        var error = Assert.Single(Validate(Set(classes: [row])));
        Assert.Equal(("ClassDef", nameof(ClassDef.ArmorClass)), (error.Table, error.Field));
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
    [InlineData(nameof(ClassDef.BodyRadiusCm), 0)]
    [InlineData(nameof(ClassDef.BodyRadiusCm), 5_001)]
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

        var errors = Validate(set);

        var error = Assert.Single(errors);
        Assert.Equal(ValidationCodes.BrokenRef, error.Code);
        Assert.Contains("paladin", error.Message);
    }

    [Theory]
    [InlineData(499, true)]
    [InlineData(500, false)]
    [InlineData(800, false)]
    public void A_monster_attack_startup_must_reach_the_monster_minimum(int startupMs, bool refused)
    {
        // R-3.1-03: reaction time plus the lag compensation cap (spec 12.7: 500 ms).
        var errors = Validate(Set(actions: new[] { Action("monster.bite", 201, startupMs: startupMs) }));

        if (refused)
        {
            var error = Assert.Single(errors);
            Assert.Equal(("MonsterDef", nameof(MonsterDef.Attacks)), (error.Table, error.Field));
            Assert.Contains("R-3.1-03", error.Message);
        }
        else
        {
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void The_startup_rule_applies_only_to_monster_attacks()
    {
        // A player's attack may be faster than the monster minimum; only MonsterDef.attacks are checked.
        var errors = Validate(Set(
            actions: new[] { Action("monster.bite", 201), Action("attack.warrior", 1, startupMs: 300) },
            effects: new[] { Strike("monster.bite"), Strike("attack.warrior") }));

        Assert.Empty(errors);
    }

    [Fact]
    public void A_monster_attack_is_an_attack_or_a_skill_with_a_strike_or_a_projectile()
    {
        var errors = Validate(Set(
            monsters: new[] { WithAttacks(Monster(), "monster.bite", "dodge", "monster.wave") },
            actions: new[] { Action("monster.bite", 201), Action("dodge", 2, ActionKind.Dodge), Action("monster.wave", 202) },
            effects: new[] { Strike("monster.bite") }));

        Assert.Contains(errors, e => e.Field == nameof(MonsterDef.Attacks) && e.Message.Contains("'dodge' is a Dodge action"));
        Assert.Contains(errors, e => e.Field == nameof(MonsterDef.Attacks) && e.Message.Contains("'monster.wave' has no strike"));
    }

    [Fact]
    public void A_monster_attack_must_exist_and_be_listed_once()
    {
        var errors = Validate(Set(monsters: new[] { WithAttacks(Monster(), "monster.bite", "monster.bite", "monster.ghost") }));

        Assert.Contains(errors, e => e.Field == nameof(MonsterDef.Attacks) && e.Message.Contains("twice"));
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Field == nameof(MonsterDef.Attacks));
    }

    [Fact]
    public void A_monster_with_a_spawn_point_needs_an_attack_and_one_without_may_have_none()
    {
        var spawned = WithAttacks(Monster());
        var boss = WithAttacks(Monster("m1.boss", 2));

        var errors = Validate(Set(monsters: new[] { spawned, boss }));

        var error = Assert.Single(errors);
        Assert.Equal(("MonsterDef", nameof(MonsterDef.Attacks)), (error.Table, error.Field));
        Assert.Contains("spawn point", error.Message);
    }

    [Theory]
    [InlineData(800, 800)]
    [InlineData(800, 500)]
    public void The_leash_range_must_be_greater_than_the_aggro_range(int aggroCm, int leashCm)
    {
        var monster = Monster();
        monster.AggroRangeCm = aggroCm;
        monster.LeashRangeCm = leashCm;

        var error = Assert.Single(Validate(Set(monsters: new[] { monster })));
        Assert.Equal(nameof(MonsterDef.LeashRangeCm), error.Field);
    }

    [Theory]
    [InlineData(nameof(MonsterDef.MoveSpeedCmPerSec), 0)]
    [InlineData(nameof(MonsterDef.AggroRangeCm), 0)]
    [InlineData(nameof(MonsterDef.LeashRangeCm), 100_001)]
    public void Monster_ai_fields_out_of_range_are_refused(string field, int value)
    {
        var monster = Monster();
        typeof(MonsterDef).GetProperty(field)!.SetValue(monster, value);

        Assert.Contains(Validate(Set(monsters: new[] { monster })), e => e.Code == ValidationCodes.OutOfRange && e.Field == field);
    }

    [Fact]
    public void The_monster_ai_settings_name_area_types_once()
    {
        var set = new ConfigSet();
        set.Add(new ConfigTable(typeof(RegionDefaults), 1, new object[] { new RegionDefaults { Id = "normalCity" } }));
        set.Add(new ConfigTable(typeof(MonsterAiSettings), 1, new object[]
        {
            new MonsterAiSettings
            {
                ThinkIntervalMs = 250, HomeToleranceCm = 50, LeashTimeoutMs = 10_000,
                NoEntryAreaTypes = new[] { "normalCity", "normalCity", "moonBase" },
            },
        }));

        var errors = TableValidator.Validate(set).Where(e => e.Table == "MonsterAiSettings").ToList();

        Assert.Contains(errors, e => e.Field == nameof(MonsterAiSettings.NoEntryAreaTypes) && e.Message.Contains("twice"));
        Assert.Contains(errors, e => e.Code == ValidationCodes.BrokenRef && e.Message.Contains("moonBase"));
    }

    private static MonsterDef WithAttacks(MonsterDef monster, params string[] attacks)
    {
        monster.Attacks = attacks;
        return monster;
    }

    [Fact]
    public void The_repository_rows_cover_the_four_root_classes_and_two_spawnable_monsters()
    {
        // Spec 3.5 (R-3.5-01) roots; the ids are what character creation writes into characters.class_id.
        var tables = LoadRepository();
        var classes = Rows<ClassDef>(tables).Select(c => c.Id).ToList();
        Assert.Equal(new[] { "warrior", "archer", "mage", "assist" }, classes);
        Assert.Equal(classes, tables.Single<CharacterCreationConfig>().AllowedInitialClasses);
        // Spec 12.7: poise 100 for players.
        Assert.All(Rows<ClassDef>(tables), c => Assert.Equal(tables.Single<StartingValuesM1>().PlayerPoise, c.PoiseMax));
        // Backlog F: two monster types with spawn points (the boss comes with its own wave), each with its attacks.
        var spawned = Rows<SpawnPoints>(tables).Select(p => p.MonsterId).Distinct().ToList();
        Assert.Equal(new[] { "m1.demon", "m1.alien" }, spawned);
        Assert.All(Rows<MonsterDef>(tables).Where(m => spawned.Contains(m.Id)), m => Assert.Equal(2, m.Attacks.Length));
        Assert.Single(Rows<MonsterDef>(tables), m => m.Boss);
        Assert.Equal(5000, tables.Single<StartingValuesM1>().PlayerRespawnDelayMs);
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
