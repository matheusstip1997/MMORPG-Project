using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using Npgsql;

namespace AuthApi.Tests.Support;

/// <summary>
/// A migrated global and realm database pair on a real PostgreSQL, dropped on dispose. Names are
/// <c>mmorpg_test_&lt;guid&gt;_&lt;kind&gt;</c>. The server comes from <c>MMORPG_TEST_PG</c>, defaulting to the
/// docker-compose dev server; tests fail, rather than skip, when it is not reachable (same rule as GameServer.Tests).
/// </summary>
public sealed class AuthTestDatabases : IAsyncDisposable
{
    public const string ServerVariable = "MMORPG_TEST_PG";
    private const string DevDefault = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=postgres";

    private readonly string _server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } fromEnv ? fromEnv : DevDefault;
    private readonly List<string> _created = [];

    public string GlobalConnectionString { get; private set; } = "";

    public string RealmConnectionString { get; private set; } = "";

    /// <summary>A database name that does not exist, for "database unavailable" tests.</summary>
    public string MissingDatabaseConnectionString =>
        new NpgsqlConnectionStringBuilder(_server) { Database = $"mmorpg_test_{Guid.NewGuid():N}_missing" }.ConnectionString;

    public async Task CreateAsync()
    {
        GlobalConnectionString = await CreateMigratedAsync(DatabaseKind.Global);
        RealmConnectionString = await CreateMigratedAsync(DatabaseKind.Realm);
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
                $"PostgreSQL for integration tests is not reachable. Run 'docker compose up -d --wait' or set {ServerVariable}.", e);
        }
        _created.Add(name);

        var connectionString = new NpgsqlConnectionStringBuilder(_server) { Database = name }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        await new MigrationRunner(new FakeClock()).ApplyAsync(source, MigrationCatalog.For(kind));
        return connectionString;
    }

    public async ValueTask DisposeAsync()
    {
        // DROP ... WITH (FORCE) ends sessions the app's data sources may still hold.
        foreach (var name in _created)
            await DatabaseAdmin.DropDatabaseAsync(_server, name);
    }
}
