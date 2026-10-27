using Dapper;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;

namespace GameServer.Tests.Persistence;

/// <summary>Schema changes only through migrations (R-7.1-04), against a real PostgreSQL.</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly MigrationRunner _runner = new(new FakeClock());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Theory]
    [InlineData(DatabaseKind.Global, "accounts")]
    [InlineData(DatabaseKind.Realm, "characters")]
    public async Task MigratesFromEmpty(DatabaseKind kind, string table)
    {
        var db = await _databases.CreateEmptyAsync("empty");
        var migrations = MigrationCatalog.For(kind);

        var report = await _runner.ApplyAsync(db, migrations);

        Assert.Equal(migrations.Select(m => m.Version), report.Applied);
        Assert.Equal(migrations[^1].Version, report.CurrentVersion);
        await using var connection = await db.OpenConnectionAsync();
        Assert.True(await connection.ExecuteScalarAsync<bool>("SELECT to_regclass(@table) IS NOT NULL", new { table }));
        Assert.Equal(migrations.Count, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task SecondRunAppliesNothing()
    {
        var db = await _databases.CreateEmptyAsync("rerun");
        var migrations = MigrationCatalog.For(DatabaseKind.Realm);
        await _runner.ApplyAsync(db, migrations);

        var report = await _runner.ApplyAsync(db, migrations);

        Assert.Empty(report.Applied);
    }

    [Fact]
    public async Task OnlyNewMigrationsAreApplied()
    {
        var db = await _databases.CreateEmptyAsync("incremental");
        var v1 = Migration.Create(1, "first", "CREATE TABLE a (id int)");
        var v2 = Migration.Create(2, "second", "CREATE TABLE b (id int)");
        await _runner.ApplyAsync(db, [v1]);

        var report = await _runner.ApplyAsync(db, [v1, v2]);

        Assert.Equal([2], report.Applied);
    }

    [Fact]
    public async Task AVersionGapAndALaterLowerVersionAreBothApplied()
    {
        // M2 wave 0: V0007 (WP-M2-2) may reach a database before V0004-V0006 of the parallel work packages.
        var db = await _databases.CreateEmptyAsync("gaps");
        var v1 = Migration.Create(1, "first", "CREATE TABLE a (id int)");
        var v4 = Migration.Create(4, "fourth", "CREATE TABLE d (id int)");
        var v7 = Migration.Create(7, "seventh", "CREATE TABLE g (id int)");
        Assert.Equal([1, 7], (await _runner.ApplyAsync(db, [v1, v7])).Applied);

        var report = await _runner.ApplyAsync(db, [v1, v4, v7]);

        Assert.Equal([4], report.Applied);
        Assert.Equal(7, report.CurrentVersion);
    }

    [Fact]
    public async Task EditedMigrationIsRefused()
    {
        var db = await _databases.CreateEmptyAsync("edited");
        await _runner.ApplyAsync(db, [Migration.Create(1, "first", "CREATE TABLE a (id int)")]);

        var edited = Migration.Create(1, "first", "CREATE TABLE a (id bigint)");
        var error = await Assert.ThrowsAsync<MigrationException>(() => _runner.ApplyAsync(db, [edited]));
        Assert.Contains("edited", error.Message);
    }

    [Fact]
    public async Task UnknownAppliedMigrationIsRefused()
    {
        var db = await _databases.CreateEmptyAsync("newer");
        var v1 = Migration.Create(1, "first", "CREATE TABLE a (id int)");
        await _runner.ApplyAsync(db, [v1, Migration.Create(2, "second", "CREATE TABLE b (id int)")]);

        await Assert.ThrowsAsync<MigrationException>(() => _runner.ApplyAsync(db, [v1]));
    }

    [Fact]
    public async Task FailingMigrationRollsBackAndIsNotRecorded()
    {
        var db = await _databases.CreateEmptyAsync("failing");
        var broken = Migration.Create(1, "broken", "CREATE TABLE a (id int); SELECT * FROM missing_table");

        await Assert.ThrowsAnyAsync<Exception>(() => _runner.ApplyAsync(db, [broken]));

        await using var connection = await db.OpenConnectionAsync();
        Assert.False(await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('a') IS NOT NULL"));
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM schema_migrations"));
    }

    [Fact]
    public async Task ConcurrentRunnersApplyEachMigrationOnce()
    {
        var db = await _databases.CreateEmptyAsync("concurrent");
        var migrations = MigrationCatalog.For(DatabaseKind.Realm);

        var reports = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _runner.ApplyAsync(db, migrations)));

        Assert.Equal(migrations.Count, reports.Sum(r => r.Applied.Count));
    }

    [Fact]
    public void LineEndingsDoNotChangeTheChecksum()
    {
        Assert.Equal(Migration.Create(1, "a", "SELECT 1;\nSELECT 2;").Checksum, Migration.Create(1, "a", "SELECT 1;\r\nSELECT 2;").Checksum);
    }

    [Fact]
    public void DuplicateVersionsAreRefused()
    {
        Assert.Throws<MigrationException>(() => MigrationCatalog.Validate([Migration.Create(1, "a", ""), Migration.Create(1, "b", "")]));
    }
}
