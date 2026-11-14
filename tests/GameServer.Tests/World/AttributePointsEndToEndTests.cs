using System.Diagnostics;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation.Stats;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// WP-M2-13's done criterion over the whole server loop (spec 3.4 R-3.4-08): the committed world and tables, a real
/// LiteNetLib client, sessions on Redis and PostgreSQL. A level-3 warrior with its 2 unspent points places one on STR; the
/// answer comes only after the save committed (rule 5), then the progression state shows it and the own stats show STR
/// and Atk one higher; a double submit changes nothing; the database holds the row and one audit row (what a new login
/// loads, <c>CharacterRepository</c>).
/// </summary>
public sealed class AttributePointsEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
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
    public async Task A_level_3_character_places_a_point_and_sees_its_stats_rise_after_the_commit()
    {
        var character = await _env.CreateCharacterAsync(accountId: 530, level: 3);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 530));
        var client = _client;

        Run(() => { Idle(client); return client.Progressions.Count > 0 && client.OwnStats.Count > 0; });
        Assert.Equal((3, 2, 0), (client.Progressions[^1].Level, client.Progressions[^1].AttributePoints, client.Progressions[^1].StrPlaced));
        // The starter weapon's grant commits with the first class 2 batch, and only then (the committed gear, rule 5) adds its
        // Atk. Wait for exactly that state, not for a quiet spell: on a busy runner the commit came after 2 s without stats and
        // the weapon's +10 landed between the two reads below (fixer backlog M4 wave 0, item 12: expected 27, actual 37).
        var ids = _server.World.Combat!.Config.Stats;
        var items = _config.Items!;
        Assert.True(items.TryGetItem(items.ClassGear("warrior")!.StarterWeaponId, out var weapon));
        int weaponAtk = items.BaseBonuses(weapon.NetId).Where(b => b.StatId == StatIds.Atk).Sum(b => b.Flat);
        Assert.True(weaponAtk > 0);
        int unarmedAtk = client.OwnStats[0].Values[ids.IdFor(StatIds.Atk)]; // the entry's stats, before any commit
        Run(() => { Idle(client); return client.OwnStats[^1].Values[ids.IdFor(StatIds.Atk)] == unarmedAtk + weaponAtk; });
        int str = client.OwnStats[^1].Values[ids.IdFor(StatIds.Str)];
        int atk = client.OwnStats[^1].Values[ids.IdFor(StatIds.Atk)];

        client.SendAttributeSpend(1, Attributes.Str);
        client.SendAttributeSpend(1, Attributes.Str); // a double submit
        Run(() => { Idle(client); return client.AttributeResults.Count > 0; });
        Assert.Equal(AttributeReason.None, client.AttributeResults[0].Reason);
        Run(() => { Idle(client); return client.Progressions[^1].StrPlaced == 1; });
        Assert.Equal(1, client.Progressions[^1].AttributePoints);
        Run(() => { Idle(client); return client.OwnStats[^1].Values[ids.IdFor(StatIds.Str)] == str + 1; });
        Assert.Equal(atk + 1, client.OwnStats[^1].Values[ids.IdFor(StatIds.Atk)]);

        // What the save wrote: the row and one audit row, whatever the double submit did.
        var progression = new ProgressionRepository(_env.RealmDatabase);
        var audit = Assert.Single(await progression.AttributeOpsAsync(character.CharacterId));
        Assert.Equal(((short)1, (short?)Attributes.Str), (audit.Op, audit.Attribute));
        var stored = (await _env.Characters.LoadAsync(character.CharacterId))!.State;
        Assert.Equal((1, 1), (stored.AttributePoints, stored.StrPlaced));
        Assert.All(client.AttributeResults, r => Assert.Equal(AttributeReason.None, r.Reason));
        Assert.Equal(1, client.Progressions[^1].StrPlaced);
    }

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None,
                Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2));
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
