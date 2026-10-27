using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Progression;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Progression;

/// <summary>
/// Progression saves and the rank-up transaction against a real PostgreSQL (WP-M2-3, spec 3.6, rule 5, R-7.1-01/02;
/// CLAUDE.md: double submit, retry, crash in the middle, concurrent access). <see cref="ProgressionStore"/> builds the
/// batches; the character save writes them with the version-checked row (level, exp, rank, skill points), the counters,
/// the kill counts and the <c>rank_history</c> audit row in one transaction.
/// </summary>
public sealed class ProgressionPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ProgressionRules _rules = ProgressionRules.From(TestCombat.Tables())!;
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

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    /// <summary>A store holding the character as loaded from the database (a world that owns it).</summary>
    private async Task<ProgressionStore> AttachAsync(CharacterRecord character)
    {
        var store = new ProgressionStore(_rules);
        store.Attach(character.CharacterId, character.State, await _progression.LoadAsync(character.CharacterId));
        return store;
    }

    /// <summary>Counters that meet the placeholder F to E requirements (60 kills, 2 types, 1 outpost).</summary>
    private static void MeetFRequirements(ProgressionStore store, long id)
    {
        for (int i = 0; i < 59; i++)
            store.RecordKill(id, "m1.demon", boss: false);
        store.RecordKill(id, "m1.alien", boss: false);
        store.Record(id, AchievementCategory.OutpostsVisited, "outpost.gate");
    }

    private static CharacterState StateOf(ProgressionStore store, CharacterRecord character)
    {
        var p = store.Snapshot(character.CharacterId);
        return character.State with { Level = p.Level, Exp = p.Exp, Rank = p.Rank, SkillPoints = p.SkillPoints };
    }

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    /// <summary>A rank-up ready to save: the store, its batch and the state the save writes.</summary>
    private async Task<(ProgressionStore Store, ProgressionBatch Batch, CharacterState State, Guid SaveId)> RankUpAsync(
        CharacterRecord character, string key)
    {
        var store = await AttachAsync(character);
        MeetFRequirements(store, character.CharacterId);
        var ranks = new RankService(store, _ => null);
        Assert.True(ranks.TryRankUp(character.CharacterId, key).Accepted);
        var saveId = Guid.NewGuid();
        var batch = store.TakeBatch(character.CharacterId, saveId)!;
        return (store, batch, StateOf(store, character), saveId);
    }

    [Fact]
    public async Task A_new_character_has_rank_F_and_one_skill_point()
    {
        var character = await CreateAsync();

        var loaded = (await _characters.LoadAsync(character.CharacterId))!;

        Assert.Equal((Rank.F, 1), (loaded.State.Rank, loaded.State.SkillPoints));
        Assert.Equal(ProgressionRecord.Empty.KillCounts, (await _progression.LoadAsync(character.CharacterId)).KillCounts);
    }

    [Fact]
    public async Task Level_exp_points_counters_and_kill_counts_round_trip_through_a_save()
    {
        var character = await CreateAsync();
        var store = await AttachAsync(character);
        long id = character.CharacterId;
        store.AddExp(id, 172 + 50, out _);
        store.RecordKill(id, "m1.demon", boss: false);
        store.RecordKill(id, "m1.demon", boss: false);
        store.RecordKill(id, "m1.cyclopsBoss", boss: true);
        store.Record(id, AchievementCategory.OutpostsVisited, "outpost.gate");
        var saveId = Guid.NewGuid();

        var result = await _characters.SaveAsync(id, StateOf(store, character), character.Version, saveId,
            progression: store.TakeBatch(id, saveId));

        Assert.Equal(SaveStatus.Saved, result.Status);
        var loaded = (await _characters.LoadAsync(id))!;
        var reloaded = new ProgressionStore(_rules);
        reloaded.Attach(id, loaded.State, await _progression.LoadAsync(id));
        Assert.Equal(new ProgressSnapshot(2, 50, Rank.F, 2), reloaded.Snapshot(id));
        Assert.Equal(2, reloaded.KillCount(id, "m1.demon"));
        Assert.Equal(1, reloaded.KillCount(id, "m1.cyclopsBoss"));
        Assert.Equal(3, reloaded.CounterValue(id, _rules.Counter(AchievementCategory.MonsterKills)!));
        Assert.Equal(1, reloaded.CounterValue(id, _rules.Counter(AchievementCategory.BossKills)!));
        Assert.Equal(1, reloaded.CounterValue(id, _rules.Counter(AchievementCategory.OutpostsVisited)!));
        Assert.Equal(2, reloaded.CounterValue(id, _rules.Counter(AchievementCategory.MonsterTypesKilled)!));

        // A later batch writes absolute counts over the earlier ones.
        reloaded.RecordKill(id, "m1.demon", boss: false);
        var second = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(id, loaded.State, loaded.Version, second,
            progression: reloaded.TakeBatch(id, second))).Status);
        Assert.Contains(new MonsterKillCount("m1.demon", 3), (await _progression.LoadAsync(id)).KillCounts);
    }

    [Fact]
    public async Task A_rank_up_commits_the_rank_its_audit_row_and_the_counters_together()
    {
        var character = await CreateAsync();
        var (store, batch, state, saveId) = await RankUpAsync(character, "rankUp:a");

        var result = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: batch);

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(Rank.E, (await _characters.LoadAsync(character.CharacterId))!.State.Rank);
        var history = Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
        Assert.Equal(("rankUp:a", (short)Rank.F, (short)Rank.E, saveId), (history.OpKey, history.FromRank, history.ToRank, history.SaveId));
        Assert.Contains("\"monsterKills\": 60", history.CountersJson); // jsonb text form
        Assert.Equal(2, await CountAsync("monster_kill_counts", character.CharacterId));

        Assert.False(store.IsRankUpCommitted(character.CharacterId, "rankUp:a"));
        store.Committed(character.CharacterId, saveId);
        Assert.True(store.IsRankUpCommitted(character.CharacterId, "rankUp:a"));
    }

    [Fact]
    public async Task A_double_submit_of_a_rank_up_save_applies_it_once()
    {
        var character = await CreateAsync();
        var (_, batch, state, saveId) = await RankUpAsync(character, "rankUp:a");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            Task.Run(() => _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: batch))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more()
    {
        var character = await CreateAsync();
        var (_, batch, state, saveId) = await RankUpAsync(character, "rankUp:a");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId,
            progression: batch)).Status);

        // The worker never heard the answer and retries the identical job.
        var retry = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_the_rank_up_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var (_, batch, state, saveId) = await RankUpAsync(character, "crash:rankUp");
        // The kill count insert fails after the row update, the history row and the counters were written: a crash mid-way.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON monster_kill_counts FOR EACH ROW EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() =>
            _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: batch));

        var unchanged = (await _characters.LoadAsync(character.CharacterId))!;
        Assert.Equal((character.Version, Rank.F), (unchanged.Version, unchanged.State.Rank));
        Assert.Empty(await _progression.RankHistoryAsync(character.CharacterId));
        Assert.Equal(0, await CountAsync("monster_kill_counts", character.CharacterId));
        Assert.Empty((await _progression.LoadAsync(character.CharacterId)).KillCounts);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON monster_kill_counts");
        var retry = await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId, progression: batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
        Assert.Equal(Rank.E, (await _characters.LoadAsync(character.CharacterId))!.State.Rank);
    }

    [Fact]
    public async Task Concurrent_rank_ups_of_one_character_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each with
        // its own rank-up key, from the same loaded version.
        var (_, batchA, stateA, saveA) = await RankUpAsync(character, "world-a:rankUp");
        var (_, batchB, stateB, saveB) = await RankUpAsync(character, "world-b:rankUp");

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(character.CharacterId, stateA, character.Version, saveA, progression: batchA)),
            Task.Run(() => _characters.SaveAsync(character.CharacterId, stateB, character.Version, saveB, progression: batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_second_rank_up_of_the_same_step_under_another_key_is_refused_by_the_database()
    {
        var character = await CreateAsync();
        var (_, batch, state, saveId) = await RankUpAsync(character, "rankUp:a");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(character.CharacterId, state, character.Version, saveId,
            progression: batch)).Status);
        // A stale world at the new version with its own F to E rank-up (a bug, or a lost lock): the step is applied once ever.
        var stale = await RankUpAsync(character with { Version = character.Version + 1 }, "rankUp:b");

        var result = await _characters.SaveAsync(character.CharacterId, stale.State, character.Version + 1, stale.SaveId,
            progression: stale.Batch);

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Single(await _progression.RankHistoryAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Fact]
    public async Task The_migration_gives_existing_characters_one_skill_point_per_level()
    {
        var realm = await _databases.CreateEmptyAsync("backfill");
        var runner = new MigrationRunner(_clock);
        var all = MigrationCatalog.For(DatabaseKind.Realm);
        await runner.ApplyAsync(realm, all.Where(m => m.Version < 5).ToList());
        var characters = new CharacterRepository(realm, 1, _clock);
        await using (var connection = await realm.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                INSERT INTO characters (realm_id, account_id, slot, name, class_id, level, exp, world_id, pos_x_cm, pos_y_cm,
                    pos_z_cm, facing, hp, mp, stamina, version, create_request_id, created_at, updated_at)
                VALUES (1, 1, 0, 'Old', 'warrior', 7, 10, '', 0, 0, 0, 0, 1, 1, 1, 1, gen_random_uuid(), now(), now())
                """);
        }

        await runner.ApplyAsync(realm, all);

        var loaded = Assert.Single(await characters.ListByAccountAsync(1));
        Assert.Equal((7, Rank.F, 7), (loaded.State.Level, loaded.State.Rank, loaded.State.SkillPoints));
    }
}
