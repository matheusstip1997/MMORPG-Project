using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using BotClient.Brain;
using BotClient.Link;
using BotClient.Loop;
using BotClient.Login;
using BotClient.World;
using Shared.Data.Tables;
using Shared.Simulation.Actions;
using Shared.Util;

namespace BotClient.Run;

/// <summary>What a run of bots plays: how many, which classes, how they behave, over which link.</summary>
public sealed class BotRunSettings
{
    public required int Bots { get; init; }
    public required IReadOnlyList<string> Classes { get; init; }
    public required BotBehaviour Behaviour { get; init; }
    public required BotLinkProfile Link { get; init; }
    public required int RampPerSecond { get; init; }

    /// <summary>Bot i logs in as <c>dev:(AccountBase + i)</c>.</summary>
    public required long AccountBase { get; init; }

    /// <summary>All random choices of the run (relay, names, brains) derive from it (architecture rule 6: logged).</summary>
    public required ulong Seed { get; init; }

    public required int RealmId { get; init; }
}

/// <summary>One bot's outcome.</summary>
public sealed record BotResult(int Index, long Account, string RequestedClass, string ClassId, string Character, string LoginError,
    BotCounters Counters, BotActivity LastActivity, long DodgesPressed, long ActionsPressed, long PickupsAsked,
    long StuckReplans = 0, long ChainPresses = 0, int? Level = null, string LoopStep = "", long? Gold = null);

/// <summary>
/// Runs N bots in one process (backlog J): logins through the Auth API on the thread pool at the ramp rate, then one
/// simulation thread drives every bot (poll, tick clock, think, predict, send), as a game client's main loop would. Every
/// bot connects through one <see cref="UdpLinkRelay"/> with the run's link profile, which also gives each bot its own
/// loopback source address. Each bot owns a LiteNetLib manager (a few threads each): fine for tens of bots; hundreds
/// need a shared manager (TODO(J): BotClient v2).
/// </summary>
public sealed class BotRunner : IDisposable
{
    private readonly BotData _data;
    private readonly BotRunSettings _settings;
    private readonly IBotTicketSource _tickets;
    private readonly Action<string> _log;
    private readonly UdpLinkRelay _relay;
    private readonly List<BotWorldClient> _bots = [];
    private readonly string[] _loginErrors;
    private readonly string[] _characters;
    private readonly ConcurrentQueue<(int Index, BotTicket Ticket)> _connects = new();
    private readonly ActionCatalog _catalog;
    private readonly Dictionary<int, int> _monsterAttackReach;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private volatile bool _stopBots;
    private volatile int _entered;
    private int _loginsDone;
    private volatile bool _threadDone;
    private Exception? _failure;

    public BotRunner(BotData data, BotRunSettings settings, IBotTicketSource tickets, IPEndPoint gameServer, Action<string>? log = null)
    {
        _data = data;
        _settings = settings;
        _tickets = tickets;
        _log = log ?? (_ => { });
        _loginErrors = new string[settings.Bots];
        _characters = new string[settings.Bots];
        _catalog = ActionCatalog.From(data.Tables);
        _monsterAttackReach = new Dictionary<int, int>();
        foreach (var monster in data.Rows<MonsterDef>())
        {
            foreach (var attack in monster.Attacks)
            {
                var def = ActionDef.Find(data.Tables, attack);
                if (def != null)
                    _monsterAttackReach[def.NetId] = BotKit.ReachCm(data.Tables, attack);
            }
        }
        _relay = new UdpLinkRelay(new IPEndPoint(IPAddress.Loopback, 0), gameServer, settings.Link, settings.Seed, log: _log);
        var steps = BotStepRegistry.Default(); // one per run: placeholders log "not available" once
        for (int i = 0; i < settings.Bots; i++)
        {
            int index = i;
            _bots.Add(new BotWorldClient(i, data, _catalog, classId => NewBrain(index, classId), steps, _log));
        }
        _thread = new Thread(Loop) { IsBackground = true, Name = "bots" };
    }

    public int Entered => _entered;

    public int LoginsDone => Volatile.Read(ref _loginsDone);

    public int LoginFailures => _loginErrors.Count(e => !string.IsNullOrEmpty(e));

    public UdpLinkRelay Relay => _relay;

    /// <summary>The simulation thread's exception, if it died.</summary>
    public Exception? Failure => _failure;

    public void Start()
    {
        _relay.Start();
        _thread.Start();
        _ = Task.Run(LoginAllAsync);
    }

