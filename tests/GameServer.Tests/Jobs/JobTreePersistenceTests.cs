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
/// Job tree learn and respec against a real PostgreSQL (WP-M3-7, spec 5.1, rule 5, R-7.1-05; CLAUDE.md: double submit,
/// retry, crash in the middle, concurrent access). <see cref="JobService"/> applies the operation and builds the batch; the
/// character save writes the job record (its points), the learned nodes (<c>job_nodes</c>) and the <c>job_ops</c> audit row
/// in one transaction, and only its commit makes the nodes count. Placeholders the tests read: a pick's 1 job point
/// (<c>JobSettings.pointsPerLevel</c>), Prospector costs 1 and gives yield +4 at level 1.
/// </summary>
public sealed class JobTreePersistenceTests : IAsyncLifetime
{
    private const string Prospector = "blacksmith.prospector";
    private const byte Blacksmith = 1;

    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly JobRules _jobRules = JobRules.From(Tables)!;
    private readonly ItemRules _itemRules = ItemRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private JobRepository _jobs = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _jobs = new JobRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A level-12 Blacksmith whose pick committed (1 job point); for a respec test, with Prospector 1 learned too.</summary>
    private async Task<CharacterRecord> PrepareAsync(JobTreeOp op)
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Tree{account}", "warrior", Spawn, Guid.NewGuid()));
        var character = (await _characters.LoadAsync(created.Character!.CharacterId))!;
        var inventory = new InventoryService(_itemRules, null);
        Assert.True(inventory.Attach(character.CharacterId, await _inventory.LoadInventoryAsync(character.CharacterId), character.ClassId, 12));
        var picker = new JobService(_jobRules, inventory, null);
        picker.Attach(character.CharacterId, JobRecord.Empty);
        Assert.Null(picker.Handle(character.CharacterId, new JobAsk(1, Blacksmith, JobSlot.Main, "job:pick", 12, false, true), 1));
        var pickSave = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, pickSave, picker.TakeBatch(character.CharacterId, pickSave)!,
            inventory.TakeBatch(character.CharacterId, pickSave))).Status);
        character = (await _characters.LoadAsync(character.CharacterId))!;
        if (op == JobTreeOp.Respec)
        {
            var learn = await ApplyAsync(character, JobTreeOp.Learn, "jobTree:setup");
            Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, learn.SaveId, learn.Jobs)).Status);
            character = (await _characters.LoadAsync(character.CharacterId))!;
        }
        return character;
    }

    /// <summary>A world's view of the loaded character with one tree operation applied: the save it would issue.</summary>
    private async Task<(JobService Service, JobBatch Jobs, Guid SaveId)> ApplyAsync(CharacterRecord character, JobTreeOp op, string key)
    {
        var service = new JobService(_jobRules, null, null);
        service.Attach(character.CharacterId, await _jobs.LoadAsync(character.CharacterId));
        Assert.Null(service.Handle(character.CharacterId, Ask(op, key), connection: 1)); // applied, waiting for its save
        var saveId = Guid.NewGuid();
        return (service, service.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private static JobTreeAsk Ask(JobTreeOp op, string key) =>
        new(1, op, Blacksmith, op == JobTreeOp.Learn ? Prospector : string.Empty, key, 12, Blocked: false, InSession: false);

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, JobBatch jobs, InventoryBatch? items = null, long? version = null) =>
        _characters.SaveAsync(character.CharacterId, character.State, version ?? character.Version, saveId, items, jobs: jobs);

    /// <summary>What the database holds of the Blacksmith's tree: its learned nodes, its unspent points and the tree operations.</summary>
    private async Task<(string Nodes, int Points, int TreeOps)> StoredAsync(long characterId)
    {
        var job = (await _jobs.LoadAsync(characterId)).Jobs.Single(j => j.JobId == "blacksmith");
        var ops = await _jobs.OpsAsync(characterId);
        return (string.Join(",", job.Nodes.Select(n => $"{n.NodeId}:{n.Level}")), job.Points, ops.Count(o => o.Op != JobOp.Pick));
    }

    /// <summary>The stored tree before and after the operation of each test.</summary>
    private static (string, int, int) Before(JobTreeOp op) => op == JobTreeOp.Learn ? ("", 1, 0) : ($"{Prospector}:1", 0, 1);

    private static (string, int, int) After(JobTreeOp op) => op == JobTreeOp.Learn ? ($"{Prospector}:1", 0, 1) : ("", 1, 2);

    [Fact]
    public async Task A_learn_commits_its_node_points_and_audit_row_together_and_counts_only_after_the_commit()
    {
        var character = await PrepareAsync(JobTreeOp.Learn);
        var (service, jobs, saveId) = await ApplyAsync(character, JobTreeOp.Learn, "jobTree:learn");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, jobs)).Status);

        Assert.Equal(After(JobTreeOp.Learn), await StoredAsync(character.CharacterId));
        var audit = (await _jobs.OpsAsync(character.CharacterId)).Single(o => o.Op == JobOp.Learn);
        Assert.Equal(("jobTree:learn", Prospector, 0, 1, 1, 0), (audit.OpKey, audit.NodeId, audit.FromLevel, audit.ToLevel, audit.PointsBefore,
            audit.PointsAfter));

        // Only the commit makes it count (rule 5): the bonus query reads committed nodes, and the answer comes then.
        Assert.Equal(0, service.Get(character.CharacterId, "blacksmith", JobBonusKind.YieldPercent, JobActivity.Harvest));
        CommittedJobOp? committed = null;
        service.OpCommitted = (_, c) => committed = c;
        service.Committed(character.CharacterId, saveId);
        Assert.Equal((JobReason.None, (byte)1, (ushort)0), (committed!.TreeAnswer!.Reason, committed.TreeAnswer.Level, committed.TreeAnswer.Points));
        Assert.Equal(4, service.Get(character.CharacterId, "blacksmith", JobBonusKind.YieldPercent, JobActivity.Harvest));

        // A relogin reads the node back; with no point left the next level is refused.
        var again = new JobService(_jobRules, null, null);
        again.Attach(character.CharacterId, await _jobs.LoadAsync(character.CharacterId));
        Assert.Equal(4, again.Get(character.CharacterId, "blacksmith", JobBonusKind.YieldPercent, JobActivity.Harvest));
        Assert.Equal(JobReason.NotEnoughJobPoints, again.Handle(character.CharacterId, Ask(JobTreeOp.Learn, "jobTree:more"), 1)!.Reason);
    }

    [Fact]
    public async Task A_respec_clears_the_tree_and_gives_every_point_back_in_one_save()
    {
        var character = await PrepareAsync(JobTreeOp.Respec);
        var (service, jobs, saveId) = await ApplyAsync(character, JobTreeOp.Respec, "jobTree:respec");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, jobs)).Status);
        service.Committed(character.CharacterId, saveId);

        Assert.Equal(After(JobTreeOp.Respec), await StoredAsync(character.CharacterId));
        var audit = (await _jobs.OpsAsync(character.CharacterId)).Single(o => o.Op == JobOp.Respec);
        Assert.Equal((1, 0, 1), (audit.FromLevel, audit.PointsBefore, audit.PointsAfter));
        Assert.Equal(0, service.Get(character.CharacterId, "blacksmith", JobBonusKind.YieldPercent, JobActivity.Harvest));
    }

    [Theory]
    [InlineData(JobTreeOp.Learn)]
    [InlineData(JobTreeOp.Respec)]
    public async Task A_double_submit_applies_it_once(JobTreeOp op)
    {
        var character = await PrepareAsync(op);
        var (service, jobs, saveId) = await ApplyAsync(character, op, "jobTree:double");
        Assert.Null(service.Handle(character.CharacterId, Ask(op, "jobTree:double"), 1));
        Assert.Equal(1, service.WaitingCount(character.CharacterId));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, saveId, jobs))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(After(op), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Theory]
    [InlineData(JobTreeOp.Learn)]
    [InlineData(JobTreeOp.Respec)]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more(JobTreeOp op)
    {
        var character = await PrepareAsync(op);
        var (_, jobs, saveId) = await ApplyAsync(character, op, "jobTree:retry");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, jobs)).Status);

        var retry = await SaveAsync(character, saveId, jobs); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(After(op), await StoredAsync(character.CharacterId));
    }

    [Theory]
    [InlineData(JobTreeOp.Learn)]
    [InlineData(JobTreeOp.Respec)]
    public async Task A_crash_between_apply_and_commit_writes_nothing_and_the_retry_applies_once(JobTreeOp op)
    {
        var character = await PrepareAsync(op);
        var (_, jobs, saveId) = await ApplyAsync(character, op, "jobTree:crash");
        // The character row, the audit row and the job record are written, then the learned nodes fail: all rolls back.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT OR DELETE ON job_nodes FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, saveId, jobs));

        Assert.Equal(Before(op), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON job_nodes");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, jobs)).Status);
        Assert.Equal(After(op), await StoredAsync(character.CharacterId));
    }

    [Theory]
    [InlineData(JobTreeOp.Learn)]
    [InlineData(JobTreeOp.Respec)]
    public async Task Two_operations_at_once_have_exactly_one_winner(JobTreeOp op)
    {
        var character = await PrepareAsync(op);
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each applying
        // the operation under its own key, from the same loaded version.
        var a = await ApplyAsync(character, op, "world-a");
        var b = await ApplyAsync(character, op, "world-b");

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.SaveId, a.Jobs)),
            Task.Run(() => SaveAsync(character, b.SaveId, b.Jobs)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(After(op), await StoredAsync(character.CharacterId));
    }
}
