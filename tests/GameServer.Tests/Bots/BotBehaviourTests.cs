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

namespace GameServer.Tests.Bots;

/// <summary>
/// Backlog J end to end: BotClient bots (the Unity client's engine-free code driven by the bot brain) against the full
/// in-process server (sessions, PostgreSQL, Redis, the M1 world, monsters) over real LiteNetLib through the link relay
/// at 100 ms round trip. Tickets come straight from the test environment instead of the Auth API.
/// </summary>
public sealed class BotBehaviourTests : IAsyncLifetime
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

    private static string Describe(BotRunner runner) => string.Join("; ", runner.Results().Select(r =>
        $"{r.Index} {r.ClassId} {r.LastActivity} combat {r.Counters.InCombat} hits {r.Counters.HitsLanded} deaths {r.Counters.Deaths}"));

    private static BotBehaviour Copy(BotBehaviour source, string[] homes) => new()
    {
        Id = source.Id + ".test", HomeMarkerIds = homes, TargetSearchCm = source.TargetSearchCm,
        ApproachReachPercent = source.ApproachReachPercent, HealBelowPercent = source.HealBelowPercent,
        BackOffBelowPercent = source.BackOffBelowPercent, BackOffDistanceCm = source.BackOffDistanceCm, BackOffMs = source.BackOffMs,
        DodgeChancePercent = source.DodgeChancePercent, ReactionMinMs = source.ReactionMinMs, ReactionMaxMs = source.ReactionMaxMs,
        PickupSearchCm = source.PickupSearchCm, PickupReachPercent = source.PickupReachPercent, LootAfterKillMs = source.LootAfterKillMs,
        StuckTimeoutMs = source.StuckTimeoutMs, StuckMinMoveCm = source.StuckMinMoveCm,
    };

    /// <summary>A character on the city spawn and a ticket signed with the test secret, as the Auth API would hand out.</summary>
    private sealed class TestTickets(ServerTestEnvironment env, ServerConfig config) : IBotTicketSource
    {
        public async Task<BotTicket> GetAsync(long account, string classId, IRng rng)
        {
            var character = await env.CreateCharacterAsync(accountId: account, classId: classId);
            var issued = env.Ticket(config, account);
            return new BotTicket(account, character.CharacterId, character.Name, issued.Ticket, issued.SessionKey);
        }
    }
}
