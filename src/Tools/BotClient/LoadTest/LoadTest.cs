using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using BotClient.Link;
using BotClient.Login;
using BotClient.Run;
using Shared.Data.Tables;
using Shared.Util;

namespace BotClient.LoadTest;

/// <summary>What <c>loadtest</c> needs besides the scenario row.</summary>
public sealed class LoadTestOptions
{
    public required string Repository { get; init; }
    public required string RunDirectory { get; init; }
    public required BotScenario Scenario { get; init; }

    /// <summary>Overrides of the scenario row (command line).</summary>
    public string? LinkId { get; init; }
    public int? Bots { get; init; }
    public int? DurationMs { get; init; }

    /// <summary>Built output folder of the servers: artifacts/bin/{GameServer,AuthApi}/&lt;configuration&gt;.</summary>
    public string Configuration { get; init; } = "debug";

    /// <summary>Npgsql settings without the database: host, port, user, password (the docker-compose dev values by default).</summary>
    public string PostgresBase { get; init; } = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev";

    /// <summary>Suffix of the dev databases, as DbMigrate --db-suffix makes them; empty for mmorpg_global / mmorpg_realm_1.</summary>
    public string DbSuffix { get; init; } = "";

    public string Redis { get; init; } = "127.0.0.1:6379";

    public long? AccountBase { get; init; }
    public ulong? Seed { get; init; }
}

/// <summary>One pass criterion of a run.</summary>
public sealed record Criterion(string Name, string Expected, string Actual, bool Pass);

/// <summary>
/// The bot load test (backlog J, spec 11.5), the same on CI (Linux) and locally (tools/load/Run-BotLoad.ps1): starts the
/// GameServer and the Auth API (dev login) from their built DLLs with secrets generated for this run (process
/// environment only) on free ports, runs the scenario's bots for its duration, stops everything, reads the server's
/// telemetry (per-tick <c>TickTiming</c> records) and checks the pass criteria. Writes <c>summary.json</c>, the server
/// logs and the telemetry into the run folder. The databases must exist and be migrated (DbMigrate) beforehand.
/// </summary>
public static class LoadTest
{
    private const int RealmId = 1;
    private static readonly TimeSpan ServerStartTimeout = TimeSpan.FromSeconds(60);

