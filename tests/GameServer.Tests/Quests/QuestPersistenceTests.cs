using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Quests;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Quests;

/// <summary>
/// Run saves and the run transactions against a real PostgreSQL (WP-M2-6, spec 5.5, 7.1, rule 5, R-7.1-01/02; CLAUDE.md: double
/// submit, retry, crash in the middle, concurrent access, for accept, progress, forfeit and claim). <see cref="RunStore"/> builds
/// the batches; the character save writes them with the version-checked row (its exp), the inventory batch of a delivery and the
/// <c>quest_run_audit</c> rows in one transaction.
/// </summary>
public sealed class QuestPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly QuestRules _rules;
    private readonly ItemRules _items;
    private readonly MonsterDef _demon;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private QuestRepository _quests = null!;
    private InventoryRepository _inventory = null!;
    private long _nextAccount = 1;

    public QuestPersistenceTests()
    {
        var tables = TestCombat.Tables();
        _rules = QuestRules.From(tables)!;
        _items = ItemRules.From(tables)!;
        Assert.True(TestCombat.Rules(tables).TryGetMonster("m1.demon", out _demon!));
    }

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _quests = new QuestRepository(_realmDb);
        _inventory = new InventoryRepository(_realmDb);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Hero{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    /// <summary>A store holding the character's open runs as loaded from the database (a world that owns it).</summary>
    private async Task<RunStore> AttachAsync(long characterId)
    {
        var store = new RunStore(_rules, id => id == _demon.Id ? _demon.NetId : 0,
            id => _items.TryGetItem(id, out var item) ? item.NetId : 0);
        store.Attach(characterId, await _quests.LoadOpenAsync(characterId));
        return store;
    }

    private BoardOffer Offer(QuestKind kind, int count, long reward, ItemDef? item = null) =>
        new(0, _rules.Kinds.Single(k => k.Kind == kind), new QuestCandidate(_demon, 1, 40, Array.Empty<ItemDef>(), null), item,
            count, reward, 100);

    private static QuestRunResult Answer(QuestRunAction action) => new() { Action = action };

    /// <summary>An accepted two-step kill run (rewards 100 and 200) under <paramref name="key"/>, and its batch.</summary>
    private async Task<(RunStore Store, QuestBatch Batch, Guid SaveId)> AcceptAsync(CharacterRecord character, string key,
        Guid? runId = null, bool missedOpenRuns = false)
    {
        var store = await AttachAsync(missedOpenRuns ? 0 : character.CharacterId);
        if (missedOpenRuns)
            store.Attach(character.CharacterId, Array.Empty<RunRecord>());
        store.Accept(character.CharacterId, runId ?? Guid.NewGuid(), "outpost.gate", 7,
            [Offer(QuestKind.Kill, 2, 100), Offer(QuestKind.Kill, 1, 200)], _clock.UtcNowMs, key, Answer(QuestRunAction.Accept));
        var saveId = Guid.NewGuid();
        return (store, store.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, long version, Guid saveId, QuestBatch? quests,
        CharacterState? state = null, InventoryBatch? inventory = null) =>
        _characters.SaveAsync(character.CharacterId, state ?? character.State, version, saveId, inventory, quests: quests);

    private async Task<int> CountAsync(string sql, object parameters)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(sql, parameters);
    }

    private async Task CrashOnAsync(string table)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync($"""
            CREATE OR REPLACE FUNCTION test_crash() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION test_crash();
            """);
    }

    private async Task HealAsync(string table)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync($"DROP TRIGGER test_crash ON {table}");
    }

    // --- Accept -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_accepted_run_round_trips_with_its_locked_steps_and_its_audit_row()
    {
        var character = await CreateAsync();
        var (_, batch, saveId) = await AcceptAsync(character, "accept:1");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version, saveId, batch)).Status);

        var run = Assert.Single(await _quests.LoadOpenAsync(character.CharacterId));
        Assert.Equal((RunState.Active, "outpost.gate", 7L), (run.State, run.OutpostMarkerId, run.BoardEpoch));
        Assert.Equal(new[] { (QuestKind.Kill, 2, 0, 100L), (QuestKind.Kill, 1, 0, 200L) },
            run.Steps.Select(s => (s.Kind, s.Required, s.Progress, s.RewardExp)));
        Assert.Equal("m1.demon", run.Steps[0].MonsterDefId);
        var audit = Assert.Single(await _quests.AuditAsync(character.CharacterId));
        Assert.Equal(("accept:1", (short)QuestRunAction.Accept, saveId), (audit.OpKey, audit.Action, audit.SaveId));
    }

    [Fact]
    public async Task A_double_submit_of_an_accept_applies_it_once()
    {
        var character = await CreateAsync();
        var (_, batch, saveId) = await AcceptAsync(character, "accept:twice");
        await SaveAsync(character, character.Version, saveId, batch);

        // The same operation again in a later save (a bug or a replay): the audit key refuses it and the save writes nothing.
        var again = await SaveAsync(character, character.Version + 1, Guid.NewGuid(), batch);

        Assert.Equal(SaveStatus.DuplicateOperation, again.Status);
        Assert.Single(await _quests.LoadAllAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Fact]
    public async Task A_retry_of_an_accept_that_did_commit_writes_nothing_more()
    {
        var character = await CreateAsync();
        var (_, batch, saveId) = await AcceptAsync(character, "accept:retry");
        await SaveAsync(character, character.Version, saveId, batch);

        // The worker never heard the answer and retries the identical job.
        var retry = await SaveAsync(character, character.Version, saveId, batch);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Single(await _quests.AuditAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_an_accept_writes_nothing_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        var (_, batch, saveId) = await AcceptAsync(character, "accept:crash");
        await CrashOnAsync("quest_run_audit"); // after the row update, the run and its steps

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, character.Version, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);
        Assert.Empty(await _quests.LoadAllAsync(character.CharacterId));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM quest_run_steps", new { }));

        await HealAsync("quest_run_audit");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version, saveId, batch)).Status);
        Assert.Single(await _quests.LoadOpenAsync(character.CharacterId));
    }

    [Fact]
    public async Task Concurrent_accepts_of_one_character_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), from one version.
        var (_, batchA, saveA) = await AcceptAsync(character, "world-a:accept");
        var (_, batchB, saveB) = await AcceptAsync(character, "world-b:accept");

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, character.Version, saveA, batchA)),
            Task.Run(() => SaveAsync(character, character.Version, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Single(await _quests.LoadAllAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_second_open_run_of_one_character_is_refused_by_the_database()
    {
        var character = await CreateAsync();
        var (_, first, saveA) = await AcceptAsync(character, "accept:first");
        await SaveAsync(character, character.Version, saveA, first);
        // A stale world at the new version that never loaded the first run (a bug, or a lost lock) accepts another.
        var (_, second, saveB) = await AcceptAsync(character, "accept:second", missedOpenRuns: true);

        var result = await SaveAsync(character, character.Version + 1, saveB, second);

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Single(await _quests.LoadAllAsync(character.CharacterId));
    }

    // --- Progress -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Kill_progress_is_saved_as_absolute_values_and_survives_a_reload()
    {
        var character = await CreateAsync();
        var (store, batch, saveId) = await AcceptAsync(character, "accept:progress");
        await SaveAsync(character, character.Version, saveId, batch);
        store.Committed(character.CharacterId, saveId);

        store.Advance(character.CharacterId, 0, 1);
        store.Advance(character.CharacterId, 1, 1);
        var progressSave = Guid.NewGuid();
        var progress = store.TakeBatch(character.CharacterId, progressSave)!;
        Assert.Empty(progress.Audit); // kills are class 2 gains without audit rows, like kill exp
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, progressSave, progress)).Status);
        // A retry of the identical job writes the same values.
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, progressSave, progress)).Status);

        var reloaded = await AttachAsync(character.CharacterId);
        var run = reloaded.Active(character.CharacterId)!;
        Assert.Equal(new[] { (1, RunStepState.Active), (1, RunStepState.Done) }, run.Steps.Select(s => (s.Progress, s.State)));
        Assert.Equal(_demon.NetId, run.Steps[0].MonsterNetId);
    }

    [Fact]
    public async Task A_delivery_commits_its_item_removal_and_its_progress_together_or_not_at_all()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        // A return step (user decision 2026-10-30: boards offer no deliver kind) hands in quest items from the Quest bag.
        Assert.True(_items.TryGetItem("m2.questDemonSigil", out var sigil));
        int questBag = _items.ReceiverOf(sigil.Category)!.Code;
        var inventory = new InventoryService(_items, events: null);
        Assert.True(inventory.Attach(id, await _inventory.LoadAsync(id)));
        inventory.TryAdd(id, sigil, 0, 5, "seed:sigils", InventoryReason.Admin, "test");
        var store = await AttachAsync(id);
        store.Accept(id, Guid.NewGuid(), "outpost.gate", 7, [Offer(QuestKind.ReturnItem, 3, 120, sigil)], _clock.UtcNowMs, "accept:d",
            Answer(QuestRunAction.Accept));
        var seedSave = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version, seedSave, store.TakeBatch(id, seedSave),
            inventory: inventory.TakeBatch(id, seedSave))).Status);
        inventory.Committed(id, seedSave);
        store.Committed(id, seedSave);

        // The delivery: three sigils out through InventoryService, the step done, one save for both.
        Assert.True(inventory.TryRemove(id, questBag, 0, 3, "deliver:1:items:0", InventoryReason.QuestDelivery, "run"));
        Assert.True(store.Advance(id, 0, 3, "deliver:1", Answer(QuestRunAction.Deliver)));
        var saveId = Guid.NewGuid();
        var items = inventory.TakeBatch(id, saveId)!;
        var quests = store.TakeBatch(id, saveId)!;
        await CrashOnAsync("quest_run_audit"); // after the row, the item removal and the step were written

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, character.Version + 1, saveId, quests, inventory: items));

        Assert.Equal(5, (await _inventory.LoadAsync(id)).Single(r => r.DefId == "m2.questDemonSigil").Count); // nothing removed
        Assert.Equal(0, Assert.Single(await _quests.LoadOpenAsync(id)).Steps[0].Progress);

        await HealAsync("quest_run_audit");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, saveId, quests, inventory: items)).Status);
        Assert.Equal(2, (await _inventory.LoadAsync(id)).Single(r => r.DefId == "m2.questDemonSigil").Count);
        var run = Assert.Single(await _quests.LoadOpenAsync(id));
        Assert.Equal((RunState.Completed, 3, RunStepState.Done), (run.State, run.Steps[0].Progress, run.Steps[0].State));
        Assert.Equal(3, (await _quests.AuditAsync(id)).Single(a => a.OpKey == "deliver:1").Amount);
        // A double submit of the same delivery in a later save is refused by its keys.
        Assert.Equal(SaveStatus.DuplicateOperation, (await SaveAsync(character, character.Version + 2, Guid.NewGuid(), quests)).Status);
    }

    [Fact]
    public async Task Concurrent_progress_saves_of_one_character_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        var (store, batch, saveId) = await AcceptAsync(character, "accept:cp");
        await SaveAsync(character, character.Version, saveId, batch);
        store.Committed(character.CharacterId, saveId);
        var other = await AttachAsync(character.CharacterId); // a second world from the same row
        store.Advance(character.CharacterId, 0, 2);
        other.Advance(character.CharacterId, 1, 1);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var batchA = store.TakeBatch(character.CharacterId, a);
        var batchB = other.TakeBatch(character.CharacterId, b);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, character.Version + 1, a, batchA)),
            Task.Run(() => SaveAsync(character, character.Version + 1, b, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        var run = Assert.Single(await _quests.LoadOpenAsync(character.CharacterId));
        Assert.True(run.Steps.Select(s => s.Progress).SequenceEqual([2, 0]) || run.Steps.Select(s => s.Progress).SequenceEqual([0, 1]));
    }

    // --- Forfeit ------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_forfeit_is_saved_with_its_penalty_once_and_a_crash_or_double_submit_changes_nothing()
    {
        var character = await CreateAsync();
        var (store, batch, saveId) = await AcceptAsync(character, "accept:f");
        await SaveAsync(character, character.Version, saveId, batch);
        store.Committed(character.CharacterId, saveId);
        store.Forfeit(character.CharacterId, 1, "forfeit:1", Answer(QuestRunAction.Forfeit));
        var forfeitSave = Guid.NewGuid();
        var forfeit = store.TakeBatch(character.CharacterId, forfeitSave)!;

        await CrashOnAsync("quest_run_audit");
        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, character.Version + 1, forfeitSave, forfeit));
        Assert.Equal(RunStepState.Active, Assert.Single(await _quests.LoadOpenAsync(character.CharacterId)).Steps[1].State);
        await HealAsync("quest_run_audit");

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, forfeitSave, forfeit)).Status);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, forfeitSave, forfeit)).Status); // retry
        Assert.Equal(SaveStatus.DuplicateOperation,
            (await SaveAsync(character, character.Version + 2, Guid.NewGuid(), forfeit)).Status); // double submit

        var run = Assert.Single(await _quests.LoadOpenAsync(character.CharacterId));
        Assert.Equal((RunStepState.Forfeited, 20), (run.Steps[1].State, run.PenaltyPercent));
        Assert.Single(await _quests.AuditAsync(character.CharacterId), a => a.Action == (short)QuestRunAction.Forfeit);
    }

    [Fact]
    public async Task Concurrent_forfeits_of_one_character_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        var (store, batch, saveId) = await AcceptAsync(character, "accept:cf");
        await SaveAsync(character, character.Version, saveId, batch);
        store.Committed(character.CharacterId, saveId);
        var other = await AttachAsync(character.CharacterId);
        store.Forfeit(character.CharacterId, 0, "world-a:forfeit", Answer(QuestRunAction.Forfeit));
        other.Forfeit(character.CharacterId, 1, "world-b:forfeit", Answer(QuestRunAction.Forfeit));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var batchA = store.TakeBatch(character.CharacterId, a);
        var batchB = other.TakeBatch(character.CharacterId, b);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, character.Version + 1, a, batchA)),
            Task.Run(() => SaveAsync(character, character.Version + 1, b, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(await _quests.AuditAsync(character.CharacterId), a => a.Action == (short)QuestRunAction.Forfeit);
    }

    // --- Claim --------------------------------------------------------------------------------------------------------------

    /// <summary>A completed two-step run saved, then claimed in the store; the claim batch and the row with its exp.</summary>
    private async Task<(RunStore Store, QuestBatch Batch, Guid SaveId, CharacterState State, long Exp)> ClaimAsync(
        CharacterRecord character, string claimKey)
    {
        var (store, batch, saveId) = await AcceptAsync(character, $"accept:{claimKey}");
        await SaveAsync(character, character.Version, saveId, batch);
        store.Committed(character.CharacterId, saveId);
        store.Advance(character.CharacterId, 0, 2);
        store.Advance(character.CharacterId, 1, 1);
        long exp = store.Claim(character.CharacterId, claimKey, Answer(QuestRunAction.Claim));
        var claimSave = Guid.NewGuid();
        // The world pays the exp through ExpService in the same call; the save captures it in the row.
        return (store, store.TakeBatch(character.CharacterId, claimSave)!, claimSave, character.State with { Exp = exp }, exp);
    }

    [Fact]
    public async Task A_claim_commits_the_exp_the_claimed_run_and_its_audit_row_together()
    {
        var character = await CreateAsync();
        var (store, batch, saveId, state, exp) = await ClaimAsync(character, "claim:1");
        int claims = 0;
        store.ClaimCommitted = (_, key) => claims += key == "claim:1" ? 1 : 0;

        Assert.Equal(330, exp); // (100 + 200) x 1.10: two completed steps
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, saveId, batch, state)).Status);
        store.Committed(character.CharacterId, saveId);

        Assert.Equal(330, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Empty(await _quests.LoadOpenAsync(character.CharacterId));
        var run = Assert.Single(await _quests.LoadAllAsync(character.CharacterId));
        Assert.Equal((RunState.Claimed, 330L, 10), (run.State, run.ClaimedExp, run.BonusPercent));
        Assert.Equal(330, (await _quests.AuditAsync(character.CharacterId)).Single(a => a.OpKey == "claim:1").Amount);
        Assert.Equal(1, claims); // the world answers the claim only now (rule 5)
        Assert.True(store.IsCommitted(character.CharacterId, "claim:1"));
    }

    [Fact]
    public async Task A_run_is_claimed_once_ever_even_under_another_key()
    {
        var character = await CreateAsync();
        var (_, batch, saveId, state, _) = await ClaimAsync(character, "claim:once");
        await SaveAsync(character, character.Version + 1, saveId, batch, state);

        // A stale world claims the same run again under its own key (claim idempotency in the database).
        var replay = new QuestBatch(batch.Runs, batch.Audit.Select(a => a with { OpKey = "other-world:claim" }).ToList());
        var result = await SaveAsync(character, character.Version + 2, Guid.NewGuid(), replay, state with { Exp = 660 });

        Assert.Equal(SaveStatus.DuplicateOperation, result.Status);
        Assert.Equal(330, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Single(await _quests.AuditAsync(character.CharacterId), a => a.Action == (short)QuestRunAction.Claim);
    }

    [Fact]
    public async Task A_retry_of_a_claim_that_did_commit_pays_nothing_more()
    {
        var character = await CreateAsync();
        var (_, batch, saveId, state, _) = await ClaimAsync(character, "claim:retry");
        await SaveAsync(character, character.Version + 1, saveId, batch, state);

        var retry = await SaveAsync(character, character.Version + 1, saveId, batch, state);

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 2), retry);
        Assert.Equal(330, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Single(await _quests.AuditAsync(character.CharacterId), a => a.Action == (short)QuestRunAction.Claim);
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_claim_pays_nothing_and_the_retry_pays_once()
    {
        var character = await CreateAsync();
        var (_, batch, saveId, state, _) = await ClaimAsync(character, "claim:crash");
        await CrashOnAsync("quest_run_audit");

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, character.Version + 1, saveId, batch, state));

        Assert.Equal(0, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Equal(RunState.Active, Assert.Single(await _quests.LoadOpenAsync(character.CharacterId)).State);

        await HealAsync("quest_run_audit");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, character.Version + 1, saveId, batch, state)).Status);
        Assert.Equal(330, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Equal(RunState.Claimed, Assert.Single(await _quests.LoadAllAsync(character.CharacterId)).State);
    }

    [Fact]
    public async Task Concurrent_claims_of_one_run_have_exactly_one_winner()
    {
        var character = await CreateAsync();
        var runId = Guid.NewGuid();
        var (storeA, batch, saveId) = await AcceptAsync(character, "accept:cc", runId);
        await SaveAsync(character, character.Version, saveId, batch);
        storeA.Committed(character.CharacterId, saveId);
        var storeB = await AttachAsync(character.CharacterId);
        foreach (var store in new[] { storeA, storeB })
        {
            store.Advance(character.CharacterId, 0, 2);
            store.Advance(character.CharacterId, 1, 1);
        }
        long expA = storeA.Claim(character.CharacterId, "world-a:claim", Answer(QuestRunAction.Claim));
        long expB = storeB.Claim(character.CharacterId, "world-b:claim", Answer(QuestRunAction.Claim));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var batchA = storeA.TakeBatch(character.CharacterId, a);
        var batchB = storeB.TakeBatch(character.CharacterId, b);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, character.Version + 1, a, batchA, character.State with { Exp = expA })),
            Task.Run(() => SaveAsync(character, character.Version + 1, b, batchB, character.State with { Exp = expB })));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(330, (await _characters.LoadAsync(character.CharacterId))!.State.Exp);
        Assert.Single(await _quests.AuditAsync(character.CharacterId), a => a.Action == (short)QuestRunAction.Claim);
    }
}
