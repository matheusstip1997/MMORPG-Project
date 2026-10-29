using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Progression;
using GameServer.Skills;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;
using Shared.Simulation.Skills;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Progression;

/// <summary>
/// The free attribute point transactions against a real PostgreSQL (WP-M2-13, spec 3.4 R-3.4-08, rule 5, R-7.1-01/02;
/// CLAUDE.md: double submit, retry, crash in the middle, concurrent access). <see cref="ProgressionStore"/> applies a
/// placement and builds the batch; the character save writes the unspent and placed points in the row with the
/// placement's <c>attribute_ops</c> audit row in one transaction. A skill respec's reset rides on the respec's save. Realm
/// migration V0011 gives existing characters their level's points.
/// </summary>
public sealed class AttributePointsPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(5, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100, SkillPoints: 5, AttributePoints: 4);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ProgressionRules _progressionRules = ProgressionRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private ProgressionRepository _progression = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _progression = new ProgressionRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync(int strPlaced = 0)
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        long id = result.Character!.CharacterId;
        if (strPlaced > 0)
        {
            await using var connection = await _realmDb.OpenConnectionAsync();
            await connection.ExecuteAsync("UPDATE characters SET str_placed = @strPlaced WHERE character_id = @id", new { id, strPlaced });
        }
        return (await _characters.LoadAsync(id))!;
    }

    /// <summary>A world's view of the loaded character with one point placed on <paramref name="attribute"/>: the save it would issue.</summary>
    private async Task<(ProgressionStore Store, CharacterState State, ProgressionBatch Batch, Guid SaveId)> PlaceAsync(
        CharacterRecord character, string key, int attribute = Attributes.Dex)
    {
        var store = new ProgressionStore(_progressionRules);
        store.Attach(character.CharacterId, character.State, await _progression.LoadAsync(character.CharacterId));
        Assert.Null(store.PlaceAttribute(character.CharacterId, attribute, key, blocked: false, baseValue: 10, statMax: 100_000));
        var saveId = Guid.NewGuid();
        return (store, Capture(store, character), store.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    /// <summary>What <c>SessionManager.Capture</c> writes: the store's working values in the row.</summary>
    private static CharacterState Capture(ProgressionStore store, CharacterRecord character)
    {
        var progress = store.Snapshot(character.CharacterId);
        var a = store.WorkingAttributes(character.CharacterId);
        return character.State with
        {
            SkillPoints = progress.SkillPoints, AttributePoints = a.Unspent, StrPlaced = a.Str, DexPlaced = a.Dex, StaPlaced = a.Sta,
            IntPlaced = a.Int,
        };
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, CharacterState state, Guid saveId, ProgressionBatch progression,
        SkillBatch? skills = null) =>
        _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: progression, skills: skills);

    /// <summary>The unspent and placed points the database holds (STR, DEX, STA, INT).</summary>
    private async Task<(int Unspent, int Str, int Dex, int Sta, int Int)> StoredAsync(long characterId)
    {
        var s = (await _characters.LoadAsync(characterId))!.State;
        return (s.AttributePoints, s.StrPlaced, s.DexPlaced, s.StaPlaced, s.IntPlaced);
    }

    private async Task<int> OpsCountAsync(long characterId) => (await _progression.AttributeOpsAsync(characterId)).Count;

    [Fact]
    public async Task A_placement_commits_its_points_and_audit_row_together_and_is_visible_only_after()
    {
        var character = await CreateAsync();
        var (store, state, batch, saveId) = await PlaceAsync(character, "attr:one");
        Assert.Equal(new AttributeSnapshot(4, 0, 0, 0, 0), store.TryGetAttributes(character.CharacterId, out var before) ? before : default);

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, batch)).Status);

        Assert.Equal((3, 0, 1, 0, 0), await StoredAsync(character.CharacterId));
        var audit = Assert.Single(await _progression.AttributeOpsAsync(character.CharacterId));
        Assert.Equal(("attr:one", (short)1, (short?)Attributes.Dex, 1, 4, 3, saveId),
            (audit.OpKey, audit.Op, audit.Attribute, audit.Points, audit.PointsBefore, audit.PointsAfter, audit.SaveId));
        Assert.Equal(new[] { 0, 0, 0, 0 }, audit.PlacedBefore);
        Assert.Equal(new[] { 0, 1, 0, 0 }, audit.PlacedAfter);

        // Only the commit makes it visible (rule 5) and answers it.
        AttributeOpRecord? committed = null;
        store.AttributeOpCommitted = (_, op) => committed = op;
        store.Committed(character.CharacterId, saveId);
        Assert.Equal("attr:one", committed!.OpKey);
        Assert.True(store.TryGetAttributes(character.CharacterId, out var after));
        Assert.Equal(new AttributeSnapshot(3, 0, 1, 0, 0), after);
    }

    [Fact]
    public async Task A_double_submit_applies_it_once()
    {
        var character = await CreateAsync();
        var (store, state, batch, saveId) = await PlaceAsync(character, "attr:double");
        // The same request again on the same connection while it waits: no answer yet, nothing applied twice.
        Assert.Null(store.PlaceAttribute(character.CharacterId, Attributes.Dex, "attr:double", false, 10, 100_000));
        Assert.Equal(new AttributeSnapshot(3, 0, 1, 0, 0), store.WorkingAttributes(character.CharacterId));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, state, saveId, batch))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal((3, 0, 1, 0, 0), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);

        // After the commit a repeat gets the first answer and still changes nothing.
        store.Committed(character.CharacterId, saveId);
        Assert.Equal(AttributeReason.None, store.PlaceAttribute(character.CharacterId, Attributes.Dex, "attr:double", false, 10, 100_000));
        Assert.Equal(new AttributeSnapshot(3, 0, 1, 0, 0), store.WorkingAttributes(character.CharacterId));
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more()
    {
        var character = await CreateAsync();
        var (_, state, batch, saveId) = await PlaceAsync(character, "attr:retry");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, batch)).Status);

        // The worker never heard the answer and retries the identical job.
        var retry = await SaveAsync(character, state, saveId, batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal((3, 0, 1, 0, 0), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task The_same_key_under_another_save_is_refused_by_the_database()
    {
        var character = await CreateAsync();
        var first = await PlaceAsync(character, "attr:same");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, first.State, first.SaveId, first.Batch)).Status);
        // A world restarted with the same key (it cannot: the key holds the run id) at the new version.
        var stale = character with { Version = character.Version + 1, State = character.State with { AttributePoints = 3, DexPlaced = 1 } };
        var second = await PlaceAsync(stale, "attr:same");

        var result = await _characters.SaveAsync(character.CharacterId, second.State, stale.Version, second.SaveId, progression: second.Batch);

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Equal((3, 0, 1, 0, 0), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var (_, state, batch, saveId) = await PlaceAsync(character, "attr:crash");
        // The audit row is written after the row update: it fails, and the row update rolls back with it.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON attribute_ops FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, state, saveId, batch));

        Assert.Equal((4, 0, 0, 0, 0), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);
        Assert.Equal(0, await OpsCountAsync(character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON attribute_ops");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, batch)).Status);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        Assert.Equal((3, 0, 1, 0, 0), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task Concurrent_placements_of_one_character_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each with its
        // own key, from the same loaded version.
        var a = await PlaceAsync(character, "world-a", Attributes.Str);
        var b = await PlaceAsync(character, "world-b", Attributes.Int);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.State, a.SaveId, a.Batch)),
            Task.Run(() => SaveAsync(character, b.State, b.SaveId, b.Batch)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(1, await OpsCountAsync(character.CharacterId));
        var stored = await StoredAsync(character.CharacterId);
        Assert.Equal(3, stored.Unspent);
        Assert.Equal(1, stored.Str + stored.Int);
    }

    [Fact]
    public async Task A_respec_writes_its_skill_op_and_the_attribute_reset_in_one_transaction()
    {
        // R-3.4-08 (user decision 2026-10-29): the respec gives every placed point back in its own save.
        var character = await CreateAsync(strPlaced: 2);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(
                "INSERT INTO character_skills (character_id, node_id, level, updated_at) VALUES (@id, 'warrior.piercingAttack', 1, now())",
                new { id = character.CharacterId });
        }
        var store = new ProgressionStore(_progressionRules);
        store.Attach(character.CharacterId, character.State, await _progression.LoadAsync(character.CharacterId));
        var service = new SkillTreeService(SkillRules.From(Tables)!, store);
        service.Attach(character.CharacterId, character.ClassId, await new SkillRepository(_realmDb).LoadAsync(character.CharacterId));
        Assert.Null(service.Handle(character.CharacterId, new SkillTreeAsk(1, SkillTreeOp.Respec, "", 0, "skill:respec", 5, false), 1));
        var saveId = Guid.NewGuid();
        var state = Capture(store, character);
        var progression = store.TakeBatch(character.CharacterId, saveId)!;
        var skills = service.TakeBatch(character.CharacterId, saveId)!;
        var reset = Assert.Single(progression.AttributeOpList);
        Assert.Equal(("skill:respec:attributes", AttributeOpKind.Reset, 2), (reset.OpKey, reset.Kind, reset.Points));

        // A crash in the skill part rolls the reset back too.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE DELETE ON character_skills FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }
        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, state, saveId, progression, skills));
        Assert.Equal((4, 2, 0, 0, 0), await StoredAsync(character.CharacterId));
        Assert.Equal(0, await OpsCountAsync(character.CharacterId));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON character_skills");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, state, saveId, progression, skills)).Status);

        Assert.Equal((6, 0, 0, 0, 0), await StoredAsync(character.CharacterId));
        var audit = Assert.Single(await _progression.AttributeOpsAsync(character.CharacterId));
        Assert.Equal(((short)2, (short?)null, 2, 4, 6), (audit.Op, audit.Attribute, audit.Points, audit.PointsBefore, audit.PointsAfter));
        Assert.Equal(new[] { 2, 0, 0, 0 }, audit.PlacedBefore);
        Assert.Single(await new SkillRepository(_realmDb).OpsAsync(character.CharacterId));
    }

    [Fact]
    public async Task The_migration_gives_existing_characters_their_level_s_points_unspent()
    {
        // V0011 (user decision 2026-10-29): level - 1 unspent points, nothing placed.
        var realm = await _databases.CreateEmptyAsync("attr");
        var runner = new GameServer.Persistence.Migrations.MigrationRunner(_clock);
        var all = GameServer.Persistence.Migrations.MigrationCatalog.For(DatabaseKind.Realm);
        await runner.ApplyAsync(realm, all.Where(m => m.Version < 11).ToList());
        var ids = new List<long>();
        await using (var connection = await realm.OpenConnectionAsync())
        {
            foreach (var (name, level) in new[] { ("One", 1), ("Twelve", 12), ("Thirty", 30) })
            {
                ids.Add(await connection.ExecuteScalarAsync<long>("""
                    INSERT INTO characters (realm_id, account_id, slot, name, class_id, level, exp, world_id, pos_x_cm, pos_y_cm,
                        pos_z_cm, facing, hp, mp, stamina, version, create_request_id, created_at, updated_at)
                    VALUES (1, @account, 0, @name, 'warrior', @level, 0, '', 0, 0, 0, 0, 1, 1, 1, 1, gen_random_uuid(), now(), now())
                    RETURNING character_id
                    """, new { account = ids.Count + 1L, name, level }));
            }
        }

        await runner.ApplyAsync(realm, all);

        var characters = new CharacterRepository(realm, 1, _clock);
        var points = new List<(int, int, int, int, int)>();
        foreach (var id in ids)
        {
            var s = (await characters.LoadAsync(id))!.State;
            points.Add((s.AttributePoints, s.StrPlaced, s.DexPlaced, s.StaPlaced, s.IntPlaced));
        }
        Assert.Equal(new[] { (0, 0, 0, 0, 0), (11, 0, 0, 0, 0), (29, 0, 0, 0, 0) }, points);
    }
}
