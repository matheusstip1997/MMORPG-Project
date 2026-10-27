using System.Diagnostics;
using System.Runtime.CompilerServices;
using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Net.Tickets;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Sessions;
using GameServer.World.Map;
using Shared.Data;
using Shared.Simulation;
using StackExchange.Redis;

namespace GameServer.Tests.Support;

/// <summary>
/// The Redis server for integration tests: <c>MMORPG_TEST_REDIS</c>, defaulting to the docker-compose dev server
/// (<c>docker compose up -d --wait</c>). Tests fail, rather than skip, when it is not reachable. Every test uses its
/// own key prefix, so tests never see each other's keys or the dev server's.
/// </summary>
public static class TestRedis
{
    public const string ServerVariable = "MMORPG_TEST_REDIS";
    private const string DevDefault = "127.0.0.1:6379";

    public static string Configuration =>
        Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } fromEnv ? fromEnv : DevDefault;

    public static ConnectionMultiplexer Connect()
    {
        try
        {
            return ConnectionMultiplexer.Connect(Configuration);
        }
        catch (RedisConnectionException e)
        {
            throw new InvalidOperationException(
                $"Redis for integration tests is not reachable. Run 'docker compose up -d --wait' or set {ServerVariable}.", e);
        }
    }

    public static string NewPrefix() => $"test:{Guid.NewGuid():N}:";

    /// <summary>Deletes every key under <paramref name="prefix"/>.</summary>
    public static async Task DeleteAsync(IConnectionMultiplexer redis, string prefix)
    {
        foreach (var endpoint in redis.GetEndPoints())
        {
            var server = redis.GetServer(endpoint);
            foreach (var key in server.Keys(pattern: prefix + "*"))
                await redis.GetDatabase().KeyDeleteAsync(key);
        }
    }
}

/// <summary>
/// A migrated realm database and a Redis key prefix shared by one or more <see cref="Bootstrap"/> servers running over
/// localhost, plus helpers to create characters, issue tickets and wait on the real clock.
/// </summary>
public sealed class ServerTestEnvironment : IAsyncDisposable
{
    public const int Realm = 1;
    public static readonly byte[] Secret = Enumerable.Repeat((byte)0x3C, 32).ToArray();
    /// <summary>
    /// The budget of one <see cref="WaitFor(Func{bool}, string)"/>. Measured on the 2-core CI runner (4 full-suite jobs,
    /// 2026-10-27): the slowest wait that succeeded took 3.5 s (50 clients logging in at once), so 15 s is room for a cold
    /// runner. A wait that runs out is one that could not succeed; its failure message says what it waited for.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly TestDatabases _databases = new();
    private readonly List<Bootstrap> _servers = [];
    private readonly List<TempDirectory> _telemetry = [];
    private Npgsql.NpgsqlDataSource? _realm;
    private string _realmConnection = "";

    public SystemClock Clock { get; } = new();

    public string Prefix { get; } = TestRedis.NewPrefix();

    public ConnectionMultiplexer Redis { get; } = TestRedis.Connect();

    public CharacterRepository Characters { get; private set; } = null!;

    /// <summary>The realm's item stacks (backlog G), to check what the server saved.</summary>
    public InventoryRepository Inventory { get; private set; } = null!;

    /// <summary>The realm database the servers use (admin actions, spec 7.4).</summary>
    public Npgsql.NpgsqlDataSource RealmDatabase => _realm!;

    public static async Task<ServerTestEnvironment> CreateAsync()
    {
        var environment = new ServerTestEnvironment();
        environment._realm = await environment._databases.CreateMigratedAsync(DatabaseKind.Realm);
        environment._realmConnection = environment._databases.ConnectionStringOf(environment._realm);
        environment.Characters = new CharacterRepository(environment._realm, Realm, environment.Clock);
        environment.Inventory = new InventoryRepository(environment._realm);
        return environment;
    }

    /// <summary>The committed M1 sample world (data/world/m1), the one the server loads by default. Read-only, so shared.</summary>
    private static readonly Lazy<WorldMap> M1World = new(() =>
    {
        var world = WorldLoader.Load(RepositoryPaths.M1World(), LoadTables());
        Assert.True(world.Ok, string.Join("\n", world.Errors));
        return world.Map!;
    });

    /// <summary>The repository's config tables, freshly loaded so a test can change values.</summary>
    public static ServerConfig LoadConfig() => ServerConfig.From(LoadTables());

    /// <summary>The repository's config tables, freshly loaded so a test can replace a table before building its config.</summary>
    internal static ConfigSet LoadTables()
    {
        var result = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        return result.Set;
    }

    /// <summary>The player spawn marker of the M1 world in whole cm: where new characters appear.</summary>
    public static (int XCm, int YCm) SpawnCm()
    {
        Assert.True(M1World.Value.TryGetMarker(LoadConfig().ServerLoop.PlayerSpawnMarkerId, out var spawn));
        return (FixedPoint.ToCmRound(spawn.Position.X), FixedPoint.ToCmRound(spawn.Position.Y));
    }

    /// <param name="realmDatabase">Another realm connection string, for example one that cannot be reached.</param>
    public Bootstrap StartServer(ServerConfig config, string? realmDatabase = null)
    {
        var telemetry = new TempDirectory();
        _telemetry.Add(telemetry);
        var settings = new ServerSettings(Secret, TicketKeyId: 1, Realm, Port: 0, DataDirectory: "unused", telemetry.Path,
            RealmDatabase: realmDatabase ?? _realmConnection, Redis: TestRedis.Configuration, RedisKeyPrefix: Prefix);
        var server = new Bootstrap(config, settings, Clock, new StopwatchTickClock(), M1World.Value);
        _servers.Add(server);
        server.Start();
        return server;
    }

