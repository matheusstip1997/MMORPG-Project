using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using Npgsql;
using Tests.Common;

namespace GameServer.Tests.Support;

/// <summary>
/// Creates throwaway databases on a real PostgreSQL server for integration tests, and drops them on dispose.
/// Names are <c>mmorpg_test_&lt;guid&gt;_&lt;kind&gt;</c>, so tests never touch the dev databases.
/// The server comes from <c>MMORPG_TEST_PG</c>, defaulting to the docker-compose dev server
/// (<c>docker compose up -d --wait</c>). Tests fail, rather than skip, when it is not reachable.
/// </summary>
public sealed class TestDatabases : IAsyncDisposable
{
    public const string ServerVariable = TestPostgres.ServerVariable;

    private readonly string _server = TestPostgres.Server("GameServer.Tests");
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

    /// <summary>The full connection string (with the password, which <c>NpgsqlDataSource.ConnectionString</c> hides) of a created database.</summary>
    public string ConnectionStringOf(NpgsqlDataSource source)
    {
        var name = _created.Single(c => c.Source == source).Name;
        return new NpgsqlConnectionStringBuilder(_server) { Database = name }.ConnectionString;
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
