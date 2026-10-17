using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace GameServer.Persistence;

/// <summary>
/// Creates and drops whole databases. Dev and test only: in staging and production the databases are
/// provisioned with the infrastructure, and the server only runs migrations.
/// </summary>
public static partial class DatabaseAdmin
{
    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex SafeName();

    /// <summary>Creates <paramref name="databaseName"/> if it does not exist. Returns true if it was created.</summary>
    public static async Task<bool> EnsureDatabaseAsync(string connectionString, string databaseName, CancellationToken ct = default)
    {
        CheckName(databaseName);
        await using var connection = await OpenMaintenanceAsync(connectionString, ct);
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM pg_database WHERE datname = @name)", new { name = databaseName }, cancellationToken: ct));
        if (exists)
            return false;

        // Identifiers cannot be parameters; the name was checked against SafeName above.
        await connection.ExecuteAsync(new CommandDefinition($"CREATE DATABASE \"{databaseName}\"", cancellationToken: ct));
        return true;
    }

    public static async Task DropDatabaseAsync(string connectionString, string databaseName, CancellationToken ct = default)
    {
        CheckName(databaseName);
        // FORCE ends sessions still open on it (for example pooled connections of a disposed data source).
        await using var connection = await OpenMaintenanceAsync(connectionString, ct);
        await connection.ExecuteAsync(new CommandDefinition($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", cancellationToken: ct));
    }

    /// <summary>The database name from a connection string (the one to create).</summary>
    public static string DatabaseNameOf(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Database
        ?? throw new ArgumentException("connection string has no Database", nameof(connectionString));

    /// <summary>The same server and credentials, pointed at the maintenance database <c>postgres</c>.</summary>
    public static string MaintenanceConnectionString(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;

    private static async Task<NpgsqlConnection> OpenMaintenanceAsync(string connectionString, CancellationToken ct)
    {
        var connection = new NpgsqlConnection(MaintenanceConnectionString(connectionString));
        await connection.OpenAsync(ct);
        return connection;
    }

    private static void CheckName(string databaseName)
    {
        if (!SafeName().IsMatch(databaseName))
            throw new ArgumentException($"database name must be lower-case letters, digits and underscores: {databaseName}", nameof(databaseName));
    }
}
