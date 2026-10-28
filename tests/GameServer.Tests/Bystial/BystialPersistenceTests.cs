using Dapper;
using GameServer.Bystial;
using GameServer.Combat;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Progression;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Bystial;

/// <summary>
/// Bystial book transactions against a real PostgreSQL (spec 14.2, WP-M2-8; rule 5, R-7.1-01/02; CLAUDE.md: double
/// submit, retry, crash in the middle, concurrent access). <see cref="BystialService"/> builds the batches (cards as
/// absolute rows, operations with their audit fields); the character save writes them with the version-checked row in
/// one transaction (migration V0009).
/// </summary>
public sealed class BystialPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly CombatRules _combat;
    private readonly ProgressionRules _progressionRules;
    private readonly BystialRules _rules;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private BystialRepository _book = null!;
    private long _nextAccount = 1;

    public BystialPersistenceTests()
    {
        var tables = TestCombat.Tables();
        _combat = TestCombat.Rules(tables);
        _progressionRules = ProgressionRules.From(tables)!;
        _rules = BystialRules.From(tables, _combat.Config)!;
    }

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _book = new BystialRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    /// <summary>A book holding the character as loaded from the database (a world that owns it), with these kill counts.</summary>
    private async Task<BystialService> AttachAsync(long characterId, params (string Monster, long Kills)[] kills)
    {
        var store = new ProgressionStore(_progressionRules);
        store.Attach(characterId, Spawn, new ProgressionRecord("{\"v\":1}", kills.Select(k => new MonsterKillCount(k.Monster, k.Kills)).ToList()));
        var service = new BystialService(_rules, store, () => _combat, events: null);
        service.Attach(characterId, await _book.LoadAsync(characterId));
        service.CatchUp(characterId);
        return service;
    }

    private ushort NetIdOf(string monsterId) => (ushort)Assert.Single(_combat.Monsters, m => m.Id == monsterId).NetId;

    private static BystialBatch Take(BystialService service, long characterId, Guid saveId) =>
        service.TakeBatch(characterId, saveId) ?? throw new InvalidOperationException("no batch");

    private async Task<int> CountAsync(string table, long characterId)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>($"SELECT count(*) FROM {table} WHERE character_id = @characterId", new { characterId });
    }

    [Fact]
    public async Task A_save_commits_the_earned_card_and_its_operation_together()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id, ("m1.demon", 100));
        var saveId = Guid.NewGuid();

        var result = await _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: Take(service, id, saveId));

        Assert.Equal(SaveStatus.Saved, result.Status);
        Assert.Equal(new BystialCardRow("m1.demon", BystialTier.Normal, null), Assert.Single((await _book.LoadAsync(id)).Cards));
        var op = Assert.Single(await _book.OpsAsync(id));
        Assert.Equal((BystialService.TierOpKey(id, "m1.demon", BystialTier.Normal), (short)BystialOpKind.Earn, (short)0, (short)1, 100L, saveId),
            (op.OpKey, op.Kind, op.FromTier, op.ToTier, op.KillCount, op.SaveId));
    }

    [Fact]
    public async Task Place_and_swap_round_trip_and_reload_into_the_same_book()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id, ("m1.demon", 100), ("m1.alien", 100));
        long version = character.Version;
        async Task SaveAsync()
        {
            var saveId = Guid.NewGuid();
            var result = await _characters.SaveAsync(id, character.State, version, saveId, bystial: Take(service, id, saveId));
            Assert.Equal(SaveStatus.Saved, result.Status);
            version = result.Version;
            service.Committed(id, saveId);
        }
        await SaveAsync();
        Assert.True(service.TryChange(id, NetIdOf("m1.demon"), 0, Rank.F, "book:a:1").Applied);
        await SaveAsync();
        Assert.True(service.TryChange(id, NetIdOf("m1.alien"), 0, Rank.F, "book:a:2").Applied); // a swap: one slot at rank F
        await SaveAsync();

        Assert.Equal(new[] { new BystialCardRow("m1.alien", BystialTier.Normal, 0), new BystialCardRow("m1.demon", BystialTier.Normal, null) },
            (await _book.LoadAsync(id)).Cards);
        Assert.Equal(new short[] { 1, 1, 3, 4 }, (await _book.OpsAsync(id)).Select(o => o.Kind).OrderBy(k => k));
        var reloaded = await AttachAsync(id, ("m1.demon", 100), ("m1.alien", 100));
        Assert.Equal((BystialTier.Normal, 0), reloaded.CardOf(id, "m1.alien"));
        Assert.Null(reloaded.TakeBatch(id, Guid.NewGuid())); // nothing to catch up: the tiers are stored
        Assert.Equal(2, reloaded.BonusesOf(id).Stats.Count); // the alien's Def and build-up resistance from the committed book
    }

    [Fact]
    public async Task A_double_submit_of_one_save_applies_its_batch_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id, ("m1.demon", 100));
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: batch)));

        Assert.All(results, r => Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), r));
        Assert.Equal(1, await CountAsync("bystial_book", id));
        Assert.Equal(1, await CountAsync("bystial_book_ops", id));
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_is_recognised_and_writes_nothing_more()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id, ("m1.demon", 100));
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        await _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: batch);

        // The worker never saw the answer (a timeout): the scheduler resends the identical job (same save id, same batch).
        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(1, await CountAsync("bystial_book_ops", id));
        Assert.Single((await _book.LoadAsync(id)).Cards);
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_the_transaction_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var service = await AttachAsync(id, ("m1.demon", 100));
        Assert.True(service.TryChange(id, NetIdOf("m1.demon"), 0, Rank.F, "book:crash:1").Applied);
        var saveId = Guid.NewGuid();
        var batch = Take(service, id, saveId);
        // The card upsert fails after the row update and the operations were written: a crash mid-way.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_book_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_book_crash BEFORE INSERT ON bystial_book FOR EACH ROW EXECUTE FUNCTION test_book_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(id))!.Version);
        Assert.Equal(0, await CountAsync("bystial_book", id));
        Assert.Equal(0, await CountAsync("bystial_book_ops", id));

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_book_crash ON bystial_book");
        var retry = await _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(new BystialCardRow("m1.demon", BystialTier.Normal, 0), Assert.Single((await _book.LoadAsync(id)).Cards));
        Assert.Equal(2, await CountAsync("bystial_book_ops", id)); // the earn and the placement, once each
    }

    [Fact]
    public async Task Concurrent_writers_of_one_character_have_exactly_one_winner_and_nothing_is_duplicated()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each placing
        // a different card into the one slot, from the same loaded version.
        var a = await AttachAsync(id, ("m1.demon", 100), ("m1.alien", 100));
        var b = await AttachAsync(id, ("m1.demon", 100), ("m1.alien", 100));
        Assert.True(a.TryChange(id, NetIdOf("m1.demon"), 0, Rank.F, "world-a:1").Applied);
        Assert.True(b.TryChange(id, NetIdOf("m1.alien"), 0, Rank.F, "world-b:1").Applied);
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveA, bystial: batchA)),
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, saveB, bystial: batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        var cards = (await _book.LoadAsync(id)).Cards;
        Assert.Equal(2, cards.Count);
        Assert.Single(cards, c => c.Slot == 0);
        Assert.Equal(3, await CountAsync("bystial_book_ops", id)); // the winner's two earns and its placement
    }

    [Fact]
    public async Task Many_parallel_saves_at_one_version_apply_exactly_one_batch()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var batches = new List<(Guid SaveId, BystialBatch Batch)>();
        for (int i = 0; i < 8; i++)
        {
            var service = await AttachAsync(id, ("m1.demon", 100));
            var saveId = Guid.NewGuid();
            batches.Add((saveId, Take(service, id, saveId)));
        }

        var results = await Task.WhenAll(batches.Select(b =>
            Task.Run(() => _characters.SaveAsync(id, character.State, character.Version, b.SaveId, bystial: b.Batch))));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(1, await CountAsync("bystial_book", id));
        Assert.Equal(1, await CountAsync("bystial_book_ops", id));
    }

    [Fact]
    public async Task A_tier_step_already_in_the_database_refuses_the_save_as_a_duplicate()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var first = await AttachAsync(id, ("m1.demon", 100));
        var saveId = Guid.NewGuid();
        var saved = await _characters.SaveAsync(id, character.State, character.Version, saveId, bystial: Take(first, id, saveId));

        // A stale book (attached before that save) steps the same tier again on the newer version: the key is unique for ever.
        var stale = new BystialService(_rules, AttachedStore(id, 100), () => _combat, events: null);
        stale.Attach(id, BystialRecord.Empty);
        stale.CatchUp(id);
        var staleSave = Guid.NewGuid();
        var result = await _characters.SaveAsync(id, character.State, saved.Version, staleSave, bystial: Take(stale, id, staleSave));

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Equal(saved.Version, (await _characters.LoadAsync(id))!.Version);
        Assert.Equal(1, await CountAsync("bystial_book_ops", id));
    }

    private ProgressionStore AttachedStore(long characterId, long demonKills)
    {
        var store = new ProgressionStore(_progressionRules);
        store.Attach(characterId, Spawn, new ProgressionRecord("{\"v\":1}", [new MonsterKillCount("m1.demon", demonKills)]));
        return store;
    }

    [Fact]
    public async Task A_slot_holds_one_card_in_the_database_too()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var bad = new BystialBatch(
            [new BystialCardRow("m1.demon", BystialTier.Normal, 0), new BystialCardRow("m1.alien", BystialTier.Normal, 0)],
            [new BystialOpRecord("bad:1", BystialOpKind.Place, "m1.demon", null, BystialTier.Normal, BystialTier.Normal, 0, null)]);

        await Assert.ThrowsAsync<PostgresException>(() =>
            _characters.SaveAsync(id, character.State, character.Version, Guid.NewGuid(), bystial: bad));

        Assert.Equal(0, await CountAsync("bystial_book", id));
        Assert.Equal(BookLimits.MaxSlots, 16); // the migration's slot check matches the wire cap
    }
}
