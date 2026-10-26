// BotClient (backlog J, spec 11.5): headless bots that play through the Unity client's own engine-free code, a link
// simulator (latency, jitter, loss below LiteNetLib), and the bot load test. Commands and examples: README.md.

using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using BotClient;
using BotClient.Link;
using BotClient.LoadTest;
using BotClient.Login;
using BotClient.Run;

if (args.Length == 0)
    return Usage();

var options = Options.Parse(args.Skip(1));
if (options == null)
    return Usage();
void Log(string line) => Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {line}");

// Windows wakes sleeping threads every 15.6 ms by default; the bot loop and the relay pump need about 1 ms.
using var timer = WindowsTimerResolution.Raise();

try
{
    return args[0] switch
    {
        "loadtest" => LoadTestCommand(options),
        "run" => RunCommand(options),
        "relay" => RelayCommand(options),
        "report" => ReportCommand(options),
        _ => Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
{
    Console.Error.WriteLine("ERROR " + e.Message);
    return 2;
}

int LoadTestCommand(Options o)
{
    string repository = BotData.FindRepository(o.Get("repo"));
    var data = BotData.Load(repository);
    var scenario = data.Scenario(o.Get("scenario") ?? "ci-smoke");
    string runDirectory = Path.GetFullPath(o.Get("run-dir")
        ?? Path.Combine(repository, "artifacts", "load", DateTime.UtcNow.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + "-" + scenario.Id));
    return LoadTest.Run(new LoadTestOptions
    {
        Repository = repository,
        RunDirectory = runDirectory,
        Scenario = scenario,
        LinkId = o.Get("link"),
        Bots = o.Int("bots"),
        DurationMs = o.Int("duration-ms"),
        Configuration = o.Get("configuration") ?? "debug",
        PostgresBase = o.Get("pg") ?? "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev",
        DbSuffix = o.Get("db-suffix") ?? "",
        Redis = o.Get("redis") ?? "127.0.0.1:6379",
        AccountBase = o.Long("account-base"),
        Seed = o.Long("seed") is long seed ? (ulong)seed : null,
    }, Log);
}

int RunCommand(Options o)
{
    string repository = BotData.FindRepository(o.Get("repo"));
    var data = BotData.Load(repository);
    var scenario = data.Scenario(o.Get("scenario") ?? "ci-smoke");
    var game = ParseEndPoint(o.Get("game") ?? "127.0.0.1:7777");
    string authUrl = o.Get("auth-url") ?? "http://127.0.0.1:5080";
    int durationMs = o.Int("duration-ms") ?? scenario.DurationMs;
    var settings = new BotRunSettings
    {
        Bots = o.Int("bots") ?? scenario.Bots,
        Classes = o.Get("class") is { } classId ? [classId] : scenario.Classes,
        Behaviour = data.Behaviour(o.Get("behaviour") ?? scenario.BehaviourId),
        Link = data.Link(o.Get("link") ?? scenario.LinkId),
        RampPerSecond = scenario.RampPerSecond,
        AccountBase = o.Long("account-base") ?? 9_000_000_000L + System.Security.Cryptography.RandomNumberGenerator.GetInt32(0, 1_000_000) * 1_000L,
        Seed = o.Long("seed") is long seed ? (ulong)seed : (ulong)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue),
        RealmId = o.Int("realm") ?? 1,
    };
    Log($"run: {settings.Bots} bots, link {settings.Link.Id}, seed {settings.Seed}, accounts dev:{settings.AccountBase}+, auth {authUrl}, game {game}");
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    using var runner = new BotRunner(data, settings, new AuthApiTicketSource(new HttpClientAuthHttp(http), authUrl, settings.RealmId), game, Log);
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Set();
    };
    runner.Start();
    bool allIn = runner.WaitAllEntered(TimeSpan.FromMilliseconds(scenario.EnterTimeoutMs));
    Log($"{runner.Entered}/{settings.Bots} bots in the world");
    if (allIn)
        stop.Wait(durationMs);
    var results = runner.Results();
    runner.StopBots();
    foreach (var r in results)
        Log($"bot {r.Index} {r.ClassId} {r.Character}: {(r.LoginError.Length > 0 ? "login " + r.LoginError : r.LastActivity.ToString())}, "
            + $"combat {r.Counters.InCombat}, hits {r.Counters.HitsLanded}, kills {r.Counters.Kills}, deaths {r.Counters.Deaths}, "
            + $"pickups {r.Counters.PickupsOk}, corrections {r.Counters.Corrections}{(r.Counters.Disconnected ? ", LOST " + r.Counters.DisconnectReason : "")}");
    return allIn && results.All(r => !r.Counters.Disconnected) ? 0 : 1;
}

