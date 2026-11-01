using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;

namespace GameServer.Tests.Dev;

/// <summary>
/// The dev console's value-moving commands against a real PostgreSQL (WP-M3-13, rule 5; CLAUDE.md: double submit, retry,
/// crash in the middle, concurrent access). A world with the console runs the command; the save it asks for is written
/// through the real <see cref="CharacterRepository"/> (the character row, the inventory batch with its audit rows, gold,
/// the progression and job batches in one transaction), and the console answers only once that save is reported committed.
/// </summary>
public sealed class DevConsolePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 0, 0, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private long _version;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>The database character and the world's (the fake source numbers characters from 1, as a fresh database does).</summary>
    private async Task<DevConsoleWorldTests.Fixture> CreateAsync()
    {
        var result = await _characters.CreateAsync(new NewCharacter(1, 0, "Dev1", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        _version = result.Character!.Version;
        var f = new DevConsoleWorldTests.Fixture(level: Spawn.Level);
        Assert.Equal(result.Character.CharacterId, f.CharacterId);
        f.Harness.Saves.AutoComplete = false;
        return f;
    }

    /// <summary>Writes a save the world issued, as the persistence worker would.</summary>
    private async Task<SaveStatus> WriteAsync(SaveJob job)
    {
        var result = await _characters.SaveAsync(job.CharacterId, job.State, _version, job.SaveId, job.Inventory,
            progression: job.Progression, skills: job.Skills, bystial: job.Bystial, quests: job.Quests, jobs: job.Jobs, craft: job.Craft);
        if (result.Status == SaveStatus.Saved)
            _version = result.Version;
        return result.Status;
    }

    private static SaveJob DevSave(DevConsoleWorldTests.Fixture f) => f.Harness.Saves.Jobs.Last(j => j.Reason == SaveReason.DevCommand);

    private async Task<int> StoredAsync(string itemId, long characterId) =>
        (await _inventory.LoadAsync(characterId)).Where(r => r.DefId == itemId).Sum(r => r.Count);

    [Fact]
    public async Task A_give_commits_its_items_and_audit_row_with_the_dev_source_and_is_answered_after()
    {
        var f = await CreateAsync();
        f.Harness.SendChat(1, "/give m3.oreT1 4");
        f.Harness.Ticks(2);
        Assert.Empty(f.Answers);

        Assert.Equal(SaveStatus.Saved, await WriteAsync(DevSave(f)));
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        Assert.Equal(4, await StoredAsync("m3.oreT1", f.CharacterId));
        var audit = Assert.Single(await _inventory.AuditAsync(f.CharacterId));
        Assert.StartsWith("dev:test-run:", audit.OpKey);
        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal(("Admin", $"dev-console:account:{f.Player.AccountId}"), await connection.QuerySingleAsync<(string, string)>(
            "SELECT reason, source FROM inventory_ops WHERE character_id = @id", new { id = f.CharacterId }));
        Assert.Equal(DevReason.None, Assert.Single(f.Answers).Reason);
    }

    [Fact]
    public async Task A_double_submit_of_the_same_save_and_a_retry_after_an_unknown_outcome_apply_once()
    {
        var f = await CreateAsync();
        f.Harness.SendChat(1, "/gold 750");
        f.Harness.Ticks(2);
        var save = DevSave(f);

        long expected = _version;
        Assert.Equal(SaveStatus.Saved, await WriteAsync(save));
        // The first outcome was lost: the worker sends the identical job again (same save id and expected version), twice.
        for (int retry = 0; retry < 2; retry++)
        {
            _version = expected;
            Assert.Equal(SaveStatus.Saved, await WriteAsync(save));
        }

        Assert.Equal(750, (await _characters.LoadAsync(f.CharacterId))!.Gold);
        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM gold_ledger WHERE character_id = @id",
            new { id = f.CharacterId }));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_nothing_answers_nothing_and_the_retry_applies_once()
    {
        var f = await CreateAsync();
        f.Harness.SendChat(1, "/give m3.oreT1 4");
        f.Harness.Ticks(2);
        var save = DevSave(f);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN IF NEW.op_key LIKE 'dev:%' THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON audit_log FOR EACH ROW EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => WriteAsync(save));
        Assert.Equal(0, await StoredAsync("m3.oreT1", f.CharacterId));
        Assert.Equal(1, (await _characters.LoadAsync(f.CharacterId))!.Version); // the row too: one transaction
        f.Harness.Saves.Outcome = SaveOutcome.Failed; // the worker reports the failure; the scheduler retries later
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(3);
        Assert.Empty(f.Answers);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON audit_log");
        Assert.Equal(SaveStatus.Saved, await WriteAsync(save));
        Assert.Equal(4, await StoredAsync("m3.oreT1", f.CharacterId));
        Assert.Single(await _inventory.AuditAsync(f.CharacterId));
    }

    [Fact]
    public async Task A_command_in_the_same_tick_as_the_players_own_bag_move_rides_the_same_save()
    {
        var f = await CreateAsync();
        f.Harness.SendChat(1, "/give m3.oreT1 4");
        f.Harness.Ticks(2);
        Assert.Equal(SaveStatus.Saved, await WriteAsync(DevSave(f)));
        f.Harness.Saves.CompleteAll();
        f.Harness.Ticks(2);

        var normal = (byte)f.Inventory.Rules.NormalBag.Code;
        f.Harness.SendMove(1, 1, normal, 0, normal, 5, 1); // the player's own split, concurrent with the command
        f.Harness.SendChat(1, "/give m3.oreT1 2");
        f.Harness.Ticks(2);
        var save = DevSave(f);
        Assert.Equal(2, save.Inventory!.Ops.Count); // both operations, one transaction
        Assert.Equal(SaveStatus.Saved, await WriteAsync(save));

        var rows = await _inventory.LoadAsync(f.CharacterId);
        Assert.Equal(6, rows.Where(r => r.DefId == "m3.oreT1").Sum(r => r.Count));
        Assert.Contains(rows, r => r.Slot == 5 && r.Count == 1);
        Assert.Equal(6, (await _inventory.AuditAsync(f.CharacterId)).Where(a => a.Kind == AuditKind.Add).Sum(a => a.CountDelta));
    }

    [Fact]
    public async Task Level_kills_and_a_job_pick_commit_with_the_character_row_and_load_again()
    {
        var f = await CreateAsync();
        foreach (var line in new[] { "/level 16", $"/kills {TestCombat.MonsterId} 14", "/job blacksmith" })
        {
            f.Harness.SendChat(1, line);
            f.Harness.Ticks(2);
            Assert.Equal(SaveStatus.Saved, await WriteAsync(DevSave(f)));
            f.Harness.Saves.CompleteAll();
            f.Harness.Ticks(2);
        }

        Assert.Equal(16, (await _characters.LoadAsync(f.CharacterId))!.State.Level);
        var progression = await new ProgressionRepository(_realmDb).LoadAsync(f.CharacterId);
        Assert.Contains(progression.KillCounts, k => k.MonsterDefId == TestCombat.MonsterId && k.Count == 14);
        var jobs = new JobRepository(_realmDb);
        Assert.Equal("blacksmith", Assert.Single((await jobs.LoadAsync(f.CharacterId)).Jobs).JobId);
        Assert.Single(await jobs.OpsAsync(f.CharacterId)); // the pick's own audit row
        Assert.Equal([DevReason.None, DevReason.None, DevReason.None], f.Answers.Select(a => a.Reason));
    }
}
