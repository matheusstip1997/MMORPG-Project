using System.Diagnostics;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// WP-M2-5's done criterion over the whole server loop (spec 3.5): the committed M1 world and tables, a real LiteNetLib
/// client, sessions on Redis and PostgreSQL. A new level-30 warrior (no skills learned, 30 points) 1.5 m south of the
/// demon advances to Knight (R-3.5-01), learns the first Knight skill (Shield Strike, row 0: level 30), places it on Q and
/// presses it until the demon is hit by it. Every answer comes only after its save committed (rule 5), and a new login
/// reads the class, the node and the hotbar back.
/// </summary>
public sealed class SkillTreeEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private const byte Q = 7;
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
    public async Task A_level_30_character_advances_learns_its_first_tier_1_skill_and_uses_it_from_the_hotbar()
    {
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        var character = await _env.CreateCharacterAsync(accountId: 520, xCm: markerX, yCm: markerY - 150, learnKit: false, level: 30);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, 520));
        var client = _client;

        Run(() => { Idle(client); return client.SkillTrees.Count > 0 && client.Progressions.Count > 0; });
        Assert.Equal(("warrior", 0), (client.SkillTrees[0].ClassId, client.SkillTrees[0].Nodes.Length));
        Assert.Equal((30, 30), (client.Progressions[^1].Level, client.Progressions[^1].SkillPoints));

        Assert.Equal(SkillTreeReason.None, Ask(client, 1, SkillTreeOp.Advance, "knight").Reason);
        Run(() => { Idle(client); return client.SkillTrees[^1].ClassId == "knight"; });
        var learned = Ask(client, 2, SkillTreeOp.Learn, "knight.shieldStrike");
        Assert.Equal((SkillTreeReason.None, (byte)1), (learned.Reason, learned.Level));
        Assert.Equal(SkillTreeReason.None, Ask(client, 3, SkillTreeOp.AssignSlot, "knight.shieldStrike", Q).Reason);
        Run(() => { Idle(client); return client.SkillTrees[^1].Hotbar.Any(h => h.Slot == Q && h.NodeId == "knight.shieldStrike"); });
        Run(() => { Idle(client); return client.Progressions[^1].SkillPoints == 29; });

        uint? monsterId = null;
        Run(() =>
        {
            Idle(client);
            var monster = client.Snapshots.SelectMany(s => s.Entities).FirstOrDefault(e => e.XCm == markerX && e.ZCm == markerY);
            monsterId = monster.EntityId == 0 ? null : monster.EntityId;
            return monsterId != null;
        });
        // Press Q every 30 frames (the skill takes 17 ticks, its cooldown 5 s); release between presses. Nothing else is pressed,
        // so a hit of the player on the demon is Shield Strike's.
        int frame = 0;
        Run(() =>
        {
            frame++;
            byte slot = frame % 30 == 1 ? Q : (byte)0;
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client), slot);
            return client.Hits.Any(h => h.Hit.TargetId == monsterId && h.Hit.AttackerId == client.OwnEntityId);
        });
        Assert.Contains(client.Hits, h => h.Hit.TargetId == monsterId && h.Hit.Amount > 0);

        // What the saves wrote: the class, the node, the hotbar and the points, and the two audit rows.
        var skills = new SkillRepository(_env.RealmDatabase);
        Run(() => { Idle(client); return skills.OpsAsync(character.CharacterId).GetAwaiter().GetResult().Count == 2; });
        var stored = (await _env.Characters.LoadAsync(character.CharacterId))!;
        Assert.Equal(("knight", 29), (stored.ClassId, stored.State.SkillPoints));
        // The move to Q is a hotbar change: save class 2, in the next batch (rule 11), so it may land after the audit rows.
        Run(() => { Idle(client); return HotbarHasQ(skills.LoadAsync(character.CharacterId).GetAwaiter().GetResult()); });
        var record = await skills.LoadAsync(character.CharacterId);
        Assert.Equal("knight.shieldStrike", Assert.Single(record.Learned).NodeId);
        Assert.True(HotbarHasQ(record), record.HotbarJson);
    }

    private static bool HotbarHasQ(SkillRecord record) =>
        record.HotbarJson != null && record.HotbarJson.Replace("\"7\":\"", "\"7\": \"").Contains("\"7\": \"knight.shieldStrike\"", StringComparison.Ordinal);

    private SkillTreeResult Ask(LoopbackGameClient client, uint requestId, SkillTreeOp op, string target, byte slot = 0)
    {
        client.SendSkillTree(requestId, op, target, slot);
        Run(() => { Idle(client); return client.SkillResults.Any(r => r.RequestId == requestId); });
        return client.SkillResults.Single(r => r.RequestId == requestId);
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
