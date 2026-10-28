using System.Diagnostics;
using System.Net;
using BotClient;
using BotClient.LoadTest;
using BotClient.Login;
using BotClient.Run;
using GameServer.Host;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Util;
using Xunit.Abstractions;

namespace GameServer.Tests.Bots;

/// <summary>
/// Backlog J end to end: BotClient bots (the Unity client's engine-free code driven by the bot brain) against the full
/// in-process server (sessions, PostgreSQL, Redis, the M1 world, monsters) over real LiteNetLib through the link relay
/// at 100 ms round trip. Tickets come straight from the test environment instead of the Auth API.
/// </summary>
public sealed class BotBehaviourTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(60);

    private ServerTestEnvironment _env = null!;
    private ServerConfig _config = null!;
    private Bootstrap _server = null!;

    public async Task InitializeAsync()
    {
        _env = await ServerTestEnvironment.CreateAsync();
        _config = ServerTestEnvironment.LoadConfig();
        _server = _env.StartServer(_config);
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public void Bots_of_three_classes_walk_out_and_fight_at_100_ms_without_losing_the_connection()
    {
        var data = BotData.Load(RepositoryPaths.Root());
        var source = data.Behaviour("field-combat");
        var behaviour = Copy(source, homes: ["m1.spawn.combat.a"]); // the demon nearest the city gate
        var settings = new BotRunSettings
        {
            Bots = 3, Classes = ["warrior", "archer", "mage"], Behaviour = behaviour, Link = data.Link("rtt100"), RampPerSecond = 10,
            AccountBase = 61_000, Seed = 7, RealmId = ServerTestEnvironment.Realm,
        };
        using var runner = new BotRunner(data, settings, new TestTickets(_env, _config),
            new IPEndPoint(IPAddress.Loopback, _server.Transport.LocalPort));
        runner.Start();
        Assert.True(runner.WaitAllEntered(TimeSpan.FromSeconds(15)), $"{runner.Entered}/3 entered, {runner.LoginFailures} login failures");

        var watch = Stopwatch.StartNew();
        while (!runner.Results().All(r => r.Counters.InCombat && r.Counters.HitsLanded > 0))
        {
            Assert.Null(_server.WorldThread.Failure);
            Assert.Null(runner.Failure);
            Assert.True(watch.Elapsed < PlayTimeout, "bots did not all fight: " + Describe(runner));
            Thread.Sleep(100);
        }
        var results = runner.Results();
        runner.StopBots();

        Assert.All(results, r =>
        {
            Assert.False(r.Counters.Disconnected, $"bot {r.Index} lost its connection: {r.Counters.DisconnectReason}");
            Assert.Equal(0, r.Counters.UnknownAcks);
            Assert.Equal(r.RequestedClass, r.ClassId); // the kit came from the own spawn's class
            Assert.True(r.Counters.ActionsStarted > 0);
        });
        Assert.Equal(3, runner.Relay.Stats().Clients);

        // The per-tick timing log (TelemetrySettings.tickTimingLog, on in the tables) is what the load test reads.
        Thread.Sleep(500); // the writer flushes every few hundred ms at most
        var ticks = TickReport.Read(_env.TelemetryDirectoryOf(_server));
        Assert.True(ticks.Ticks > 100, $"{ticks.Ticks} TickTiming records");
        Assert.Equal(0, ticks.MalformedInputs);
    }

    [Fact]
    public void A_bot_plays_a_round_of_the_m2_loop_and_presses_through_the_attack_chain()
    {
        // WP-M2-12 (spec 11.5): fight until a kill (with hits 2 to 4 of the chain, R-3.1-10), pick up, then the M2 steps play
        // (every step of the loop is real since WP-M2-5 and WP-M2-6) and the next round begins.
        // No dodges: the bot opens with its skills and starts the chain once the stamina pays all of it, before a skill is
        // ready again (BotBrainTests). A dodge spends 20 stamina whenever the live demon happens to attack, which can push
        // the chain past the kill; the bot then backs off and dies before the next fight, and the chain misses the budget.
        // A demon with half again its HP (its own server): the warrior's four opening skills deal about 180-200 at level 1
        // (230 with a crit) and hit 1 about 20-30, so the repository's 200 HP demon died to the opening or to hit 1 in every
        // fight measured (16 of 16 over 5 minutes); the chain hits the tests saw then started after the kill. At 300 HP the
        // chain starts on a living demon and its hits kill it. Full HP keeps that fight above backOffBelowPercent.
        var config = ConfigWithTougherDemon();
        var server = _env.StartServer(config);
        var data = BotData.Load(RepositoryPaths.Root());
        var behaviour = Copy(data.Behaviour("m2-loop"), homes: ["m1.spawn.combat.a"], dodgeChancePercent: 0);
        var settings = new BotRunSettings
        {
            Bots = 1, Classes = ["warrior"], Behaviour = behaviour, Link = data.Link("lan"), RampPerSecond = 10,
            AccountBase = 62_000, Seed = 5, RealmId = ServerTestEnvironment.Realm,
        };
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var runner = new BotRunner(data, settings, new TestTickets(_env, config, hp: int.MaxValue),
            new IPEndPoint(IPAddress.Loopback, server.Transport.LocalPort), log.Enqueue);
        runner.Start();
        Assert.True(runner.WaitAllEntered(TimeSpan.FromSeconds(15)), $"{runner.Entered}/1 entered, {runner.LoginFailures} login failures");

        var watch = Stopwatch.StartNew();
        while (!runner.Results().All(r => r.Counters.LoopRounds >= 1))
        {
            Assert.Null(server.WorldThread.Failure);
            Assert.Null(runner.Failure);
            Assert.True(watch.Elapsed < LoopTimeout, "no loop round: " + Describe(runner));
            Thread.Sleep(100);
        }
        var result = runner.Results().Single();
        runner.StopBots();

        Assert.False(result.Counters.Disconnected, result.Counters.DisconnectReason);
        Assert.True(result.Counters.Kills >= 1);
        Assert.True(result.Counters.ChainHitsBeforeFirstKill > 0, "no chain hit on the first, living demon: " + Describe(runner));
        Assert.True(result.Counters.Loop["fight.done"] >= result.Counters.LoopRounds);
        Assert.True(result.Counters.Loop["pickup.done"] + result.Counters.Loop.GetValueOrDefault("pickup.timedOut") >= 1);
        // QA M2 wave 0 F2: sell, buy, rankUp and equip play the protocol (WP-M2-4, WP-M2-3, WP-M2-1) from the client's kept
        // state, which has the enter-world messages sent before the loop existed (bag, gold, progression).
        var loop = result.Counters.Loop;
        output.WriteLine(string.Join(", ", loop.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}")) + $"; gold {result.Gold}");
        Assert.DoesNotContain(log, l => l.Contains("not available", StringComparison.Ordinal)); // no placeholder step is left
        // WP-M2-6: the run step reads the run from the RunStatus kept since enter world and, without one, accepts a run of kill
        // offers at the outpost (the fights of the next rounds complete it).
        Assert.True(loop.GetValueOrDefault("run.done") >= 1 && loop.GetValueOrDefault("run.accepted") >= 1,
            "run: " + string.Join(", ", loop.Select(c => $"{c.Key} {c.Value}")));
        foreach (var step in new[] { "sell", "buy", "rankUp", "equip", "learn" })
            Assert.True(loop.GetValueOrDefault(step + ".done") >= 1, $"{step}: " + Describe(runner) + " " + string.Join(", ", loop.Select(c => $"{c.Key} {c.Value}")));
        Assert.True(loop.GetValueOrDefault("sell.units") > 0 || loop.GetValueOrDefault("sell.nothingToSell") > 0, "neither a sale nor nothing to sell");
        if (loop.GetValueOrDefault("sell.units") > 0)
            Assert.True(result.Gold > 0, $"gold {result.Gold} after selling for {loop["sell.gold"]}");
        Assert.NotNull(result.Gold); // GoldBalance at enter world, before the loop (OwnGoldReader)
        Assert.Equal(1, loop.GetValueOrDefault("rankUp.notMet")); // asked once at level 1, rank F (60 kills needed, R-3.6-04)
        // WP-M2-5: the level-1 warrior has its kit (the test character) and 1 point: it learns Increase Def, the one row-0 node left.
        Assert.Equal(1, loop.GetValueOrDefault("learn.skills"));
        Assert.True(result.Level >= 1, $"level {result.Level}"); // the own level from ProgressionState (WP-M2-3)
    }

    /// <summary>
    /// From the tables, at full HP: walk from the city spawn to home a (about 33 m at moveSpeedCmPerSec 400: ~8 s), the
    /// opening skills and then the chain once the stamina pays it (~6 s, BotBrainTests), its hits killing the tougher demon
    /// (~2 s), pickup (loopPickupIdleMs 1.5 s), the placeholder steps at once: ~18 s, no back-off and so no recovery wait
    /// (backOffRecoverMs) on the way. The rest is room for a loaded CI machine. No retry.
    /// </summary>
    private static readonly TimeSpan LoopTimeout = TimeSpan.FromSeconds(90);

    /// <summary>The repository's tables with the demon at half again its HP (the m2-loop test's first fight).</summary>
    private static ServerConfig ConfigWithTougherDemon()
    {
        var tables = ServerTestEnvironment.LoadTables();
        Assert.True(tables.TryGet(typeof(MonsterDef), out var monsters));
        var demon = monsters.Rows.Cast<MonsterDef>().Single(m => m.Id == "m1.demon");
        demon.MaxHp = demon.MaxHp * 3 / 2;
        return ServerConfig.From(tables);
    }

    private static string Describe(BotRunner runner) => string.Join("; ", runner.Results().Select(r =>
        $"{r.Index} {r.ClassId} {r.LastActivity} combat {r.Counters.InCombat} hits {r.Counters.HitsLanded} deaths {r.Counters.Deaths} "
        + $"kills {r.Counters.Kills} chain {r.ChainPresses}/{r.Counters.ChainHitsStarted} ({r.Counters.ChainHitsBeforeFirstKill} before the first kill) step '{r.LoopStep}' rounds {r.Counters.LoopRounds}"));

    private static BotBehaviour Copy(BotBehaviour source, string[] homes, int? dodgeChancePercent = null) => new()
    {
        Id = source.Id + ".test", HomeMarkerIds = homes, TargetSearchCm = source.TargetSearchCm,
        ApproachReachPercent = source.ApproachReachPercent, ApproachBodyMarginCm = source.ApproachBodyMarginCm,
        HealBelowPercent = source.HealBelowPercent,
        BackOffBelowPercent = source.BackOffBelowPercent, BackOffDistanceCm = source.BackOffDistanceCm, BackOffMs = source.BackOffMs,
        ReengageAbovePercent = source.ReengageAbovePercent, BackOffRecoverMs = source.BackOffRecoverMs,
        DodgeChancePercent = dodgeChancePercent ?? source.DodgeChancePercent, ReactionMinMs = source.ReactionMinMs,
        ReactionMaxMs = source.ReactionMaxMs,
        PickupSearchCm = source.PickupSearchCm, PickupReachPercent = source.PickupReachPercent, LootAfterKillMs = source.LootAfterKillMs,
        StuckTimeoutMs = source.StuckTimeoutMs, StuckMinMoveCm = source.StuckMinMoveCm, PressAttackChain = source.PressAttackChain,
        LoopSteps = source.LoopSteps, LoopKillsPerRound = source.LoopKillsPerRound, LoopPickupIdleMs = source.LoopPickupIdleMs,
        LoopStepTimeoutMs = source.LoopStepTimeoutMs, LoopSellQuoteRetries = source.LoopSellQuoteRetries,
        LoopBuyItemId = source.LoopBuyItemId, LoopBuyKeep = source.LoopBuyKeep,
    };

    /// <summary>
    /// A character on the city spawn and a ticket signed with the test secret, as the Auth API would hand out;
    /// <paramref name="hp"/> above the class's max HP gives full HP (clamped when read).
    /// </summary>
    private sealed class TestTickets(ServerTestEnvironment env, ServerConfig config, int hp = 100) : IBotTicketSource
    {
        public async Task<BotTicket> GetAsync(long account, string classId, IRng rng)
        {
            var character = await env.CreateCharacterAsync(accountId: account, classId: classId, hp: hp);
            var issued = env.Ticket(config, account);
            return new BotTicket(account, character.CharacterId, character.Name, issued.Ticket, issued.SessionKey);
        }
    }
}
