using GameServer.Combat;
using GameServer.Host.Config;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.Support;

/// <summary>Combat rules from the repository's tables, with the spawn points a test needs on its own map.</summary>
public static class TestCombat
{
    public const string MonsterSpawnId = "test.monster.a";
    public const string MonsterId = "m1.demon";
    public const string BossId = "m1.cyclopsBoss";

    /// <summary>The repository's tables, freshly loaded so a test can change values.</summary>
    public static ConfigSet Tables()
    {
        var result = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        return result.Set;
    }

    /// <summary>The repository's combat tables with <paramref name="spawnPoints"/> instead of its SpawnPoints rows.</summary>
    public static CombatRules Rules(params SpawnPoints[] spawnPoints) => Rules(Tables(), spawnPoints);

    public static CombatRules Rules(ConfigSet set, params SpawnPoints[] spawnPoints)
    {
        set.Add(new ConfigTable(typeof(SpawnPoints), 1, spawnPoints));
        var errors = new List<ValidationError>();
        var rules = CombatRules.TryBuild(set, errors);
        Assert.True(rules != null, string.Join("\n", errors));
        return rules!;
    }

    /// <summary>
    /// A derived stat of a level-1 character of <paramref name="classDef"/> before modifiers: the class base plus its
    /// attributes' share (spec 3.4 "M2 primary attributes", WP-M2-2). Clamped like the stat block does.
    /// </summary>
    public static int AtLevel1(CombatRules rules, ClassDef classDef, string statId)
    {
        var block = rules.ForPlayer(classDef, 0).Stats;
        return block.Get(rules.Config.Stats.IdFor(statId));
    }

    public static SpawnPoints Point(string markerId = MonsterSpawnId, string monsterId = MonsterId) =>
        new() { MarkerId = markerId, MonsterId = monsterId };

    public static WorldMarker MonsterMarker(int xCm, int yCm, ushort facing = 0, string id = MonsterSpawnId) =>
        new(id, MarkerKind.MonsterSpawn, FixVec2.FromCm(xCm, yCm), facing);

    /// <summary><see cref="TestMaps.Open"/> (player spawn at the origin) with monster spawn markers.</summary>
    public static WorldMap OpenMap(params WorldMarker[] monsterMarkers)
    {
        var open = TestMaps.Open();
        return TestMaps.Map(open.Grid, [TestMaps.SpawnMarker(0, 0), .. monsterMarkers]);
    }
}
