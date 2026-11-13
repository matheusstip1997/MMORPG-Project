using Dapper;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Sessions;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;

namespace GameServer.Tests.Items;

/// <summary>
/// System mail against a real PostgreSQL (spec 4.5 M4 mail, 7.1, 8.2; WP-M4-3; CLAUDE.md rule 5): a claim is one class 1
/// operation whose bag write and mail update commit together, tested by double submit, retry after an unknown outcome, a crash
/// in the middle and concurrent writers (two sessions of one character are kept apart by the ownership lock, so the
/// concurrent case is two worlds saving one character: one wins). A send rides another operation's transaction; the stand-alone
/// writer is idempotent; the expiry job closes each mail once; and the done check: a mail with an item and gold, claimed after a
/// relogin, is in the bag after the next one.
/// </summary>
public sealed class MailPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _items;
    private readonly MailRules _rules;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private GoldRepository _gold = null!;
    private MailRepository _mail = null!;
    private long _nextAccount = 1;

    public MailPersistenceTests()
    {
        var tables = TestCombat.Tables();
        _items = ItemRules.From(tables)!;
        _rules = MailRules.From(tables, _items)!;
    }

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _gold = new GoldRepository(_realmDb);
        _mail = new MailRepository(_realmDb, _clock);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var result = await _characters.CreateAsync(new NewCharacter(account, 0, $"Mailed{account}", "warrior", Spawn, Guid.NewGuid()));
        return result.Character!;
    }

    /// <summary>A world holding the character as a login loads it: its bag, its gold and its mailbox.</summary>
    private async Task<(InventoryService Items, MailService Mail)> LoginAsync(long characterId)
    {
        var items = new InventoryService(_items, events: null);
        Assert.True(items.Attach(characterId, await _inventory.LoadInventoryAsync(characterId), "warrior", 1));
        items.AttachGold(characterId, (await _characters.LoadAsync(characterId))!.Gold);
        var mail = new MailService(_rules, items, () => _clock.UtcNowMs);
        mail.Attach(characterId, await _mail.LoadOpenAsync(characterId, _rules.Settings.MaxListed));
        return (items, mail);
    }

    /// <summary>A stand-alone send (the admin compensation's path) in its own transaction.</summary>
    private async Task<long> SendAsync(long to, string key, long gold, params MailAttachment[] items)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var draft = new MailDraft(to, MailKind.AdminCompensation, MailSubjects.AdminCompensation, [], items, gold, key, "test");
        Assert.Null(_rules.Check(draft));
        long now = _clock.UtcNowMs;
        var id = await MailWriter.SendAsync(connection, transaction, draft, now, _rules.ExpiresAt(now), null, null, skipDuplicate: true,
            CancellationToken.None);
        await transaction.CommitAsync();
        return id!.Value;
    }

    private static (InventoryBatch? Items, MailBatch? Mail) Take((InventoryService Items, MailService Mail) world, long id, Guid saveId) =>
        (world.Items.TakeBatch(id, saveId), world.Mail.TakeBatch(id, saveId));

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, (InventoryBatch? Items, MailBatch? Mail) batch) =>
        _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, batch.Items, mail: batch.Mail);

    private MailResult? Claim((InventoryService Items, MailService Mail) world, long id, uint request, long mailId = 0) =>
        world.Mail.Claim(id, new MailClaimAsk(request, mailId, false, 1, $"mail:test:{id}:1:{request}", AtMailbox: true));

    private async Task<int> Units(long characterId, string defId) =>
        (await _inventory.LoadAsync(characterId)).Where(r => r.DefId == defId).Sum(r => r.Count);

    private async Task CreateCrashTriggerAsync(string table, string condition)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync($"""
            CREATE FUNCTION test_mail_crash() RETURNS trigger AS $$
            BEGIN IF {condition} THEN RAISE EXCEPTION 'simulated crash'; END IF; RETURN NEW; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_mail_crash BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION test_mail_crash();
            """);
    }

    private async Task DropCrashTriggerAsync(string table)
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        await connection.ExecuteAsync($"DROP TRIGGER test_mail_crash ON {table}; DROP FUNCTION test_mail_crash();");
    }

    [Fact]
    public async Task A_mail_with_an_item_and_gold_is_claimed_into_the_bag_after_a_relogin()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        long mailId = await SendAsync(id, "admin:1", 250, new MailAttachment("m1.demonAsh", 0, 7),
            new MailAttachment("m2.trainingSword", 0, 1));

        var world = await LoginAsync(id); // the relogin reads the mail written while offline
        Assert.Equal(mailId, Assert.Single(world.Mail.Mailbox(id)).MailId);
        Assert.Null(Claim(world, id, 1, mailId));
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, Take(world, id, saveId))).Status);

        var after = await LoginAsync(id); // the next login
        Assert.Empty(after.Mail.Mailbox(id));
        Assert.Equal(250, after.Items.Gold(id));
        Assert.Equal(7, await Units(id, "m1.demonAsh"));
        Assert.Contains((await _inventory.LoadInventoryAsync(id)).Instances, i => i.DefId == "m2.trainingSword");
        var mail = (await _mail.GetAsync(mailId))!.Value;
        Assert.Equal(MailState.Claimed, mail.State);
        Assert.Empty(mail.Mail.Items);
        // An admin compensation's claim is the faucet (reason Admin); the audit rows rebuild the bag (R-7.1-06).
        var gold = (await _gold.LedgerAsync(id)).Single();
        Assert.Equal((250L, GoldFlow.Faucet, nameof(InventoryReason.Admin)), (gold.Delta, gold.Flow, gold.Reason));
        Assert.Equal([MailOpKind.Send, MailOpKind.Claim], (await _mail.OpsAsync(mailId)).Select(o => o.Kind));
    }

    [Fact]
    public async Task A_settlement_mails_claim_moves_gold_and_is_no_faucet()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        var seller = await CreateAsync();
        var world = await LoginAsync(seller.CharacterId);
        // The seller's proceeds, carried by another character's save (a buyer's, here the seller's own for the test).
        world.Mail.Send(seller.CharacterId, new MailDraft(id, MailKind.ListingProceeds, MailSubjects.ListingProceeds, [], [], 90,
            "buy:r:1:1:1", "listing:1"));
        var sendSave = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(seller, sendSave, Take(world, seller.CharacterId, sendSave))).Status);

        var recipient = await LoginAsync(id);
        Assert.Null(Claim(recipient, id, 1));
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, Take(recipient, id, saveId))).Status);

        var entry = (await _gold.LedgerAsync(id)).Single();
        Assert.Equal((90L, GoldFlow.Move, nameof(InventoryReason.MailClaim)), (entry.Delta, entry.Flow, entry.Reason));
    }

    [Fact]
    public async Task A_double_submit_of_a_claim_save_applies_it_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        long mailId = await SendAsync(id, "admin:2", 40, new MailAttachment("m1.demonAsh", 0, 3));
        var world = await LoginAsync(id);
        Assert.Null(Claim(world, id, 1));
        var saveId = Guid.NewGuid();
        var batch = Take(world, id, saveId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SaveAsync(character, saveId, batch)));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(40, await _gold.BalanceAsync(id));
        Assert.Equal(3, await Units(id, "m1.demonAsh"));
        Assert.Single(await _mail.OpsAsync(mailId), o => o.Kind == MailOpKind.Claim);
    }

    [Fact]
    public async Task A_retry_of_a_claim_after_an_unknown_outcome_is_recognised_and_pays_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        await SendAsync(id, "admin:3", 40, new MailAttachment("m1.demonAsh", 0, 3));
        var world = await LoginAsync(id);
        Assert.Null(Claim(world, id, 1));
        var saveId = Guid.NewGuid();
        var batch = Take(world, id, saveId);
        await SaveAsync(character, saveId, batch);

        var retry = await SaveAsync(character, saveId, batch);

        Assert.Equal(SaveStatus.Saved, retry.Status);
        Assert.Equal(40, await _gold.BalanceAsync(id));
        Assert.Equal(3, await Units(id, "m1.demonAsh"));
    }

    [Fact]
    public async Task A_crash_between_the_mail_update_and_the_bag_write_writes_neither_and_the_retry_applies_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        long mailId = await SendAsync(id, "admin:4", 40, new MailAttachment("m1.demonAsh", 0, 3));
        var world = await LoginAsync(id);
        Assert.Null(Claim(world, id, 1));
        var saveId = Guid.NewGuid();
        var batch = Take(world, id, saveId);
        // The claim's audit row fails after the bag, the gold and the mail rows were written.
        await CreateCrashTriggerAsync("mail_ops", "NEW.kind = 2");

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, saveId, batch));

        Assert.Equal(character.Version, (await _characters.LoadAsync(id))!.Version);
        Assert.Equal(0, await _gold.BalanceAsync(id));
        Assert.Equal(0, await Units(id, "m1.demonAsh"));
        var open = (await _mail.GetAsync(mailId))!.Value;
        Assert.Equal((MailState.Open, 40L, 3), (open.State, open.Mail.Gold, open.Mail.Items.Single().Count));

        await DropCrashTriggerAsync("mail_ops");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, batch)).Status);

        Assert.Equal(40, await _gold.BalanceAsync(id));
        Assert.Equal(3, await Units(id, "m1.demonAsh"));
        Assert.Equal(MailState.Claimed, (await _mail.GetAsync(mailId))!.Value.State);
    }

    [Fact]
    public async Task Two_worlds_claiming_one_mail_have_one_winner_and_pay_once()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        await SendAsync(id, "admin:5", 60, new MailAttachment("m1.demonAsh", 0, 2));
        // Two worlds that both think they own the character (the ownership lock prevents it; the version check is the backstop).
        var a = await LoginAsync(id);
        var b = await LoginAsync(id);
        Assert.Null(Claim(a, id, 1));
        Assert.Null(Claim(b, id, 1));
        var saveA = Guid.NewGuid();
        var saveB = Guid.NewGuid();
        var batchA = Take(a, id, saveA);
        var batchB = Take(b, id, saveB);

        var results = await Task.WhenAll(Task.Run(() => SaveAsync(character, saveA, batchA)), Task.Run(() => SaveAsync(character, saveB, batchB)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(60, await _gold.BalanceAsync(id));
        Assert.Equal(2, await Units(id, "m1.demonAsh"));
    }

    [Fact]
    public async Task A_claim_saved_after_the_expiry_job_closed_the_mail_writes_nothing()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        long mailId = await SendAsync(id, "admin:6", 60, new MailAttachment("m1.demonAsh", 0, 2));
        var world = await LoginAsync(id);
        Assert.Null(Claim(world, id, 1));
        var saveId = Guid.NewGuid();
        var batch = Take(world, id, saveId);
        _clock.Advance(_rules.Settings.ExpiryDurationMs() + _rules.Settings.ExpiryGraceMs); // a save stuck past the grace
        Assert.Single(await _mail.ExpireDueAsync(_rules.Settings.ExpiryGraceMs, 10));

        Assert.Equal(SaveStatus.VersionConflict, (await SaveAsync(character, saveId, batch)).Status); // the character faults

        Assert.Equal(0, await _gold.BalanceAsync(id));
        Assert.Equal(0, await Units(id, "m1.demonAsh"));
        Assert.Equal(MailState.Expired, (await _mail.GetAsync(mailId))!.Value.State);
    }

    [Fact]
    public async Task A_send_rides_another_operations_save_and_a_crash_rolls_both_back()
    {
        var buyer = await CreateAsync();
        var seller = await CreateAsync();
        var world = await LoginAsync(buyer.CharacterId);
        Assert.Equal(3, world.Items.TryAdd(buyer.CharacterId, ItemDef("m1.demonAsh"), 0, 3, "seed:ash", InventoryReason.Admin, "test").Added);
        var seedSave = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(buyer, seedSave, Take(world, buyer.CharacterId, seedSave))).Status);
        world.Items.Committed(buyer.CharacterId, seedSave);
        buyer = (await _characters.LoadAsync(buyer.CharacterId))!;

        // The operation: the units leave the carrier's bag and go to the seller by mail, in one save.
        Assert.True(world.Items.TryRemove(buyer.CharacterId, 0, 3, "op:1:items", InventoryReason.Admin, "test"));
        Assert.Equal(MailSendStatus.Queued, world.Mail.Send(buyer.CharacterId, new MailDraft(seller.CharacterId, MailKind.ListingReturn,
            MailSubjects.ListingReturn, [new MailArg("count", MailArgKind.Number, 3)], [new MailAttachment("m1.demonAsh", 0, 3)], 0, "op:1",
            "test")).Status);
        var saveId = Guid.NewGuid();
        var batch = Take(world, buyer.CharacterId, saveId);
        await CreateCrashTriggerAsync("mail_items", "true");

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(buyer, saveId, batch));
        Assert.Equal(3, await Units(buyer.CharacterId, "m1.demonAsh")); // the removal rolled back with the mail
        Assert.Equal(0, (await _mail.LoadOpenAsync(seller.CharacterId, 10)).OpenCount);

        await DropCrashTriggerAsync("mail_items");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(buyer, saveId, batch)).Status);
        Assert.Equal(0, await Units(buyer.CharacterId, "m1.demonAsh"));
        var mail = Assert.Single((await _mail.LoadOpenAsync(seller.CharacterId, 10)).Open);
        Assert.Equal((MailKind.ListingReturn, 3, "count"), (mail.Kind, mail.Items.Single().Count, mail.Args.Single().Name));
        Assert.True(batch.Mail!.Sends[0].MailId == mail.MailId); // the world learns the id with the commit
        Assert.Equal(seller.Version, (await _characters.LoadAsync(seller.CharacterId))!.Version); // the recipient's row untouched
    }

    [Fact]
    public async Task A_save_carrying_a_send_key_twice_is_a_duplicate_and_a_stand_alone_retry_writes_nothing()
    {
        var a = await CreateAsync();
        var b = await CreateAsync();
        await SendAsync(b.CharacterId, "dup:1", 5);
        await using (var connection = await _realmDb.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var draft = new MailDraft(b.CharacterId, MailKind.AdminCompensation, MailSubjects.AdminCompensation, [], [], 5, "dup:1", "test");
            Assert.Null(await MailWriter.SendAsync(connection, transaction, draft, _clock.UtcNowMs, _rules.ExpiresAt(_clock.UtcNowMs), null,
                null, skipDuplicate: true, CancellationToken.None));
        }

        var world = await LoginAsync(a.CharacterId);
        world.Mail.Send(a.CharacterId, new MailDraft(b.CharacterId, MailKind.ListingProceeds, MailSubjects.ListingProceeds, [], [], 5,
            "dup:1", "test"));
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.DuplicateOperation, (await SaveAsync(a, saveId, Take(world, a.CharacterId, saveId))).Status);
        Assert.Equal(1, (await _mail.LoadOpenAsync(b.CharacterId, 10)).OpenCount);
    }

    [Fact]
    public async Task A_send_in_a_transaction_that_rolls_back_leaves_nothing()
    {
        var b = await CreateAsync();
        await using (var connection = await _realmDb.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var draft = new MailDraft(b.CharacterId, MailKind.AdminCompensation, MailSubjects.AdminCompensation, [],
                [new MailAttachment("m1.demonAsh", 0, 1)], 5, "rollback:1", "test");
            Assert.NotNull(await MailWriter.SendAsync(connection, transaction, draft, _clock.UtcNowMs, _rules.ExpiresAt(_clock.UtcNowMs),
                null, null, skipDuplicate: false, CancellationToken.None));
            await transaction.RollbackAsync();
        }
        Assert.Equal(MailRecord.Empty.OpenCount, (await _mail.LoadOpenAsync(b.CharacterId, 10)).OpenCount);
    }

    [Fact]
    public async Task The_expiry_job_closes_each_mail_once_after_the_grace_and_records_what_it_destroyed()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        long mailId = await SendAsync(id, "admin:7", 30, new MailAttachment("m1.demonAsh", 0, 4));
        long grace = _rules.Settings.ExpiryGraceMs;

        _clock.Advance(_rules.Settings.ExpiryDurationMs());
        Assert.Empty(await _mail.ExpireDueAsync(grace, 10)); // the world stops claims now; the job waits for the grace
        _clock.Advance(grace);
        var expired = Assert.Single(await _mail.ExpireDueAsync(grace, 10));
        Assert.Empty(await _mail.ExpireDueAsync(grace, 10)); // idempotent

        Assert.Equal((mailId, id, 30L, 4), (expired.MailId, expired.RecipientId, expired.Gold, expired.Items.Single().Count));
        Assert.Equal(MailState.Expired, (await _mail.GetAsync(mailId))!.Value.State);
        var op = (await _mail.OpsAsync(mailId)).Single(o => o.Kind == MailOpKind.Expire);
        Assert.Equal((MailRepository.ExpireKey(mailId), 30L), (op.OpKey, op.Gold));
        Assert.Equal(0, (await _mail.LoadOpenAsync(id, 10)).OpenCount);
    }

    [Fact]
    public async Task The_character_source_reads_the_open_mails_at_login()
    {
        var character = await CreateAsync();
        long id = character.CharacterId;
        await SendAsync(id, "admin:8", 1);
        await SendAsync(id, "admin:9", 2);
        var source = new RepositoryCharacterSource(_characters, mail: _mail, mailListed: 1);

        var record = await source.LoadMailAsync(id);

        Assert.Equal((2, 1L), (record.OpenCount, Assert.Single(record.Open).Gold)); // oldest first, the limit, the count of all
    }

    private ItemDef ItemDef(string id)
    {
        Assert.True(_items.TryGetItem(id, out var item));
        return item;
    }
}
