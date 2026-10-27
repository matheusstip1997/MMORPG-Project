using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Progression;
using GameServer.Skills;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;
using Shared.Simulation.Skills;

namespace GameServer.Tests.Skills;

/// <summary>
/// The skill-tree transactions against a real PostgreSQL (WP-M2-5, spec 3.5 R-3.5-01/04, rule 5, R-7.1-01/02; CLAUDE.md:
/// double submit, retry, crash in the middle, concurrent access), for each of learn, upgrade, evolve, respec and class
/// advance. <see cref="SkillTreeService"/> applies the operation and builds the batch; the character save writes the points
/// in the row with the operation's <c>skill_ops</c> audit row, the class, the hotbar and the learned nodes in one transaction.
/// </summary>
public sealed class SkillTreePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(30, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100, SkillPoints: 30);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ProgressionRules _progressionRules = ProgressionRules.From(Tables)!;
    private readonly SkillRules _skillRules = SkillRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private ProgressionRepository _progression = null!;
    private SkillRepository _skills = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _progression = new ProgressionRepository(_realmDb);
        _skills = new SkillRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>One operation of each kind: the character's class and learned nodes before it, and the request.</summary>
    public static TheoryData<SkillTreeOp> Ops() => new() { SkillTreeOp.Learn, SkillTreeOp.Upgrade, SkillTreeOp.Evolve, SkillTreeOp.Respec, SkillTreeOp.Advance };

    private static (string ClassId, string[] Learned, string Target) Case(SkillTreeOp op) => op switch
    {
        SkillTreeOp.Learn => ("warrior", [], "warrior.piercingAttack"),
        SkillTreeOp.Upgrade => ("warrior", ["warrior.piercingAttack"], "warrior.piercingAttack"),
        SkillTreeOp.Evolve => ("knight", ["warrior.piercingAttack"], "knight.piercingSequence"),
        SkillTreeOp.Respec => ("warrior", ["warrior.piercingAttack", "warrior.increaseDef"], ""),
        SkillTreeOp.Advance => ("warrior", ["warrior.piercingAttack"], "knight"),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    private async Task<CharacterRecord> CreateAsync(SkillTreeOp op)
    {
        long account = _nextAccount++;
        var (classId, learned, _) = Case(op);
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", classId, Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        long id = result.Character!.CharacterId;
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("UPDATE characters SET level = 30, skill_points = 30 WHERE character_id = @id", new { id });
            foreach (var node in learned)
                await connection.ExecuteAsync(
                    "INSERT INTO character_skills (character_id, node_id, level, updated_at) VALUES (@id, @node, 1, now())", new { id, node });
        }
        return (await _characters.LoadAsync(id))!;
    }

    /// <summary>A world's view of the loaded character with <paramref name="op"/> applied: the save it would issue.</summary>
    private async Task<(SkillTreeService Service, CharacterState State, ProgressionBatch? Progression, SkillBatch Skills, Guid SaveId)>
        ApplyAsync(CharacterRecord character, SkillTreeOp op, string key)
    {
        var store = new ProgressionStore(_progressionRules);
        store.Attach(character.CharacterId, character.State, await _progression.LoadAsync(character.CharacterId));
        var service = new SkillTreeService(_skillRules, store);
        service.Attach(character.CharacterId, character.ClassId, await _skills.LoadAsync(character.CharacterId));
        var answer = service.Handle(character.CharacterId,
            new SkillTreeAsk(1, op, Case(op).Target, 0, key, Level: 30, Blocked: false), connection: 1);
        Assert.Null(answer); // applied, waiting for its save
        var saveId = Guid.NewGuid();
        var progress = store.Snapshot(character.CharacterId);
        var state = character.State with { Level = progress.Level, Exp = progress.Exp, Rank = progress.Rank, SkillPoints = progress.SkillPoints };
        return (service, state, store.TakeBatch(character.CharacterId, saveId), service.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, CharacterState state, Guid saveId, ProgressionBatch? progression,
        SkillBatch skills) =>
        _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: progression, skills: skills);

    private async Task<int> OpsCountAsync(long characterId) => (await _skills.OpsAsync(characterId)).Count;

    /// <summary>What the database holds for the character's skills: class, points and learned nodes.</summary>
    private async Task<(string ClassId, int Points, string Learned)> StoredAsync(long characterId)
    {
        var record = (await _characters.LoadAsync(characterId))!;
        var skills = await _skills.LoadAsync(characterId);
        return (record.ClassId, record.State.SkillPoints, string.Join(",", skills.Learned.Select(s => $"{s.NodeId}={s.Level}")));
    }

    private static (string ClassId, int Points, string Learned) Expected(SkillTreeOp op) => op switch
    {
        SkillTreeOp.Learn => ("warrior", 29, "warrior.piercingAttack=1"),
        SkillTreeOp.Upgrade => ("warrior", 29, "warrior.piercingAttack=2"),
        SkillTreeOp.Evolve => ("knight", 29, "knight.piercingSequence=1"), // the base's point is lost (R-3.5-04)
        SkillTreeOp.Respec => ("warrior", 32, ""), // two level-1 nodes back
        SkillTreeOp.Advance => ("knight", 30, "warrior.piercingAttack=1"), // nothing is reset (R-3.5-04)
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task An_operation_commits_its_points_nodes_class_and_audit_row_together(SkillTreeOp op)
    {
        var character = await CreateAsync(op);
        var before = await StoredAsync(character.CharacterId);
        var (service, state, progression, skills, saveId) = await ApplyAsync(character, op, $"skill:{op}");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, progression, skills)).Status);

        Assert.Equal(Expected(op), await StoredAsync(character.CharacterId));
        var audit = Assert.Single(await _skills.OpsAsync(character.CharacterId));
        Assert.Equal(($"skill:{op}", op, before.Points, Expected(op).Points, before.ClassId, Expected(op).ClassId),
            (audit.OpKey, audit.Op, audit.PointsBefore, audit.PointsAfter, audit.ClassFrom, audit.ClassTo));
        Assert.Equal(op == SkillTreeOp.Advance ? 1 : null, audit.AdvanceTier);

        // Only the commit makes it visible (rule 5) and answers it.
        Assert.Equal(1, service.WaitingCount(character.CharacterId));
        CommittedSkillOp? committed = null;
        service.OpCommitted = (_, c) => committed = c;
        service.Committed(character.CharacterId, saveId);
        Assert.Equal((SkillTreeReason.None, op), (committed!.Answer.Reason, committed.Answer.Op));
        Assert.Equal(Expected(op).ClassId, service.Visible(character.CharacterId)!.ClassId);
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task A_double_submit_applies_it_once(SkillTreeOp op)
    {
        var character = await CreateAsync(op);
        var (service, state, progression, skills, saveId) = await ApplyAsync(character, op, $"skill:{op}");
        // The same request again on the same connection: the first (pending) answer, nothing applied twice.
        Assert.Null(service.Handle(character.CharacterId, new SkillTreeAsk(1, op, Case(op).Target, 0, $"skill:{op}", 30, false), 1));
        Assert.Equal(1, service.WaitingCount(character.CharacterId));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => SaveAsync(character, state, saveId, progression, skills))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal(Expected(op), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more(SkillTreeOp op)
    {
        var character = await CreateAsync(op);
        var (_, state, progression, skills, saveId) = await ApplyAsync(character, op, $"skill:{op}");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, progression, skills)).Status);

        // The worker never heard the answer and retries the identical job.
        var retry = await SaveAsync(character, state, saveId, progression, skills);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal(Expected(op), await StoredAsync(character.CharacterId));
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task A_crash_in_the_middle_writes_nothing_and_the_retry_applies_once(SkillTreeOp op)
    {
        var character = await CreateAsync(op);
        var before = await StoredAsync(character.CharacterId);
        var (_, state, progression, skills, saveId) = await ApplyAsync(character, op, $"crash:{op}");
        // The learned set is replaced after the row update, the audit row, the class and the hotbar were written: it fails.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE DELETE ON character_skills FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, state, saveId, progression, skills));

        Assert.Equal(before, await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);
        Assert.Equal(0, await OpsCountAsync(character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON character_skills");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, progression, skills)).Status);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal(Expected(op), await StoredAsync(character.CharacterId));
    }

    [Theory]
    [MemberData(nameof(Ops))]
    public async Task Concurrent_operations_of_one_character_have_exactly_one_winner(SkillTreeOp op)
    {
        var character = await CreateAsync(op);
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each with its
        // own key, from the same loaded version.
        var a = await ApplyAsync(character, op, $"world-a:{op}");
        var b = await ApplyAsync(character, op, $"world-b:{op}");

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.State, a.SaveId, a.Progression, a.Skills)),
            Task.Run(() => SaveAsync(character, b.State, b.SaveId, b.Progression, b.Skills)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal(Expected(op), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_second_advance_into_the_same_tier_under_another_key_is_refused_by_the_database()
    {
        var character = await CreateAsync(SkillTreeOp.Advance);
        var first = await ApplyAsync(character, SkillTreeOp.Advance, "advance:a");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, first.State, first.SaveId, first.Progression, first.Skills)).Status);
        // A stale world at the new version with its own advance into tier 1 (a bug, or a lost lock): R-3.5-01, once ever.
        var stale = character with { Version = character.Version + 1 };
        var second = await ApplyAsync(stale, SkillTreeOp.Advance, "advance:b");

        var result = await _characters.SaveAsync(character.CharacterId, second.State, stale.Version, second.SaveId,
            progression: second.Progression, skills: second.Skills);

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
    }

    [Fact]
    public async Task The_hotbar_round_trips_and_a_character_without_one_gets_the_default_layout()
    {
        var character = await CreateAsync(SkillTreeOp.Respec); // piercingAttack and increaseDef learned, no hotbar saved
        var store = new ProgressionStore(_progressionRules);
        store.Attach(character.CharacterId, character.State, ProgressionRecord.Empty);
        var service = new SkillTreeService(_skillRules, store);
        service.Attach(character.CharacterId, character.ClassId, await _skills.LoadAsync(character.CharacterId));
        Assert.Equal(9, service.Visible(character.CharacterId)!.SlotOf("warrior.piercingAttack")); // its kit slot (R)

        var answer = service.Handle(character.CharacterId,
            new SkillTreeAsk(2, SkillTreeOp.AssignSlot, "warrior.piercingAttack", 3, "hotbar:1", 30, Blocked: true), 1);
        Assert.Equal(SkillTreeReason.None, answer!.Reason); // a hotbar change is not blocked in combat
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
            skills: service.TakeBatch(character.CharacterId, saveId))).Status);

        var reloaded = new SkillTreeService(_skillRules, store);
        reloaded.Attach(character.CharacterId, character.ClassId, await _skills.LoadAsync(character.CharacterId));
        Assert.Equal((3, string.Empty), (reloaded.Visible(character.CharacterId)!.SlotOf("warrior.piercingAttack"),
            reloaded.Visible(character.CharacterId)!.NodeAt(9)));
    }

    [Fact]
    public async Task The_migration_gives_existing_characters_their_class_kit()
    {
        // V0008 (user decision 2026-10-27): the M1 kit as learned level-1 nodes, for free; the rules list the same nodes.
        var realm = await _databases.CreateEmptyAsync("kit");
        var runner = new GameServer.Persistence.Migrations.MigrationRunner(_clock);
        var all = GameServer.Persistence.Migrations.MigrationCatalog.For(DatabaseKind.Realm);
        await runner.ApplyAsync(realm, all.Where(m => m.Version < 8).ToList());
        long id;
        await using (var connection = await realm.OpenConnectionAsync())
        {
            id = await connection.ExecuteScalarAsync<long>("""
                INSERT INTO characters (realm_id, account_id, slot, name, class_id, level, exp, world_id, pos_x_cm, pos_y_cm,
                    pos_z_cm, facing, hp, mp, stamina, version, create_request_id, created_at, updated_at)
                VALUES (1, 1, 0, 'Old', 'mage', 1, 0, '', 0, 0, 0, 0, 1, 1, 1, 1, gen_random_uuid(), now(), now())
                RETURNING character_id
                """);
        }

        await runner.ApplyAsync(realm, all);

        var characters = new CharacterRepository(realm, 1, _clock);
        var created = (await characters.LoadAsync(id))!;
        var learned = (await new SkillRepository(realm).LoadAsync(created.CharacterId)).Learned;
        Assert.Equal(_skillRules.KitNodes("mage").OrderBy(n => n, StringComparer.Ordinal), learned.Select(s => s.NodeId));
        Assert.All(learned, s => Assert.Equal(1, s.Level));
        Assert.Equal(1, (await characters.LoadAsync(created.CharacterId))!.State.SkillPoints);
    }
}
