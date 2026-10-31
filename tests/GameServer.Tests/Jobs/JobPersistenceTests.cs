using Dapper;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Jobs;

/// <summary>
/// The job pick against a real PostgreSQL (WP-M3-1, spec 5.1 M3 decisions, rule 5, R-7.1-01/05; CLAUDE.md: double submit,
/// retry, crash in the middle, concurrent access). <see cref="JobService"/> applies the pick and builds the batch, the
/// <see cref="InventoryService"/> the starter item's; the character save writes the job record, its <c>job_ops</c> audit row
/// and the item in one transaction. A slot is written once: a second pick into it under another key fails the save.
/// </summary>
public sealed class JobPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private static readonly Shared.Data.ConfigSet Tables = LoadTables();

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly JobRules _jobRules = JobRules.From(Tables)!;
    private readonly ItemRules _itemRules = ItemRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private JobRepository _jobs = null!;
    private long _nextAccount = 1;

    private static Shared.Data.ConfigSet LoadTables()
    {
        return ServerTestEnvironment.LoadTables(); // the Blacksmith's starter molds are WP-M3-2's rows
    }

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _jobs = new JobRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Smith{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return (await _characters.LoadAsync(result.Character!.CharacterId))!;
    }

    /// <summary>A world's view of the loaded character with a pick applied: the save it would issue.</summary>
    private async Task<(JobService Service, InventoryBatch? Items, JobBatch Jobs, Guid SaveId)> ApplyAsync(CharacterRecord character,
        string key, byte job = 1, JobSlot slot = JobSlot.Main)
    {
        var inventory = new InventoryService(_itemRules, null);
        Assert.True(inventory.Attach(character.CharacterId, await _inventory.LoadInventoryAsync(character.CharacterId), character.ClassId,
            character.State.Level));
        var service = new JobService(_jobRules, inventory, null);
        service.Attach(character.CharacterId, await _jobs.LoadAsync(character.CharacterId));
        var answer = service.Handle(character.CharacterId,
            new JobAsk(1, job, slot, key, character.State.Level, Blocked: false, InReach: true), connection: 1);
        Assert.Null(answer); // applied, waiting for its save
        var saveId = Guid.NewGuid();
        return (service, inventory.TakeBatch(character.CharacterId, saveId), service.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, InventoryBatch? items, JobBatch jobs, long? version = null) =>
        _characters.SaveAsync(character.CharacterId, character.State, version ?? character.Version, saveId, items, jobs: jobs);

    /// <summary>What the database holds: the job records, the pick audit rows and the starter molds.</summary>
    private async Task<(string Jobs, int Ops, int Molds)> StoredAsync(long characterId)
    {
        var jobs = await _jobs.LoadAsync(characterId);
        var ops = await _jobs.OpsAsync(characterId);
        var items = await _inventory.LoadInventoryAsync(characterId);
        return (string.Join(",", jobs.Jobs.Select(j => $"{j.JobId}:{j.Slot}:{j.Level}")), ops.Count,
            items.Instances.Count(i => i.DefId is "m3.moldSword" or "m3.moldChest"));
    }

    private static readonly (string, int, int) Picked = ("blacksmith:Main:1", 1, 2);
    private static readonly (string, int, int) Nothing = ("", 0, 0);

    [Fact]
    public async Task A_pick_commits_its_record_audit_row_and_starter_item_together_and_loads_again()
    {
        var character = await CreateAsync();
        var (service, items, jobs, saveId) = await ApplyAsync(character, "job:pick");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, jobs)).Status);

        Assert.Equal(Picked, await StoredAsync(character.CharacterId));
        var audit = Assert.Single(await _jobs.OpsAsync(character.CharacterId));
        Assert.Equal(("job:pick", JobOp.Pick, "blacksmith", JobSlot.Main, 12), (audit.OpKey, audit.Op, audit.JobId, audit.Slot, audit.LevelAtPick));
        Assert.Equal(["m3.moldSword", "m3.moldChest"], audit.StarterItems);

        // Only the commit makes it count (rule 5) and answers it.
        Assert.False(service.HasJob(character.CharacterId, "blacksmith"));
        CommittedJobOp? committed = null;
        service.OpCommitted = (_, c) => committed = c;
        service.Committed(character.CharacterId, saveId);
        Assert.Equal(JobReason.None, committed!.Answer!.Reason);
        Assert.True(service.HasJob(character.CharacterId, "blacksmith"));

        // A relogin reads the pick back: the slot is held, a second main pick is refused.
        var again = new JobService(_jobRules, null, null);
        again.Attach(character.CharacterId, await _jobs.LoadAsync(character.CharacterId));
        Assert.Equal("blacksmith", again.JobIn(character.CharacterId, JobSlot.Main));
        var refusal = again.Handle(character.CharacterId, new JobAsk(2, 2, JobSlot.Main, "job:again", 12, false, true), 1);
        Assert.Equal(JobReason.JobAlreadyChosen, refusal!.Reason);
    }

    [Fact]
    public async Task A_double_submit_applies_it_once()
    {
        var character = await CreateAsync();
        var (service, items, jobs, saveId) = await ApplyAsync(character, "job:double");
        Assert.Null(service.Handle(character.CharacterId, new JobAsk(1, 1, JobSlot.Main, "job:double", 12, false, true), 1));
        Assert.Equal(1, service.WaitingCount(character.CharacterId));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, saveId, items, jobs))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(Picked, await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more()
    {
        var character = await CreateAsync();
        var (_, items, jobs, saveId) = await ApplyAsync(character, "job:retry");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, jobs)).Status);

        var retry = await SaveAsync(character, saveId, items, jobs); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(Picked, await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_between_apply_and_commit_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var (_, items, jobs, saveId) = await ApplyAsync(character, "job:crash");
        // The row update and the starter item are written, then the job record fails: the whole save rolls back.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON job_progress FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, saveId, items, jobs));

        Assert.Equal(Nothing, await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON job_progress");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, jobs)).Status);
        Assert.Equal(Picked, await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task Two_picks_at_once_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each picking a
        // main job under its own key, from the same loaded version.
        var a = await ApplyAsync(character, "world-a", job: 1);
        var b = await ApplyAsync(character, "world-b", job: 2);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.SaveId, a.Items, a.Jobs)),
            Task.Run(() => SaveAsync(character, b.SaveId, b.Items, b.Jobs)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        var stored = await StoredAsync(character.CharacterId);
        Assert.Equal(1, stored.Ops);
        Assert.Single(stored.Jobs.Split(','));
    }

    [Fact]
    public async Task A_second_pick_into_a_written_slot_under_another_key_is_refused_by_the_database()
    {
        var character = await CreateAsync();
        var first = await ApplyAsync(character, "job:first", job: 1);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, first.SaveId, first.Items, first.Jobs)).Status);
        // A stale world at the new version that never loaded the pick (a bug, or a lost lock) picks another main job.
        var stale = character with { Version = character.Version + 1 };
        var inventory = new InventoryService(_itemRules, null);
        Assert.True(inventory.Attach(character.CharacterId, InventoryLoad.Empty, character.ClassId, 12));
        var service = new JobService(_jobRules, inventory, null);
        service.Attach(character.CharacterId, JobRecord.Empty);
        Assert.Null(service.Handle(character.CharacterId, new JobAsk(1, 2, JobSlot.Main, "job:second", 12, false, true), 1));
        var saveId = Guid.NewGuid();

        var result = await SaveAsync(stale, saveId, null, service.TakeBatch(character.CharacterId, saveId)!);

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Equal(Picked, await StoredAsync(character.CharacterId));
    }
}