    public string TelemetryDirectoryOf(Bootstrap server) => _telemetry[_servers.IndexOf(server)].Path;

    /// <summary>A saved character, by default a warrior standing on the M1 spawn marker (a walkable cell).</summary>
    /// <param name="hp">Saved HP; a value above the class's max HP is clamped when read (<c>CombatRules.Restore</c>).</param>
    public async Task<CharacterRecord> CreateCharacterAsync(long accountId, int slot = 0, int? xCm = null, int? yCm = null,
        string classId = "warrior", int hp = 100)
    {
        var (spawnX, spawnY) = SpawnCm();
        var initial = new CharacterState(1, 0, SessionManager.MainWorldKey, xCm ?? spawnX, yCm ?? spawnY, 0, 0, hp, 50, 100);
        var result = await Characters.CreateAsync(new NewCharacter(accountId, slot, $"hero{accountId}s{slot}", classId, initial, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    public IssuedTicket Ticket(ServerConfig config, long accountId, byte slot = 0) =>
        new DevTicketIssuer(1, Secret, Clock, config.NetSecurity.TicketLifetimeMs).Issue(accountId, Realm, slot);

    public LoopbackGameClient Connect(Bootstrap server, ServerConfig config, long accountId, byte slot = 0) =>
        new(server.Transport.LocalPort, Ticket(config, accountId, slot));

    public string LockKey(long characterId) => $"{Prefix}realm:{Realm}:char:{characterId}";

    public string? LockHolder(long characterId) => Redis.GetDatabase().StringGet(LockKey(characterId));

    /// <summary>Waits until <paramref name="done"/>, failing if a world crashed or the time ran out.</summary>
    /// <param name="what">The condition's source text, for the failure message.</param>
    public void WaitFor(Func<bool> done, [CallerArgumentExpression(nameof(done))] string what = "") => Wait(done, [], what);

    /// <summary>Polls <paramref name="client"/> until <paramref name="done"/> (see <see cref="WaitFor(Func{bool}, string)"/>).</summary>
    public void WaitFor(Func<bool> done, LoopbackGameClient client, [CallerArgumentExpression(nameof(done))] string what = "") =>
        Wait(done, [client], what);

    /// <summary>Polls both clients until <paramref name="done"/> (see <see cref="WaitFor(Func{bool}, string)"/>).</summary>
    public void WaitFor(Func<bool> done, LoopbackGameClient first, LoopbackGameClient second,
        [CallerArgumentExpression(nameof(done))] string what = "") => Wait(done, [first, second], what);

    /// <summary>Polls the clients until <paramref name="done"/> (see <see cref="WaitFor(Func{bool}, string)"/>).</summary>
    public void WaitFor(Func<bool> done, LoopbackGameClient[] clients, [CallerArgumentExpression(nameof(done))] string what = "") =>
        Wait(done, clients, what);

    private void Wait(Func<bool> done, LoopbackGameClient[] clients, string what)
    {
        var watch = Stopwatch.StartNew();
        var ticksBefore = _servers.Select(s => s.World.ServerTick).ToArray();
        while (!done())
        {
            if (watch.Elapsed >= Timeout)
                Assert.Fail(DescribeTimeout(what, watch.Elapsed, ticksBefore, clients));
            foreach (var server in _servers)
                Assert.Null(server.WorldThread.Failure);
            foreach (var client in clients)
                client.Poll();
            Thread.Sleep(5);
        }
    }

    /// <summary>What a wait that ran out waited for, how long, how far each world ticked and where each client stands.</summary>
    private string DescribeTimeout(string what, TimeSpan waited, uint[] ticksBefore, LoopbackGameClient[] clients)
    {
        var text = new System.Text.StringBuilder();
        text.Append($"Timed out after {waited.TotalSeconds:F1} s (budget {Timeout.TotalSeconds:F0} s) waiting for: {what}");
        for (int i = 0; i < ticksBefore.Length; i++)
            text.Append($"\nServer {i}: tick {_servers[i].World.ServerTick} (+{_servers[i].World.ServerTick - ticksBefore[i]} during the wait), {_servers[i].Transport.ConnectedPeers} peers");
        AppendClients(text, clients);
        return text.ToString();
    }

    /// <summary>Where each client stands, the unusual ones (disconnected, no entity yet) first; at most 10 lines.</summary>
    internal static void AppendClients(System.Text.StringBuilder text, IReadOnlyList<LoopbackGameClient> clients)
    {
        const int MaxListed = 10;
        if (clients.Count == 0)
            return;
        text.Append($"\nClients: {clients.Count(c => c.Connected)} of {clients.Count} connected, {clients.Count(c => c.Disconnected)} disconnected, "
            + $"{clients.Count(c => c.OwnEntityId != null)} with an own entity");
        var listed = clients.Select((client, index) => (client, index))
            .OrderBy(c => c.client.Disconnected || c.client.OwnEntityId == null ? 0 : 1).ThenBy(c => c.index).Take(MaxListed);
        foreach (var (client, index) in listed)
            text.Append($"\n  [{index}] {client.Describe()}");
        if (clients.Count > MaxListed)
            text.Append($"\n  ... {clients.Count - MaxListed} more");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
            server.Dispose();
        foreach (var telemetry in _telemetry)
            telemetry.Dispose();
        await TestRedis.DeleteAsync(Redis, Prefix);
        Redis.Dispose();
        await _databases.DisposeAsync();
    }
}
