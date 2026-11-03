using System.Diagnostics;
using GameServer.Host;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog F over the whole server loop: the committed M1 world and tables, a real LiteNetLib client, sessions on Redis and
/// PostgreSQL. A warrior 6 m south of the demon of spawn point <c>m1.spawn.combat.a</c> (inside its 8 m aggro range) stands
/// still: the demon walks up and attacks it, which the client sees as the demon's action state in snapshots and as hit
/// messages. The warrior hits back once, then runs south out of the demon's 15 m leash: the demon walks back to its
/// marker and heals (user decision 2026-10-22). Runs about 30 s of real time.
/// </summary>
public sealed class MonsterAiEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private const ushort South = 32768; // facing 0 is +Y
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
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
    public async Task A_monster_aggroes_a_player_attacks_it_and_leashes_back_home()
    {
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        await _env.CreateCharacterAsync(accountId: 320, xCm: markerX, yCm: markerY - 600);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 320));
        var client = _client;

        // The demon: the monster within 3 m of the marker (spawn point b is 18 m away). It may already be moving.
        uint monsterId = 0;
        Run(() =>
        {
            Idle(client);
            var monsters = client.Snapshots.SelectMany(s => s.Spawns).Where(sp => sp.Kind == EntityKind.Monster)
                .Select(sp => sp.EntityId).ToHashSet();
            var demon = client.Snapshots[^1].Entities.FirstOrDefault(e => monsters.Contains(e.EntityId)
                && Math.Abs(e.XCm - markerX) <= 300 && Math.Abs(e.ZCm - markerY) <= 300);
            monsterId = demon.EntityId;
            return monsterId != 0;
        });
        uint self = client.OwnEntityId!.Value;

        // Aggro and chase: it walks toward the player and starts an attack (snapshot state Startup, a monster action).
        Run(() =>
        {
            Idle(client);
            return Monster(client, monsterId) is { State: 1 } m && m.ActionId >= 201;
        });
        Assert.True(Monster(client, monsterId)!.Value.ZCm < markerY, "the demon left its marker toward the player");

        // Its strike lands on the player.
        Run(() => { Idle(client); return client.Hits.Any(h => h.Hit.AttackerId == monsterId && h.Hit.TargetId == self); });
        Assert.All(client.Hits.Where(h => h.Hit.AttackerId == monsterId), h => Assert.True(h.Hit.Amount > 0));

        // The player hits back, facing the demon, until one swing lands.
        int frame = 0;
        Run(() =>
        {
            frame++;
            var own = client.Snapshots[^1].Self;
            var demon = Monster(client, monsterId)!.Value;
            ushort facing = FixedTrig.AngleOf(demon.XCm - own.XCm, demon.ZCm - own.ZCm);
            var actions = frame % 15 == 1 ? InputActions.Attack : InputActions.None;
            client.SendFrame(MoveKeys.None, facing, _config.StartingValues.InputRedundancy, actions, ViewTick(client));
            return client.Hits.Any(h => h.Hit.AttackerId == self && h.Hit.TargetId == monsterId);
        });
        Assert.True(Monster(client, monsterId)!.Value.HpBucket < Snapshot.TopHpBucket);

        // The player runs 25 m south: the demon follows until it is past its 15 m leash, then goes home and heals.
        Run(() =>
        {
            client.SendFrame(MoveKeys.S, South, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
            return client.Snapshots[^1].Self.ZCm <= markerY - 2_500;
        });
        Run(() =>
        {
            Idle(client, South);
            return Monster(client, monsterId) is { } m && Math.Abs(m.XCm - markerX) <= 50 && Math.Abs(m.ZCm - markerY) <= 50
                && m.HpBucket == Snapshot.TopHpBucket;
        });
        Assert.True(_server.World.Combat!.TryGetMonster("m1.demon", out _));
    }

    /// <summary>The demon in the client's newest snapshot, if it is in interest.</summary>
    private static EntitySnapshot? Monster(LoopbackGameClient client, uint monsterId)
    {
        foreach (var entity in client.Snapshots[^1].Entities)
        {
            if (entity.EntityId == monsterId)
                return entity;
        }
        return null;
    }

    /// <summary>The server tick this client would render others at: its newest snapshot, 2 ticks of interpolation behind.</summary>
    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client, ushort facing = 0)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
    }

    /// <summary>Calls <paramref name="step"/> about once per server tick (polling the client) until it returns true.</summary>
    private void Run(Func<bool> step)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < Deadline, "Timed out waiting for the server.");
            Assert.Null(_server.WorldThread.Failure);
            _client!.Poll();
            if (_client.Connected && _client.Snapshots.Count > 0 && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
