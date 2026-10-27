using System.Diagnostics;
using GameServer.Host;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog F over the whole server loop: the committed M1 world and tables, a real LiteNetLib client, sessions on Redis and
/// PostgreSQL. The monster of SpawnPoints row <c>m1.spawn.combat.a</c> spawns at its marker and reaches a nearby player's
/// snapshot with its type and HP bucket; the player gets its own exact resources. (The 30 s respawn is covered tick by
/// tick in <see cref="CombatantWorldTests"/>.)
/// </summary>
public sealed class MonsterEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync()
    {
        _env = await ServerTestEnvironment.CreateAsync();
        _server = _env.StartServer(_config);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task A_player_next_to_the_spawn_point_receives_the_monster_with_its_type_and_full_HP_bucket()
    {
        var rules = _config.Combat!;
        var point = Assert.Single(rules.SpawnPoints, p => p.MarkerId == SpawnPointId);
        Assert.True(rules.TryGetMonster(point.MonsterId, out var monsterDef));
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = Shared.Simulation.FixedPoint.ToCmRound(marker.Position.X);
        int markerY = Shared.Simulation.FixedPoint.ToCmRound(marker.Position.Y);

        // A warrior 4 m west of the marker, well inside the 30 m interest area.
        await _env.CreateCharacterAsync(accountId: 300, xCm: markerX - 400, yCm: markerY);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 300));
        _env.WaitFor(() => _client.Snapshots.Any(s => s.Spawns.Any(sp => sp.Kind == EntityKind.Monster)), _client);

        var withSpawn = _client.Snapshots.First(s => s.Spawns.Any(sp => sp.Kind == EntityKind.Monster));
        // Snapshots carry the ground plane's Y as Z (spec 1.7). Spawn point b is in interest too.
        var monster = withSpawn.Entities.Single(e => e.XCm == markerX && e.ZCm == markerY);
        var spawn = withSpawn.Spawns.Single(sp => sp.EntityId == monster.EntityId);
        Assert.Equal(EntityKind.Monster, spawn.Kind);
        Assert.Equal((ushort)monsterDef.NetId, spawn.DefId);
        Assert.Equal(Snapshot.TopHpBucket, monster.HpBucket);
        Assert.Equal(marker.Facing, monster.Facing);

        // The observer's own resources: its saved HP (CreateCharacterAsync saves 100) of the warrior's max, the shared
        // stamina pool (spec 12.7).
        var header = withSpawn.Header;
        Assert.True(header.HasOwnResources);
        Assert.True(rules.TryGetClass("warrior", out var warrior));
        Assert.Equal(TestCombat.AtLevel1(rules, warrior, StatIds.MaxHp), header.Own.MaxHp); // spec 3.4: base plus attributes
        Assert.Equal(100, header.Own.Hp);
        Assert.Equal(_config.StartingValues.StaminaPool, header.Own.MaxStamina);
        Assert.Equal(EntityKind.Player, withSpawn.Spawns.Single(sp => sp.EntityId == withSpawn.Self.EntityId).Kind);
    }

    [Fact]
    public async Task A_player_at_the_city_spawn_does_not_see_the_monsters_yet()
    {
        // The nearest spawn point is 32 m from the city spawn, just outside the 30 m interest area (sample world layout).
        await _env.CreateCharacterAsync(accountId: 301);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 301));
        _env.WaitFor(() => _client.Snapshots.Count >= 5, _client);

        Assert.All(_client.Snapshots, s => Assert.DoesNotContain(s.Spawns, sp => sp.Kind == EntityKind.Monster));
        Assert.All(_client.Snapshots, s => Assert.Single(_client.WithoutNpcs(s))); // the city NPCs (WP-M2-4) are in view
    }
}
