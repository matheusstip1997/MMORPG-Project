using System.Diagnostics;
using GameServer.Host;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog D3 over the whole server loop: the committed M1 world and tables, a real LiteNetLib client, sessions on Redis
/// and PostgreSQL. A warrior south of spawn point <c>m1.spawn.combat.a</c> faces the demon and presses the normal attack
/// with its view tick (R-3.1-03) until the demon dies (it fights back from 1.5 m, backlog F; a warrior outlasts it): the
/// client gets the hit messages, the killing blow and the despawn, then a new demon at the same marker 30 s (600 ticks,
/// spec 12.7 <c>monsterRespawnMs</c>) after the death.
/// Runs about 45 s of real time.
/// </summary>
public sealed class MonsterKillEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
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
    public async Task A_player_kills_the_demon_with_normal_attacks_and_it_respawns_after_30_s()
    {
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        // 1.5 m south of the demon, facing north (facing 0 = +Y): inside the 220 cm cone of attack.basic.
        await _env.CreateCharacterAsync(accountId: 310, xCm: markerX, yCm: markerY - 150);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 310));
        var client = _client;

        uint? monsterId = null;
        Run(() =>
        {
            Idle(client);
            var monster = client.Snapshots.SelectMany(s => s.Entities).FirstOrDefault(e => e.XCm == markerX && e.ZCm == markerY);
            monsterId = monster.EntityId == 0 ? null : monster.EntityId;
            return monsterId != null;
        });

        // Press attack every 15 frames (the attack takes 14 ticks); refusals for stamina just wait for the regeneration.
        int frame = 0;
        Run(() =>
        {
            frame++;
            var actions = frame % 15 == 1 ? InputActions.Attack : InputActions.None;
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, actions, ViewTick(client));
            return client.Hits.Any(h => h.Hit.TargetId == monsterId && h.Hit.Kind == HitKind.Killed);
        });

        var hits = client.Hits.Where(h => h.Hit.TargetId == monsterId).ToList();
        Assert.True(hits.Count >= 2, "several hits before the kill");
        Assert.All(hits, h => Assert.Equal(client.OwnEntityId, h.Hit.AttackerId));
        Assert.All(hits[..^1], h => Assert.Equal(HitKind.Damaged, h.Hit.Kind));
        Assert.All(hits, h => Assert.True(h.Hit.Amount > 0));
        Assert.True(_server.World.Combat!.TryGetMonster("m1.demon", out var demon));
        Assert.Equal(demon.MaxHp, hits.Sum(h => h.Hit.Amount)); // the last hit takes what was left
        uint deathTick = hits[^1].Tick;

        Run(() => { Idle(client); return client.Snapshots.Any(s => s.Despawns.Contains(monsterId!.Value)); });

        // The new demon: a monster spawn after the death whose entity stands on marker a (spawn point b is in interest too).
        bool IsRespawn(DecodedSnapshot s) => s.Header.ServerTick > deathTick && s.Spawns.Any(sp => sp.Kind == EntityKind.Monster
            && sp.EntityId != monsterId && s.Entities.Any(e => e.EntityId == sp.EntityId && e.XCm == markerX && e.ZCm == markerY));
        Run(() => { Idle(client); return client.Snapshots.Any(IsRespawn); });
        var respawn = client.Snapshots.First(IsRespawn);
        var fresh = respawn.Entities.Single(e => e.XCm == markerX && e.ZCm == markerY);
        Assert.Equal(Snapshot.TopHpBucket, fresh.HpBucket);
        Assert.Equal((uint)_server.World.Combat.MonsterRespawnTicks, respawn.Header.ServerTick - deathTick);
    }

    /// <summary>The server tick this client would render others at: its newest snapshot, 2 ticks of interpolation behind.</summary>
    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
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
            if (_client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
