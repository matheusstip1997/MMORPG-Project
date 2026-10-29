using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServer.Admin;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shared.Util;
using StackExchange.Redis;

namespace AdminPanel.Tests.Support;

/// <summary>Thread-safe: concurrent requests read it while a test advances it.</summary>
public sealed class FakeClock(long utcNowMs = 1_800_000_000_000) : IClock
{
    private long _now = utcNowMs;

    public long UtcNowMs => Interlocked.Read(ref _now);

    public void Advance(long ms) => Interlocked.Add(ref _now, ms);
}

/// <summary>Accepts any well-formed name as <c>test:&lt;name&gt;</c>, so the tests run the same in Debug and Release.</summary>
public sealed class FakeAdminIdentity : IAdminIdentity
{
    public AdminReasonCode Verify(string? name, int maxChars, out string adminName)
    {
        adminName = string.IsNullOrWhiteSpace(name) ? "" : "test:" + name.Trim();
        return adminName.Length == 0 ? AdminReasonCode.RequestInvalid : AdminReasonCode.None;
    }
}

/// <summary>
/// The admin panel hosted in memory against a real PostgreSQL (a migrated global and realm database pair, dropped on
/// dispose) and Redis (its own key prefix), with temp telemetry and log folders. Tests fail, rather than skip, when
/// PostgreSQL or Redis is not reachable (same rule as GameServer.Tests).
/// </summary>
public sealed class AdminPanelEnvironment : IAsyncDisposable
{
    public const int Realm = 1;
    private const string DevPg = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=postgres";

    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly string _server = Environment.GetEnvironmentVariable("MMORPG_TEST_PG") is { Length: > 0 } pg ? pg : DevPg;
    private readonly string _redisConfiguration = Environment.GetEnvironmentVariable("MMORPG_TEST_REDIS") is { Length: > 0 } r ? r : "127.0.0.1:6379";
    private readonly List<string> _created = [];
    private readonly List<NpgsqlDataSource> _sources = [];
    private Factory? _factory;

    public string Prefix { get; } = $"test:{Guid.NewGuid():N}:";

    public string TelemetryDirectory { get; } = Directory.CreateTempSubdirectory("admin-telemetry-").FullName;

    public string LogDirectory { get; } = Directory.CreateTempSubdirectory("admin-logs-").FullName;

    public FakeClock Clock { get; } = new();

    public ConnectionMultiplexer Redis { get; private set; } = null!;

    public NpgsqlDataSource Global { get; private set; } = null!;

    public NpgsqlDataSource RealmDb { get; private set; } = null!;

    public Dictionary<string, string> Settings { get; } = new();

    public WebApplicationFactory<Program> App => _factory ??= new Factory(this);

    public static async Task<AdminPanelEnvironment> CreateAsync()
    {
        var env = new AdminPanelEnvironment();
        var global = await env.CreateMigratedAsync(DatabaseKind.Global);
        var realm = await env.CreateMigratedAsync(DatabaseKind.Realm);
        env.Global = NpgsqlDataSource.Create(global);
        env.RealmDb = NpgsqlDataSource.Create(realm);
        env._sources.AddRange([env.Global, env.RealmDb]);
        env.Redis = await ConnectionMultiplexer.ConnectAsync(env._redisConfiguration);
        env.Settings["MMORPG_GLOBAL_DB"] = global;
        env.Settings["MMORPG_REALM_DB"] = realm;
        env.Settings["MMORPG_REALM_ID"] = Realm.ToString();
        env.Settings[AdminPanelSettings.RedisVariable] = env._redisConfiguration;
        env.Settings[AdminPanelSettings.RedisKeyPrefixVariable] = env.Prefix;
        env.Settings[AdminPanelSettings.TelemetryDirectoryVariable] = env.TelemetryDirectory;
        env.Settings[AdminPanelSettings.LogDirectoryVariable] = env.LogDirectory;
        env.Settings[AdminPanelSettings.DataDirectoryVariable] = TablesDirectory();
        return env;
    }

    /// <summary>When false, the configured identity (default <c>none</c>) is used instead of <see cref="FakeAdminIdentity"/>.</summary>
    public bool UseFakeIdentity { get; set; } = true;

    public AdminActionRepository Actions => new(RealmDb, Realm, Clock);

    public static string TablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data", "tables");
    }

    /// <summary>A client that is signed in as <c>test:&lt;name&gt;</c> and sends the request header on changes.</summary>
    public async Task<HttpClient> SignedInClientAsync(string name = "ana")
    {
        var client = App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add(AdminEndpoints.RequestHeader, "1");
        var response = await client.PostAsJsonAsync("/api/session", new { name });
        response.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>An account (global database) and a character in its slot 0 (realm database).</summary>
    public async Task<(Account Account, CharacterRecord Character)> CreateCharacterAsync(long steamId, string name)
    {
        var account = await new AccountRepository(Global, Clock).GetOrCreateBySteamIdAsync(steamId, null);
        var initial = new CharacterState(1, 0, SessionManager.MainWorldKey, 100, 200, 0, 0, 100, 50, 100);
        var created = await new CharacterRepository(RealmDb, Realm, Clock).CreateAsync(
            new NewCharacter(account.AccountId, 0, name, "warrior", initial, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, created.Status);
        return (account, created.Character!);
    }

    /// <summary>Marks the character as held by game server <paramref name="serverId"/>, as its ownership lock does.</summary>
    public Task HoldAsync(long characterId, string serverId) =>
        Redis.GetDatabase().StringSetAsync(RedisOwnershipLocks.KeyFor(Prefix, Realm, characterId),
            OwnershipLockToken.Create(serverId, 1), TimeSpan.FromMinutes(1));

    public async ValueTask DisposeAsync()
    {
        if (_factory != null)
            await _factory.DisposeAsync();
        if (Redis != null)
        {
            foreach (var endpoint in Redis.GetEndPoints())
                foreach (var key in Redis.GetServer(endpoint).Keys(pattern: Prefix + "*"))
                    await Redis.GetDatabase().KeyDeleteAsync(key);
            Redis.Dispose();
        }
        foreach (var source in _sources)
            await source.DisposeAsync();
        foreach (var name in _created)
            await DatabaseAdmin.DropDatabaseAsync(_server, name);
        Directory.Delete(TelemetryDirectory, recursive: true);
        Directory.Delete(LogDirectory, recursive: true);
    }

    private async Task<string> CreateMigratedAsync(DatabaseKind kind)
    {
        var name = $"mmorpg_test_{Guid.NewGuid():N}_{kind.ToString().ToLowerInvariant()}";
        try
        {
            await DatabaseAdmin.EnsureDatabaseAsync(_server, name);
        }
        catch (NpgsqlException e)
        {
            throw new InvalidOperationException(
                "PostgreSQL for integration tests is not reachable. Run 'docker compose up -d --wait' or set MMORPG_TEST_PG.", e);
        }
        _created.Add(name);
        var connectionString = new NpgsqlConnectionStringBuilder(_server) { Database = name }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        await new MigrationRunner(Clock).ApplyAsync(source, MigrationCatalog.For(kind));
        return connectionString;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class Factory(AdminPanelEnvironment env) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            foreach (var (key, value) in env.Settings)
                builder.UseSetting(key, value);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IClock>(env.Clock);
                if (env.UseFakeIdentity)
                    services.AddSingleton<IAdminIdentity, FakeAdminIdentity>();
            });
        }
    }
}