    /// <summary>Waits until every bot is in the world, a login failed, <paramref name="abort"/> says so (a server died), or the time is up.</summary>
    public bool WaitAllEntered(TimeSpan timeout, Func<bool>? abort = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (_entered == _settings.Bots)
                return true;
            if (LoginFailures > 0 || _failure != null || (abort?.Invoke() ?? false))
                return false;
            Thread.Sleep(50);
        }
        return _entered == _settings.Bots;
    }

    /// <summary>Disconnects every bot (not counted as lost), then stops the loop.</summary>
    public void StopBots()
    {
        _stopBots = true;
        var watch = Stopwatch.StartNew();
        while (!_threadDone && watch.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(20);
    }

    public List<BotResult> Results()
    {
        var results = new List<BotResult>();
        for (int i = 0; i < _bots.Count; i++)
        {
            var bot = _bots[i];
            results.Add(new BotResult(i, _settings.AccountBase + i, ClassOf(i), bot.ClassId, _characters[i] ?? "",
                _loginErrors[i] ?? "", bot.Counters, bot.Brain?.Activity ?? BotActivity.Waiting, bot.Brain?.DodgesPressed ?? 0,
                bot.Brain?.ActionsPressed ?? 0, bot.Brain?.PickupsAsked ?? 0, bot.Brain?.StuckReplans ?? 0,
                bot.Brain?.ChainPresses ?? 0, bot.Own.Level, bot.Loop?.Current?.Id ?? "", bot.Own.Gold));
        }
        return results;
    }

    /// <summary>Each bot's level as its client knows it (null: unknown), for the level report (thread safe).</summary>
    public int?[] Levels() => _bots.Select(b => b.Own.Level).ToArray();

    public void Dispose()
    {
        _stopBots = true;
        _stop.Cancel();
        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(5));
        foreach (var bot in _bots)
            bot.Dispose();
        _relay.Dispose();
        _stop.Dispose();
    }

    private string ClassOf(int index) => _settings.Classes[index % _settings.Classes.Count];

    private BotBrain NewBrain(int index, string classId)
    {
        var homes = _settings.Behaviour.HomeMarkerIds;
        var home = _data.Markers[homes[index % homes.Length]];
        int pickupRange = _data.Tables.Single<LootSettings>().PickupRangeCm;
        // Brains draw from their own seed (relay streams are 0..2N-1 of the run seed), one stream per bot.
        var rng = new Pcg32Rng(_settings.Seed ^ 0x9E37_79B9_7F4A_7C15UL, (ulong)index);
        return new BotBrain(BotKit.For(_data.Tables, classId), _settings.Behaviour, _data.Grid, home, pickupRange, _monsterAttackReach, rng,
            ContactCm(classId));
    }

    /// <summary>R-2.2-07: where each monster's body stops a bot of <paramref name="classId"/> (radii added, less the tolerance).</summary>
    private Dictionary<int, int> ContactCm(string classId)
    {
        var contact = new Dictionary<int, int>();
        var settings = _data.Rows<BodyCollisionSettings>().FirstOrDefault();
        var classDef = _data.Rows<ClassDef>().FirstOrDefault(c => c.Id == classId);
        if (settings == null || classDef == null)
            return contact;
        foreach (var monster in _data.Rows<MonsterDef>())
            contact[monster.NetId] = classDef.BodyRadiusCm + monster.BodyRadiusCm - settings.OverlapToleranceCm;
        return contact;
    }

    private async Task LoginAllAsync()
    {
        var logins = new List<Task>();
        var interval = TimeSpan.FromSeconds(1.0 / _settings.RampPerSecond);
        for (int i = 0; i < _settings.Bots && !_stop.IsCancellationRequested; i++)
        {
            logins.Add(LoginAsync(i));
            await Task.Delay(interval).ConfigureAwait(false);
        }
        await Task.WhenAll(logins).ConfigureAwait(false);
    }

    private async Task LoginAsync(int index)
    {
        var rng = new Pcg32Rng(_settings.Seed + 1, (ulong)index); // names
        try
        {
            var ticket = await _tickets.GetAsync(_settings.AccountBase + index, ClassOf(index), rng)
                .ConfigureAwait(false);
            _characters[index] = ticket.CharacterName;
            _connects.Enqueue((index, ticket));
        }
        catch (Exception e)
        {
            _loginErrors[index] = e is BotLoginException login ? $"{login.Step}: {login.Reason}" : e.GetType().Name + ": " + e.Message;
            _log($"bot {index}: login failed: {_loginErrors[index]}");
        }
        finally
        {
            Interlocked.Increment(ref _loginsDone);
        }
    }

    private void Loop()
    {
        try
        {
            var relayPort = _relay.LocalEndPoint.Port;
            var watch = Stopwatch.StartNew();
            double last = 0;
            bool stopped = false;
            double stoppedAt = 0;
            while (!_stop.IsCancellationRequested)
            {
                while (_connects.TryDequeue(out var connect))
                {
                    var bot = _bots[connect.Index];
                    bot.CharacterName = connect.Ticket.CharacterName;
                    bot.Connection.Connect(IPAddress.Loopback.ToString(), relayPort, connect.Ticket.Ticket, connect.Ticket.SessionKey);
                    Array.Clear(connect.Ticket.SessionKey); // only derived keys are kept (ClientConnection.Connect)
                }

                double now = watch.Elapsed.TotalMilliseconds;
                double delta = now - last;
                last = now;
                int entered = 0;
                foreach (var bot in _bots)
                {
                    bot.Update(now, delta);
                    if (bot.Counters.EnteredWorld)
                        entered++;
                }
                _entered = entered;

                if (_stopBots && !stopped)
                {
                    foreach (var bot in _bots)
                        bot.Stop();
                    stopped = true;
                    stoppedAt = now;
                }
                if (stopped && now - stoppedAt > 500)
                    break; // the disconnects went out
                Thread.Sleep(1);
            }
        }
        catch (Exception e)
        {
            _failure = e;
            _log("bot loop failed: " + e);
        }
        finally
        {
            _threadDone = true;
        }
    }
}
