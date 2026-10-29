using System.Diagnostics;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.Quests;

/// <summary>
/// WP-M2-6 over the whole server (the "done" check of plan 6, wave 1): the committed M1 world and tables, a real LiteNetLib client,
/// sessions on Redis and PostgreSQL. A warrior standing by the first demon opens the outpost's quest board, builds a two-step run of
/// kill offers, kills the demon twice, and claims the run: the claim is answered once its save committed, pays the step bonus through
/// <c>ExpService</c>, and the claimed run, its audit row and the exp are in the database. The test narrows the tables so it stays
/// short: kill offers of count 1 only, the outpost's zone to the first demon's spawn, and the outpost's reach to the field (the
/// sample world's outpost stands in the city, 17 m from that demon). One kill counts once, so the second step waits for the
/// demon's respawn (StartingValuesM1.monsterRespawnMs, 30 s): runs about 45 s.
/// </summary>
public sealed class QuestEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private const long Account = 620;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private ServerConfig _config = null!;
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync() => _env = await ServerTestEnvironment.CreateAsync();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task A_character_builds_a_two_step_run_at_the_outpost_completes_it_and_claims_the_exp()
    {
        _config = NarrowedConfig();
        _server = _env.StartServer(_config);
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        var character = await _env.CreateCharacterAsync(Account, xCm: markerX, yCm: markerY - 150);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, Account));
        var client = _client;
        Assert.True(_config.Economy!.TryGetNpc("m1.outpost", out var outpostDef));
        Assert.True(_config.Combat!.TryGetMonster("m1.demon", out var demonDef));

        uint outpost = 0, demon = 0;
        Run(client, () =>
        {
            Idle(client);
            var spawns = client.Snapshots.SelectMany(s => s.Spawns).ToList();
            outpost = spawns.FirstOrDefault(s => s.Kind == EntityKind.Npc && s.DefId == outpostDef.NetId).EntityId;
            demon = client.Snapshots.SelectMany(s => s.Entities).FirstOrDefault(e => e.XCm == markerX && e.ZCm == markerY).EntityId;
            return outpost != 0 && demon != 0;
        }, "the outpost and the demon in the snapshots");
        Run(client, () => { Idle(client); return client.RunStatuses.Count > 0; }, "the first run status");
        Assert.Equal(RunState.None, client.RunStatuses[^1].State);

        // The board: opened through the outpost interaction (gate, then the cached offers of this epoch).
        client.SendInteract(1, outpost, NpcInteraction.Quests);
        Run(client, () => { Idle(client); return client.QuestBoards.Count > 0; }, "the quest board");
        Assert.Equal(OutpostReason.None, Assert.Single(client.InteractResults).Reason);
        var board = client.QuestBoards[^1];
        Assert.Equal(6, board.Offers.Length);
        Assert.All(board.Offers, o => Assert.Equal((QuestKind.Kill, (ushort)demonDef.NetId, (ushort)1), (o.Kind, o.MonsterNetId, o.Count)));

        // A two-step run, rewards locked.
        client.SendQuestRun(2, outpost, QuestRunAction.Accept, board.Epoch, slots: [0, 1]);
        Run(client, () => { Idle(client); return client.RunStatuses[^1].State == RunState.Active; }, "the accepted run");
        Assert.Equal(OutpostReason.None, Assert.Single(client.QuestResults, r => r.RequestId == 2).Reason);
        Assert.Equal(2, client.RunStatuses[^1].Steps.Length);

        // One kill counts once: the first kill does the first step, the respawned demon's the second.
        Kill(client, demon, "the demon killed");
        Run(client, () => { Idle(client); return client.RunStatuses[^1].Steps[0].State == RunStepState.Done; }, "the first step done");
        Assert.Equal((RunState.Active, RunStepState.Active), (client.RunStatuses[^1].State, client.RunStatuses[^1].Steps[1].State));
        uint respawned = 0;
        int seen = client.Snapshots.Count;
        Run(client, () =>
        {
            Idle(client);
            // A new monster spawn on the first demon's marker (its loot drop lies there too; the field's other demon is older).
            for (; seen < client.Snapshots.Count && respawned == 0; seen++)
            {
                var snapshot = client.Snapshots[seen];
                foreach (var spawn in snapshot.Spawns)
                {
                    if (spawn.Kind == EntityKind.Monster && spawn.EntityId != demon
                        && snapshot.Entities.Any(e => e.EntityId == spawn.EntityId && e.XCm == markerX && e.ZCm == markerY))
                        respawned = spawn.EntityId;
                }
            }
            return respawned != 0;
        }, "the demon's respawn");
        Kill(client, respawned, "the respawned demon killed");
        Run(client, () => { Idle(client); return client.RunStatuses[^1].State == RunState.Completed; }, "the completed run");
        long rewards = board.Offers[0].RewardExp + board.Offers[1].RewardExp;
        long expected = rewards * 110 / 100; // two completed steps: +10 % (RunBonus)
        Assert.Equal(expected, client.RunStatuses[^1].ClaimExp);
        Run(client, () => { Idle(client); return client.Progressions.Count > 0 && client.Progressions[^1].Exp > 0; }, "the kill's exp");
        var beforeClaim = client.Progressions[^1];

        // The claim: answered once its save committed, the exp through ExpService.
        client.SendQuestRun(3, outpost, QuestRunAction.Claim);
        Run(client, () => { Idle(client); return client.QuestResults.Any(r => r.RequestId == 3); }, "the claim's answer");
        var claim = client.QuestResults.Single(r => r.RequestId == 3);
        Assert.Equal((OutpostReason.None, expected), (claim.Reason, claim.Exp));
        Run(client, () => { Idle(client); return client.Progressions[^1].Exp != beforeClaim.Exp || client.Progressions[^1].Level != beforeClaim.Level; },
            "the claimed exp");
        var afterClaim = client.Progressions[^1];
        Assert.Equal(beforeClaim.Level, afterClaim.Level); // the placeholder curve: 2 x 10 exp x 1.1 is no level-up
        Assert.Equal(beforeClaim.Exp + expected, afterClaim.Exp);
        Assert.Equal(RunState.None, client.RunStatuses[^1].State);

        // In the database: the claimed run, the accept and claim audit rows, the exp on the character (one transaction).
        var quests = new QuestRepository(_env.RealmDatabase);
        var run = Assert.Single(await quests.LoadAllAsync(character.CharacterId));
        Assert.Equal((RunState.Claimed, expected, 10), (run.State, run.ClaimedExp, run.BonusPercent));
        Assert.All(run.Steps, s => Assert.Equal((RunStepState.Done, 1), (s.State, s.Progress)));
        var audit = await quests.AuditAsync(character.CharacterId);
        Assert.Equal(new[] { (short)QuestRunAction.Accept, (short)QuestRunAction.Claim }, audit.Select(a => a.Action));
        Assert.Equal(expected, audit[1].Amount);
        Assert.StartsWith($"quest:{_server.ServerInstanceId}:", audit[1].OpKey);
        Assert.Equal(afterClaim.Exp, (await _env.Characters.LoadAsync(character.CharacterId))!.State.Exp);
    }

    /// <summary>Kill offers of count 1 only, the zone narrowed to the first demon, the outpost's reach widened to it.</summary>
    private static ServerConfig NarrowedConfig()
    {
        var tables = ServerTestEnvironment.LoadTables();
        Replace(tables, new OutpostQuestDef
        {
            Id = "kill", Kind = QuestKind.Kill, Weight = 1, CountMin = 1, CountMax = 1, ExpPercentPerUnit = 50,
        });
        var board = tables.Single<QuestBoardSettings>();
        Replace(tables, new QuestBoardSettings
        {
            OffersPerBoard = board.OffersPerBoard, RefreshMs = board.RefreshMs, RadiusCm = 2_000, IncludeBosses = false,
        });
        Assert.True(tables.TryGet(typeof(NpcDef), out var npcs));
        var widened = npcs.Rows.Cast<NpcDef>().Select(n => n.Id != "m1.outpost" ? n : new NpcDef
        {
            Id = n.Id, NetId = n.NetId, Kind = n.Kind, Interactions = n.Interactions, VisualId = n.VisualId, NameId = n.NameId,
            InteractRangeCm = 2_500,
        }).ToArray<object>();
        tables.Add(new ConfigTable(typeof(NpcDef), npcs.Version, widened));
        return ServerConfig.From(tables);
    }

    /// <summary>
    /// Presses the normal attack toward <paramref name="monster"/> until it dies (a respawned demon comes for the character, so
    /// the facing follows it).
    /// </summary>
    private void Kill(LoopbackGameClient client, uint monster, string what)
    {
        int frame = 0;
        ushort facing = 0; // the first demon stands at +Y of the character
        Run(client, () =>
        {
            frame++;
            var entities = client.Snapshots[^1].Entities;
            var self = entities.FirstOrDefault(e => e.EntityId == client.OwnEntityId);
            var target = entities.FirstOrDefault(e => e.EntityId == monster);
            if (self.EntityId != 0 && target.EntityId != 0 && (target.XCm != self.XCm || target.ZCm != self.ZCm))
                facing = BotClient.Brain.GridPath.FacingToward(target.XCm - self.XCm, target.ZCm - self.ZCm);
            var actions = frame % 15 == 1 ? InputActions.Attack : InputActions.None;
            client.SendFrame(MoveKeys.None, facing, _config.StartingValues.InputRedundancy, actions, ViewTick(client));
            return client.Hits.Any(h => h.Hit.TargetId == monster && h.Hit.Kind == HitKind.Killed);
        }, what);
    }

    private static void Replace<T>(ConfigSet set, params T[] rows) where T : class =>
        set.Add(new ConfigTable(typeof(T), 1, rows));

    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
    }

    /// <summary>Calls <paramref name="step"/> about once per server tick until it returns true (see <c>LootEndToEndTests</c>).</summary>
    private void Run(LoopbackGameClient client, Func<bool> step, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (watch.Elapsed >= Deadline || client.Disconnected)
            {
                var text = new System.Text.StringBuilder(client.Disconnected
                    ? $"The client was disconnected after {watch.Elapsed.TotalSeconds:F1} s waiting for {what}."
                    : $"Timed out after {watch.Elapsed.TotalSeconds:F1} s waiting for {what}.");
                text.Append($"\nServer tick {_server.World.ServerTick}");
                ServerTestEnvironment.AppendClients(text, [client]);
                Assert.Fail(text.ToString());
            }
            Assert.Null(_server.WorldThread.Failure);
            client.Poll();
            if (client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
