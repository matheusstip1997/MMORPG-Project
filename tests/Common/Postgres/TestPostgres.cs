using Npgsql;

namespace Tests.Common;

/// <summary>
/// The PostgreSQL server of the integration tests (GameServer.Tests, AdminPanel.Tests, AuthApi.Tests; linked into each):
/// <c>MMORPG_TEST_PG</c>, defaulting to the docker-compose dev server. Every connection string the tests derive from it
/// (their own data sources and the ones the game server, Auth API and admin panel build from their settings) carries a
/// pool cap, so a full <c>dotnet test Mmorpg.sln</c> stays well under PostgreSQL's <c>max_connections</c> (default 100)
/// and two runs, or a run beside the dev stack, fit on one server.
/// </summary>
/// <remarks>
/// Measured 2026-11-10 (16 cores, 4 test assemblies in parallel): without the cap one run peaked at 75 client
/// connections, 51 of them the idle realm pool of one game server after 50 players logged in at once (Npgsql's default
/// pool is 100 per data source, and idle connections live 300 s). The largest concurrency a test needs on one data source
/// is 8 parallel operations (the character slot race in <c>CharacterRepositoryTests</c>), so
/// <see cref="MaxPoolSize"/> keeps every concurrency test truly concurrent; past the cap an open waits for a free
/// connection (the connection string's <c>Timeout</c>, 15 s by default) instead of failing.
/// </remarks>
public static class TestPostgres
{
    public const string ServerVariable = "MMORPG_TEST_PG";

    /// <summary>The most connections one test data source opens. At least the largest concurrency a test needs (8).</summary>
    public const int MaxPoolSize = 10;

    private const string DevDefault = "Host=127.0.0.1;Port=5432;Username=mmorpg;Password=mmorpg_dev;Database=postgres";

    /// <summary>
    /// The server's connection string with the test pool cap, and <paramref name="applicationName"/> as the
    /// <c>application_name</c> that <c>pg_stat_activity</c> shows (which test assembly holds a connection).
    /// </summary>
    public static string Server(string applicationName) =>
        new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(ServerVariable) is { Length: > 0 } fromEnv ? fromEnv : DevDefault)
        {
            MaxPoolSize = MaxPoolSize,
            ApplicationName = applicationName,
        }.ConnectionString;
}
