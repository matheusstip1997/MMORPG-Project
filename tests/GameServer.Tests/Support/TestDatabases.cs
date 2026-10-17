using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using Npgsql;

namespace GameServer.Tests.Support;

/// <summary>
/// Creates throwaway databases on a real PostgreSQL server for integration tests, and drops them on dispose.
/// Names are <c>mmorpg_test_&lt;guid&gt;_&lt;kind&gt;</c>, so tests never touch the dev databases.
/// The server comes from <c>MMORPG_TEST_PG</c>, defaulting to the docker-compose dev server
/// (<c>docker compose up -d --wait</c>). Tests fail, rather than skip, when it is not reachable.
/// </summary>
public sealed class TestDatabases : IAsyncDisposable
{
    public const string ServerVariable = "MMORPG_TEST_PG";
    private const string DevDefault = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=postgres";

    private readonly string _server = Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } fromEnv ? fromEnv : DevDefault;
    private readonly List<(string Name, NpgsqlDataSource Source)> _created = [];

    /// <summary>Creates an empty database (no migrations applied).</summary>
    public async Task<NpgsqlDataSource> CreateEmptyAsync(string kind)
    {
        var name = $"mmorpg_test_{Guid.NewGuid():N}_{kind}";
        try
        {
            await DatabaseAdmin.EnsureDatabaseAsync(_server, name);
        }
        catch (NpgsqlException e)
        {
            throw new InvalidOperationException(
                $"PostgreSQL for integration tests is not reachable. Run 'docker compose up -d --wait' or set {ServerVariable}.", e);
        }

        var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(_server) { Database = name }.ConnectionString);
        _created.Add((name, source));
        return source;
    }

    /// <summary>Creates a database and applies the migrations of <paramref name="kind"/>.</summary>
    public async Task<NpgsqlDataSource> CreateMigratedAsync(DatabaseKind kind)
    {
        var source = await CreateEmptyAsync(kind.ToString().ToLowerInvariant());
        await new MigrationRunner(new FakeClock()).ApplyAsync(source, MigrationCatalog.For(kind));
        return source;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (name, source) in _created)
        {
            await source.DisposeAsync();
            await DatabaseAdmin.DropDatabaseAsync(_server, name);
        }
    }
}