    public static int Run(LoadTestOptions options, Action<string> log)
    {
        var data = BotData.Load(options.Repository);
        var scenario = options.Scenario;
        var link = data.Link(options.LinkId ?? scenario.LinkId);
        int bots = options.Bots ?? scenario.Bots;
        int durationMs = options.DurationMs ?? scenario.DurationMs;
        ulong seed = options.Seed ?? (ulong)RandomNumberGenerator.GetInt32(1, int.MaxValue);
        // A fresh account range per run: no ownership lock or lingering session of an earlier run is in the way.
        long accountBase = options.AccountBase ?? 9_000_000_000L + RandomNumberGenerator.GetInt32(0, 1_000_000) * 1_000L;
        int tickRateHz = data.Tables.Single<StartingValuesM1>().TickRateHz;
        long periodUs = 1_000_000 / tickRateHz;

        Directory.CreateDirectory(options.RunDirectory);
        string runId = Path.GetFileName(Path.GetFullPath(options.RunDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string telemetry = Path.Combine(options.RunDirectory, "telemetry");
        Directory.CreateDirectory(telemetry);
        log($"loadtest {scenario.Id}: {bots} bots ({string.Join(", ", scenario.Classes)}), link {link.Id}, {durationMs / 1000} s, "
            + $"seed {seed}, accounts dev:{accountBase}+, run folder {options.RunDirectory}");

        int gamePort = ServerProcess.FreeUdpPort();
        int authPort = ServerProcess.FreeTcpPort();
        string authUrl = $"http://127.0.0.1:{authPort}";
        string suffix = options.DbSuffix.Length > 0 ? "_" + options.DbSuffix : "";
        var environment = new Dictionary<string, string>
        {
            ["MMORPG_TICKET_SECRET"] = NewSecret(),
            ["MMORPG_TICKET_KEY_ID"] = "1",
            ["MMORPG_REALM_ID"] = RealmId.ToString(),
            ["MMORPG_GAME_PORT"] = gamePort.ToString(),
            ["MMORPG_GLOBAL_DB"] = $"{options.PostgresBase};Database=mmorpg_global{suffix}",
            ["MMORPG_REALM_DB"] = $"{options.PostgresBase};Database=mmorpg_realm_1{suffix}",
            ["MMORPG_REDIS"] = options.Redis,
            // Own keys per run: nonces and ownership locks never meet another session's or an earlier run's.
            ["MMORPG_REDIS_PREFIX"] = $"mmorpg:load-{runId}:",
            ["MMORPG_DATA_DIR"] = Path.Combine(options.Repository, "data", "tables"),
            ["MMORPG_WORLD_DIR"] = Path.Combine(options.Repository, "data", "world", "m1"),
            ["MMORPG_TELEMETRY_DIR"] = telemetry,
            ["MMORPG_AUTH_TOKEN_SECRET"] = NewSecret(),
            ["MMORPG_AUTH_IDENTITY"] = "dev",
            ["MMORPG_AUTH_URLS"] = authUrl,
        };

        var criteria = new List<Criterion>();
        var watch = Stopwatch.StartNew();
        IClock clock = new SystemClock();
        long windowFromMs = 0, windowToMs = 0;
        List<BotResult> results = [];
        RelayStats relay = default;
        TickReport? ticks = null;
        string failure = "";
        ServerProcess? game = null, auth = null;
        try
        {
            string bin = Path.Combine(options.Repository, "artifacts", "bin");
            game = ServerProcess.Start("gameserver", Path.Combine(bin, "GameServer", options.Configuration, "GameServer.dll"),
                options.Repository, options.RunDirectory, environment, "GameServer running",
                line => line.StartsWith("ERROR", StringComparison.Ordinal) || line.Contains("World simulation stopped", StringComparison.Ordinal)
                        || line.Contains("Unhandled exception", StringComparison.Ordinal));
            auth = ServerProcess.Start("authapi", Path.Combine(bin, "AuthApi", options.Configuration, "AuthApi.dll"),
                options.Repository, options.RunDirectory, environment, "Now listening on",
                line => line.StartsWith("fail:", StringComparison.Ordinal) || line.StartsWith("crit:", StringComparison.Ordinal)
                        || line.Contains("Unhandled exception", StringComparison.Ordinal));
            if (!game.WaitReady(ServerStartTimeout))
                throw new InvalidOperationException($"GameServer did not start (exit {game.ExitCode}); see gameserver.log/.err");
            if (!auth.WaitReady(ServerStartTimeout))
                throw new InvalidOperationException($"Auth API did not start (exit {auth.ExitCode}); see authapi.log/.err");
            log($"servers up after {watch.Elapsed.TotalSeconds:0.0} s: game udp {gamePort}, auth {authUrl}");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var settings = new BotRunSettings
            {
                Bots = bots, Classes = scenario.Classes, Behaviour = data.Behaviour(scenario.BehaviourId), Link = link,
                RampPerSecond = scenario.RampPerSecond, AccountBase = accountBase, Seed = seed, RealmId = RealmId,
            };
            using var runner = new BotRunner(data, settings, new AuthApiTicketSource(new HttpClientAuthHttp(http), authUrl, RealmId),
                new IPEndPoint(IPAddress.Loopback, gamePort), log);
            runner.Start();
            bool allIn = runner.WaitAllEntered(TimeSpan.FromMilliseconds(scenario.EnterTimeoutMs), () => game.HasExited || auth.HasExited);
            log($"{runner.Entered}/{bots} bots in the world after {watch.Elapsed.TotalSeconds:0.0} s");
            criteria.Add(new Criterion("all bots entered the world", $"{bots} within {scenario.EnterTimeoutMs} ms",
                $"{runner.Entered} ({runner.LoginFailures} login failures)", allIn));

            if (allIn)
            {
                windowFromMs = clock.UtcNowMs;
                var playWatch = Stopwatch.StartNew();
                while (playWatch.ElapsedMilliseconds < durationMs && runner.Failure == null && !game.HasExited && !auth.HasExited)
                {
                    Thread.Sleep(1_000);
                    if (playWatch.ElapsedMilliseconds / 1_000 % 30 == 0)
                        log(Progress(runner, playWatch.Elapsed));
                }
                windowToMs = clock.UtcNowMs;
            }
            results = runner.Results();
            relay = runner.Relay.Stats();
            runner.StopBots();
            if (runner.Failure != null)
                failure = "bot loop failed: " + runner.Failure.Message;
        }
        catch (Exception e)
        {
            failure = e.Message;
            log("loadtest failed: " + e.Message);
        }
        finally
        {
            bool gameAlive = game is { HasExited: false }, authAlive = auth is { HasExited: false };
            foreach (var server in new[] { game, auth })
            {
                foreach (var line in server?.FirstErrors ?? [])
                    log($"{server!.Name}: {line}");
            }
            // Let the telemetry writer flush its last records (TelemetrySettings.flushIntervalMs) before the stop.
            Thread.Sleep(1_000);
            criteria.Add(new Criterion("servers still running at the end", "both", $"game {(gameAlive ? "up" : "down")}, auth {(authAlive ? "up" : "down")}",
                gameAlive && authAlive));
            criteria.Add(new Criterion("no error lines from the servers", "0",
                $"game {game?.ErrorLines ?? 0}, auth {auth?.ErrorLines ?? 0}", (game?.ErrorLines ?? 0) + (auth?.ErrorLines ?? 0) == 0));
            game?.Dispose();
            auth?.Dispose();
        }

        if (windowToMs > windowFromMs)
        {
            ticks = TickReport.Read(telemetry, windowFromMs, windowToMs);
            long budgetUs = periodUs * scenario.TickP99MaxPercentOfPeriod / 100;
            criteria.Add(new Criterion("tick p99 within budget", $"<= {budgetUs} us ({scenario.TickP99MaxPercentOfPeriod}% of {periodUs} us)",
                $"{ticks.P99Us} us over {ticks.Ticks} ticks", ticks.Ticks > 0 && ticks.P99Us <= budgetUs));
            criteria.Add(new Criterion("no world crash, security event or dropped telemetry", "0",
                $"crashed {ticks.WorldCrashed}, security {ticks.SecurityEvents}, queue dropped {ticks.QueueDropped}",
                ticks.WorldCrashed == 0 && ticks.SecurityEvents == 0 && ticks.QueueDropped == 0));
            criteria.Add(new Criterion("no malformed inputs", "0", ticks.MalformedInputs.ToString(), ticks.MalformedInputs == 0));
            criteria.Add(new Criterion("no unreliable message too large to send", "0", ticks.UnreliableTooLarge.ToString(),
                ticks.UnreliableTooLarge == 0));
        }
        if (results.Count > 0)
        {
            int disconnects = results.Count(r => r.Counters.Disconnected);
            criteria.Add(new Criterion("disconnects", $"<= {scenario.MaxDisconnects}", disconnects.ToString(), disconnects <= scenario.MaxDisconnects));
            int inCombat = results.Count(r => r.Counters.InCombat);
            int needed = (int)Math.Ceiling(bots * scenario.MinInCombatPercent / 100.0);
            criteria.Add(new Criterion("bots reached combat", $">= {needed} ({scenario.MinInCombatPercent}%)", inCombat.ToString(), inCombat >= needed));
        }
        if (failure.Length > 0)
            criteria.Add(new Criterion("harness", "no failure", failure, false));

        bool pass = criteria.Count > 0 && criteria.All(c => c.Pass);
        var summary = Summary(scenario.Id, link.Id, bots, durationMs, seed, accountBase, periodUs, watch.Elapsed, pass, criteria, ticks,
            relay, results);
        File.WriteAllText(Path.Combine(options.RunDirectory, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions));
        Print(log, criteria, ticks, results, relay, pass);
        return pass ? 0 : 1;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static string Progress(BotRunner runner, TimeSpan elapsed)
    {
        var results = runner.Results();
        return $"  {elapsed.TotalSeconds:0} s: in world {runner.Entered}, in combat {results.Count(r => r.Counters.InCombat)}, "
               + $"hits {results.Sum(r => r.Counters.HitsLanded)}, kills {results.Sum(r => r.Counters.Kills)}, "
               + $"deaths {results.Sum(r => r.Counters.Deaths)}, disconnects {results.Count(r => r.Counters.Disconnected)}";
    }

    private static object Summary(string scenario, string link, int bots, int durationMs, ulong seed, long accountBase, long periodUs,
        TimeSpan elapsed, bool pass, List<Criterion> criteria, TickReport? ticks, RelayStats relay, List<BotResult> results)
    {
        double minutes = Math.Max(1e-9, durationMs / 60_000.0);
        return new
        {
            scenario, link, bots, durationMs, seed, accountBase, tickPeriodUs = periodUs, elapsedSeconds = Math.Round(elapsed.TotalSeconds, 1),
            pass, criteria, ticks, relay = new
            {
                relay.Clients, relay.UpAdmitted, relay.UpDropped, relay.DownAdmitted, relay.DownDropped, relay.Sent, relay.MeanLateUs,
                relay.MaxLateUs, relay.SharedSourceAddress,
            },
            totals = new
            {
                inCombat = results.Count(r => r.Counters.InCombat),
                actionsStarted = results.Sum(r => r.Counters.ActionsStarted),
                hitsLanded = results.Sum(r => r.Counters.HitsLanded),
                kills = results.Sum(r => r.Counters.Kills),
                hitsTaken = results.Sum(r => r.Counters.HitsTaken),
                dodgedHits = results.Sum(r => r.Counters.DodgedHits),
                dodgesPressed = results.Sum(r => r.DodgesPressed),
                deaths = results.Sum(r => r.Counters.Deaths),
                pickupsOk = results.Sum(r => r.Counters.PickupsOk),
                pickupsRefused = results.Sum(r => r.Counters.PickupsRefused),
                corrections = results.Sum(r => r.Counters.Corrections),
                correctionsPerBotMinute = Math.Round(results.Sum(r => r.Counters.Corrections) / (double)Math.Max(1, results.Count) / minutes, 2),
                actionMismatches = results.Sum(r => r.Counters.ActionMismatches),
                unknownAcks = results.Sum(r => r.Counters.UnknownAcks),
                refusals = results.SelectMany(r => r.Counters.Refusals).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Sum(p => p.Value)),
            },
            botsDetail = results.Select(r => new
            {
                r.Index, r.Account, r.RequestedClass, r.ClassId, r.Character, r.LoginError, lastActivity = r.LastActivity.ToString(),
                r.Counters.EnteredWorld, r.Counters.InCombat, r.Counters.Disconnected, r.Counters.DisconnectReason,
                r.Counters.ActionsStarted, r.Counters.HitsLanded, r.Counters.Kills, r.Counters.Deaths, r.Counters.PickupsOk,
                r.Counters.Corrections, r.Counters.UnknownAcks, r.DodgesPressed, r.ActionsPressed, refusals = r.Counters.Refusals,
            }),
        };
    }

    private static void Print(Action<string> log, List<Criterion> criteria, TickReport? ticks, List<BotResult> results, RelayStats relay, bool pass)
    {
        if (ticks != null)
            log($"tick time over {ticks.Ticks} ticks: p50 {ticks.P50Us / 1000.0:0.00} ms, p95 {ticks.P95Us / 1000.0:0.00} ms, "
                + $"p99 {ticks.P99Us / 1000.0:0.00} ms, max {ticks.MaxUs / 1000.0:0.00} ms, overruns {ticks.Overruns}");
        if (results.Count > 0)
            log($"bots: in combat {results.Count(r => r.Counters.InCombat)}/{results.Count}, hits {results.Sum(r => r.Counters.HitsLanded)}, "
                + $"kills {results.Sum(r => r.Counters.Kills)}, deaths {results.Sum(r => r.Counters.Deaths)}, "
                + $"pickups {results.Sum(r => r.Counters.PickupsOk)}, corrections {results.Sum(r => r.Counters.Corrections)}");
        log($"relay: {relay.Clients} clients, sent {relay.Sent}, dropped {relay.UpDropped + relay.DownDropped}, "
            + $"late mean {relay.MeanLateUs} us max {relay.MaxLateUs} us{(relay.SharedSourceAddress ? ", SHARED source address" : "")}");
        foreach (var c in criteria)
            log($"  [{(c.Pass ? "PASS" : "FAIL")}] {c.Name}: expected {c.Expected}, got {c.Actual}");
        log(pass ? "loadtest PASSED" : "loadtest FAILED");
    }
}

/// <summary>Wall-clock UTC for the harness (not simulation code).</summary>
public sealed class SystemClock : IClock
{
    public long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
