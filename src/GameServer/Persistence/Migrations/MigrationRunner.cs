using Dapper;
using Npgsql;
using Shared.Util;

namespace GameServer.Persistence.Migrations;

public sealed record MigrationReport(IReadOnlyList<int> Applied, int CurrentVersion);

/// <summary>
/// Applies versioned SQL migrations (R-7.1-04: schema changes only through migrations, never by hand).
/// <list type="bullet">
/// <item>The journal is <c>schema_migrations</c> (version, name, checksum, applied_at).</item>
/// <item>An applied script whose checksum changed, or a journal entry the code does not know, stops the run:
/// the database and the code disagree and a person has to look.</item>
/// <item>Each script runs in its own transaction together with its journal row.
/// Statements that cannot run in a transaction (<c>CREATE INDEX CONCURRENTLY</c>) are not supported yet.</item>
/// <item>A session advisory lock serializes runners, so two servers starting at once do not race.</item>
/// </list>
/// </summary>
public sealed class MigrationRunner(IClock clock)
{
    // Advisory lock keys are scoped to the current database, so one key serves every database.
    private const long AdvisoryLockKey = 0x4D4D4F5250475F4D; // "MMORPG_M"

    private const string CreateJournalSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version    integer     PRIMARY KEY,
            name       text        NOT NULL,
            checksum   text        NOT NULL,
            applied_at timestamptz NOT NULL
        )
        """;

    public async Task<MigrationReport> ApplyAsync(NpgsqlDataSource database, IReadOnlyList<Migration> migrations, CancellationToken ct = default)
    {
        migrations = MigrationCatalog.Validate(migrations);

        await using var connection = await database.OpenConnectionAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@key)", new { key = AdvisoryLockKey }, cancellationToken: ct));
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(CreateJournalSql, cancellationToken: ct));

            var applied = (await connection.QueryAsync<(int Version, string Checksum)>(
                    new CommandDefinition("SELECT version, checksum FROM schema_migrations ORDER BY version", cancellationToken: ct)))
                .ToDictionary(r => r.Version, r => r.Checksum);

            var known = migrations.ToDictionary(m => m.Version);
            foreach (var (version, checksum) in applied)
            {
                if (!known.TryGetValue(version, out var migration))
                    throw new MigrationException($"database has migration V{version:D4}, which this build does not know (database newer than code?)");
                if (migration.Checksum != checksum)
                    throw new MigrationException($"migration V{version:D4}__{migration.Name} was edited after it was applied; add a new migration instead");
            }

            var appliedNow = new List<int>();
            foreach (var migration in migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using var transaction = await connection.BeginTransactionAsync(ct);
                await connection.ExecuteAsync(new CommandDefinition(migration.Sql, transaction: transaction, cancellationToken: ct));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO schema_migrations (version, name, checksum, applied_at) VALUES (@Version, @Name, @Checksum, @AppliedAt)",
                    new { migration.Version, migration.Name, migration.Checksum, AppliedAt = DbTime.ToUtc(clock.UtcNowMs) },
                    transaction, cancellationToken: ct));
                await transaction.CommitAsync(ct);
                appliedNow.Add(migration.Version);
            }

            var current = migrations.Count == 0 ? 0 : migrations[^1].Version;
            return new MigrationReport(appliedNow, current);
        }
        finally
        {
            try
            {
                await connection.ExecuteAsync("SELECT pg_advisory_unlock(@key)", new { key = AdvisoryLockKey });
            }
            catch (NpgsqlException)
            {
                // The connection is gone, and a session lock dies with its session; keep the original error.
            }
        }
    }
}
