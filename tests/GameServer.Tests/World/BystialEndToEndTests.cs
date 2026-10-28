using System.Diagnostics;
using Dapper;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Stats;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// The WP-M2-8 "done" scenario over the whole server (spec 14.2; the committed M1 world and tables, a real LiteNetLib
/// client, sessions on Redis and PostgreSQL): a warrior with 99 demon kills saved (the test-only way to the milestone:
/// <c>monster_kill_counts</c> seeded, as <c>tools/qa/Set-KillCount.ps1</c> does for a play session) kills one more demon;
/// the book state shows the normal demon card (R-14.2-02); out of combat it places the card and its Max HP rises by the
/// card's +10 in the own-stats message (R-14.2-05); after a restart the card is still in its slot and the bonus still applies.
/// Runs about a minute of real time.
/// </summary>
public sealed class BystialEndToEndTests : IAsyncLifetime
{
    private const long Account = 820;
    private const string SpawnPointId = "m1.spawn.combat.a";
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private ServerTestEnvironment _env = null!;
    private Bootstrap? _server;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync() => _env = await ServerTestEnvironment.CreateAsync();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task After_the_100th_demon_kill_a_card_appears_and_placing_it_raises_max_hp()
    {
        var server = _server = _env.StartServer(_config);
        Assert.True(server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        // 1.5 m south of the demon, facing north: inside the warrior's attack cone (MonsterKillEndToEndTests).
        var character = await _env.CreateCharacterAsync(Account, xCm: markerX, yCm: markerY - 150);
        await SeedKillsAsync(character.CharacterId, "m1.demon", 99);
        Assert.True(server.World.Combat!.TryGetMonster("m1.demon", out var demon));
        ushort demonNetId = (ushort)demon.NetId;
        int maxHpStat = _config.Combat!.Config.Stats.IdFor(StatIds.MaxHp);

        var client = Connect(server);
        Run(() => { Idle(client); return Card(client, demonNetId) is { Kills: 99, Tier: BystialTier.None } && client.OwnStats.Count > 0; });
        int maxHpBefore = client.OwnStats[^1].Values[maxHpStat];

        // The 100th kill: attack the demon until it dies.
        uint? monsterId = null;
        Run(() =>
        {
            Idle(client);
            var monster = client.Snapshots.SelectMany(s => s.Entities).FirstOrDefault(e => e.XCm == markerX && e.ZCm == markerY);
            monsterId = monster.EntityId == 0 ? null : monster.EntityId;
            return monsterId != null;
        });
        int frame = 0;
        Run(() =>
        {
            frame++;
            client.SendFrame(MoveKeys.None, 0, _config.StartingValues.InputRedundancy,
                frame % 15 == 1 ? InputActions.Attack : InputActions.None, ViewTick(client));
            return client.Hits.Any(h => h.Hit.TargetId == monsterId && h.Hit.Kind == HitKind.Killed);
        });

        // The card appears once the class 2 save that holds it committed.
        Run(() => { Idle(client); return Card(client, demonNetId) is { Tier: BystialTier.Normal }; });
        var earned = Card(client, demonNetId)!;
        Assert.Equal((100L, 500L, BookLimits.NoSlot), (earned.Kills, earned.NextTierKills, earned.Slot));
        Assert.Contains(client.Books, b => b.Cause == BookCause.CardEarned);

        // Place it: refused while in combat (R-14.2-06), so ask again until the combat state is over.
        uint request = 0;
        Run(() =>
        {
            Idle(client);
            if (client.BookResults.Count == request)
                client.SendBookChange(++request, demonNetId, 0);
            return client.BookResults.Any(r => r.Reason == BookReason.None);
        });
        Assert.All(client.BookResults.Take(client.BookResults.Count - 1), r => Assert.Equal(BookReason.InCombat, r.Reason));
        Run(() => { Idle(client); return client.OwnStats[^1].Values[maxHpStat] == maxHpBefore + 10; });
        Assert.Equal((byte)0, Card(client, demonNetId)!.Slot);

        // A restart: the book is read back with the card in its slot and the bonus applies again.
        client.Dispose();
        _client = null;
        server.Stop();
        var stored = await new BystialRepository(_env.RealmDatabase).LoadAsync(character.CharacterId);
        Assert.Equal(new BystialCardRow("m1.demon", BystialTier.Normal, 0), Assert.Single(stored.Cards));
        server = _server = _env.StartServer(_config);
        client = Connect(server);
        Run(() => { Idle(client); return client.Books.Count > 0 && client.OwnStats.Count > 0; });
        Assert.Equal((BystialTier.Normal, (byte)0), (Card(client, demonNetId)!.Tier, Card(client, demonNetId)!.Slot));
        Assert.Equal(maxHpBefore + 10, client.OwnStats[^1].Values[maxHpStat]);
    }

    private async Task SeedKillsAsync(long characterId, string monsterId, long count)
    {
        await using var connection = await _env.RealmDatabase.OpenConnectionAsync();
        await connection.ExecuteAsync("""
            INSERT INTO monster_kill_counts (character_id, monster_def_id, count, updated_at)
            VALUES (@characterId, @monsterId, @count, now())
            """, new { characterId, monsterId, count });
    }

    private LoopbackGameClient Connect(Bootstrap server)
    {
        _client = new LoopbackGameClient(server.Transport.LocalPort, _env.Ticket(_config, Account));
        return _client;
    }

    private static BookCard? Card(LoopbackGameClient client, ushort monsterNetId) =>
        client.Books.Count == 0 ? null : client.Books[^1].Cards.FirstOrDefault(c => c.MonsterNetId == monsterNetId);

    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
    }

    /// <summary>Calls <paramref name="step"/> about once per server tick (polling the client) until it returns true.</summary>
    private void Run(Func<bool> step)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < Deadline, "Timed out waiting for the server.");
            Assert.Null(_server!.WorldThread.Failure);
            _client!.Poll();
            if (_client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
