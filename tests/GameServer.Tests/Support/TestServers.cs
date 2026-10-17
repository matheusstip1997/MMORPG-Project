using System.Diagnostics;
using GameServer.Host;
using GameServer.Host.Config;
using GameServer.Net.Tickets;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Sessions;
using Shared.Data;
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

    public static async Task<ServerTestEnvironment> CreateAsync()
    {
        var environment = new ServerTestEnvironment();
        environment._realm = await environment._databases.CreateMigratedAsync(DatabaseKind.Realm);
        environment._realmConnection = environment._databases.ConnectionStringOf(environment._realm);
        environment.Characters = new CharacterRepository(environment._realm, Realm, environment.Clock);
        return environment;
    }

    /// <summary>The repository's config tables, freshly loaded so a test can change values.</summary>
    public static ServerConfig LoadConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var result = ConfigLoader.LoadAndValidate(Path.Combine(directory.FullName, "data", "tables"), ConfigTables.All);
        Assert.True(result.Ok, string.Join("\n", result.Errors));
        return ServerConfig.From(result.Set);
    }

    /// <param name="realmDatabase">Another realm connection string, for example one that cannot be reached.</param>
    public Bootstrap StartServer(ServerConfig config, string? realmDatabase = null)
    {
        var telemetry = new TempDirectory();
        _telemetry.Add(telemetry);
        var settings = new ServerSettings(Secret, TicketKeyId: 1, Realm, Port: 0, DataDirectory: "unused", telemetry.Path,
            realmDatabase ?? _realmConnection, TestRedis.Configuration, Prefix);
        var server = new Bootstrap(config, settings, Clock, new StopwatchTickClock());
        _servers.Add(server);
        server.Start();
        return server;
    }

    public string TelemetryDirectoryOf(Bootstrap server) => _telemetry[_servers.IndexOf(server)].Path;

    public async Task<CharacterRecord> CreateCharacterAsync(long accountId, int slot = 0, int xCm = 0, int yCm = 0)
    {
        var initial = new CharacterState(1, 0, SessionManager.MainWorldKey, xCm, yCm, 0, 0, 100, 50, 100);
        var result = await Characters.CreateAsync(new NewCharacter(accountId, slot, $"hero{accountId}s{slot}", "warrior", initial, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    public IssuedTicket Ticket(ServerConfig config, long accountId, byte slot = 0) =>
        new DevTicketIssuer(1, Secret, Clock, config.NetSecurity.TicketLifetimeMs).Issue(accountId, Realm, slot);

    public LoopbackGameClient Connect(Bootstrap server, ServerConfig config, long accountId, byte slot = 0) =>
        new(server.Transport.LocalPort, Ticket(config, accountId, slot));

    public string LockKey(long characterId) => $"{Prefix}realm:{Realm}:char:{characterId}";

    public string? LockHolder(long characterId) => Redis.GetDatabase().StringGet(LockKey(characterId));

    /// <summary>Polls the clients until <paramref name="done"/>, failing if a world crashed or the time ran out.</summary>
    public void WaitFor(Func<bool> done, params LoopbackGameClient[] clients)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < Timeout, "Timed out waiting for the server.");
            foreach (var server in _servers)
                Assert.Null(server.WorldThread.Failure);
            foreach (var client in clients)
                client.Poll();
            Thread.Sleep(5);
        }
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