int RelayCommand(Options o)
{
    string repository = BotData.FindRepository(o.Get("repo"));
    var data = BotData.Load(repository);
    var link = data.Link(o.Get("link") ?? "rtt100");
    var listen = new IPEndPoint(IPAddress.Loopback, o.Int("listen") ?? 7778);
    var server = ParseEndPoint(o.Get("server") ?? "127.0.0.1:7777");
    ulong seed = o.Long("seed") is long s ? (ulong)s : (ulong)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
    using var relay = new UdpLinkRelay(listen, server, link, seed, log: Log);
    using var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Set();
    };
    relay.Start();
    Log($"relay {relay.LocalEndPoint} -> {server}, link {link.Id} (up {link.UpDelayMs} ms, down {link.DownDelayMs} ms, "
        + $"jitter {link.JitterMs} ms, loss {link.LossPercent}%), seed {seed}. Ctrl+C stops.");
    while (!stop.Wait(TimeSpan.FromSeconds(10)))
    {
        var stats = relay.Stats();
        Log($"clients {stats.Clients}, sent {stats.Sent}, dropped {stats.UpDropped + stats.DownDropped}, late mean {stats.MeanLateUs} us max {stats.MaxLateUs} us");
    }
    return 0;
}

int ReportCommand(Options o)
{
    var directory = o.Get("telemetry") ?? throw new ArgumentException("report needs --telemetry <dir>");
    var report = TickReport.Read(directory, o.Long("from-ms") ?? long.MinValue, o.Long("to-ms") ?? long.MaxValue);
    Log($"{report.Ticks} ticks: p50 {report.P50Us / 1000.0:0.00} ms, p95 {report.P95Us / 1000.0:0.00} ms, p99 {report.P99Us / 1000.0:0.00} ms, "
        + $"max {report.MaxUs / 1000.0:0.00} ms, max late {report.MaxLateUs / 1000.0:0.00} ms, overruns {report.Overruns}, "
        + $"crashes {report.WorldCrashed}, security {report.SecurityEvents}, queue dropped {report.QueueDropped}");
    return report.Ticks > 0 ? 0 : 1;
}

static IPEndPoint ParseEndPoint(string text) =>
    IPEndPoint.TryParse(text, out var endPoint) ? endPoint : throw new ArgumentException($"'{text}' is not host:port (an IP address).");

static int Usage()
{
    Console.Error.WriteLine("""
        BotClient (backlog J): bots, link simulation and the bot load test. See src/Tools/BotClient/README.md.
          loadtest [--scenario ci-smoke] [--run-dir <dir>] [--link <BotLinkProfile>] [--bots <n>] [--duration-ms <ms>]
                   [--db-suffix <s>] [--pg <npgsql base>] [--redis <host:port>] [--configuration debug] [--seed <n>] [--account-base <n>]
          run      [--scenario <id>] [--auth-url http://127.0.0.1:5080] [--game 127.0.0.1:7777] [--bots <n>] [--class <id>]
                   [--link <id>] [--duration-ms <ms>] [--seed <n>] [--account-base <n>]
          relay    [--listen 7778] [--server 127.0.0.1:7777] [--link rtt100] [--seed <n>]
          report   --telemetry <dir> [--from-ms <utc ms>] [--to-ms <utc ms>]
        """);
    return 2;
}

/// <summary><c>--name value</c> pairs.</summary>
internal sealed class Options
{
    private readonly Dictionary<string, string> _values;

    private Options(Dictionary<string, string> values)
    {
        _values = values;
    }

    public static Options? Parse(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = args.ToList();
        for (int i = 0; i < list.Count; i += 2)
        {
            if (!list[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= list.Count)
                return null;
            values[list[i][2..]] = list[i + 1];
        }
        return new Options(values);
    }

    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    public int? Int(string name) => Get(name) is { } text
        ? int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : throw new ArgumentException($"--{name} needs a number")
        : null;

    public long? Long(string name) => Get(name) is { } text
        ? long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : throw new ArgumentException($"--{name} needs a number")
        : null;
}

/// <summary>Raises the Windows timer resolution to 1 ms for this process while alive (timeBeginPeriod); nothing elsewhere.</summary>
internal sealed class WindowsTimerResolution : IDisposable
{
    private readonly bool _raised;

    private WindowsTimerResolution(bool raised)
    {
        _raised = raised;
    }

    public static WindowsTimerResolution Raise() => new(OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0);

    public void Dispose()
    {
        if (_raised)
            TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);
}
