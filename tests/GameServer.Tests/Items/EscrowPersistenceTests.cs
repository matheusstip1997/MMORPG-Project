using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Simulation.Items;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Items;

/// <summary>
/// Escrow against a real PostgreSQL (WP-M4-4; spec 4.5, 5.4, 8.2; rule 5): a hold writes the escrow rows in the owner's save
/// transaction with the units and gold that left; a release to a character in that character's save, guarded so it happens
/// once; a release to mail in one transaction with the mail (WP-M4-3's writer replaced by a double). The four transaction
/// tests on hold and release, and the conservation check after each: every item and gold over the characters, the held
/// escrows and the mail double is unchanged; the ledger rows are moves.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class EscrowPersistenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private EscrowRepository _escrows = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _a = null!, _b = null!;
    private string _before = "";

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _escrows = new EscrowRepository(_realmDb, new TestMailSink());
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        await _kit.CreateMailDoubleAsync();
        _a = await _kit.CreateAsync(ash: 10, swords: 1, gold: 100);
        _b = await _kit.CreateAsync();
        _before = await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private EscrowService Escrow(InventoryService world) => new(world, _clock, null);

    /// <summary>A holds 4 ash, its sword and 30 gold for a test holder (what a listing or a contract will do).</summary>
    private SaveJob Hold(InventoryService world, EscrowService escrow, string escrowId = "escrow:h:1")
    {
        var result = escrow.Hold(_a.CharacterId, [new ReserveItem(new InventoryLocation(Normal, 0), 4), new ReserveItem(SwordAt(world, _a.CharacterId), 1)],
            30, EscrowHolderKind.Test, "listing:1", escrowId);
        Assert.Equal(EscrowRefusal.None, result.Refusal);
        return Job(world, _a, SaveReason.Escrow, escrow);
    }

    /// <summary>A hold already committed (the world heard of it).</summary>
    private async Task<string> HeldAsync()
    {
        var world = await _kit.AttachAsync(_a.CharacterId);
        var escrow = Escrow(world);
        var job = Hold(world, escrow);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        return "escrow:h:1";
    }

    private async Task AssertConservedAsync(params long[] ids)
    {
        Assert.Equal(_before, await _kit.HoldingsAsync(ids.Length == 0 ? [_a.CharacterId, _b.CharacterId] : ids));
        foreach (long id in ids.Length == 0 ? [_a.CharacterId, _b.CharacterId] : ids)
            await _kit.AssertRebuildsFromAuditAsync(id);
    }

    [Fact]
    public async Task A_hold_commits_the_units_and_gold_out_of_the_bag_and_the_escrow_rows_together()
    {
        var world = await _kit.AttachAsync(_a.CharacterId);
        var escrow = Escrow(world);
        var job = Hold(world, escrow);
        Assert.False(escrow.IsHeld("escrow:h:1")); // not before the commit

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        world.Committed(_a.CharacterId, job.SaveId);
        escrow.Committed(_a.CharacterId, job.SaveId);

        Assert.True(escrow.IsHeld("escrow:h:1"));
        Assert.Equal(EscrowState.Held, await _escrows.StateAsync("escrow:h:1"));
        Assert.Equal(2, await _kit.CountAsync("SELECT count(*) FROM escrow_items WHERE escrow_id = 'escrow:h:1'"));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM escrow_items WHERE instance_id <> 0 AND properties IS NOT NULL"));
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM item_instances WHERE character_id = @a", new { a = _a.CharacterId }));
        Assert.Equal(70, (await _characters.LoadAsync(_a.CharacterId))!.Gold);
        await AssertConservedAsync();
        await _kit.AssertGoldMovesAsync("escrow:", heldOrMailed: 30);
        var loaded = Assert.Single(await _escrows.LoadOpenAsync());
        Assert.Equal((_a.CharacterId, 30L, 2), (loaded.OwnerCharacterId, loaded.Gold, loaded.Items.Count));
    }

    [Fact]
    public async Task A_double_submit_of_a_hold_writes_it_once()
    {
        var world = await _kit.AttachAsync(_a.CharacterId);
        var job = Hold(world, Escrow(world));

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _characters.SaveJobAsync(job)));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM escrow"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_retry_of_a_hold_after_an_unknown_outcome_is_recognised()
    {
        var world = await _kit.AttachAsync(_a.CharacterId);
        var job = Hold(world, Escrow(world));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM escrow"));
        Assert.Equal(70, (await _characters.LoadAsync(_a.CharacterId))!.Gold);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_hold_writes_nothing_and_the_retry_applies_once()
    {
        var world = await _kit.AttachAsync(_a.CharacterId);
        var job = Hold(world, Escrow(world));
        await _kit.ExecuteAsync("""
            CREATE FUNCTION test_escrow_crash() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_escrow_crash BEFORE INSERT ON escrow_items FOR EACH ROW EXECUTE FUNCTION test_escrow_crash();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM escrow"));
        Assert.Equal(_a.Version, (await _characters.LoadAsync(_a.CharacterId))!.Version);
        await AssertConservedAsync();

        await _kit.ExecuteAsync("DROP TRIGGER test_escrow_crash ON escrow_items");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM escrow"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_release_to_a_character_moves_everything_once_with_the_sword_intact()
    {
        string escrowId = await HeldAsync();
        var world = await _kit.AttachAsync(_b.CharacterId);
        var escrow = Escrow(world);
        escrow.Load(await _escrows.LoadOpenAsync());

        var released = escrow.ReleaseToCharacter(escrowId, _b.CharacterId, "escrow:r:1", 16);
        Assert.Equal(EscrowRefusal.None, released.Refusal);
        Assert.Equal(EscrowRefusal.NotHeld, escrow.ReleaseToCharacter(escrowId, _b.CharacterId, "escrow:r:2", 16).Refusal); // once
        var job = Job(world, _b, SaveReason.Escrow, escrow);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        escrow.Committed(_b.CharacterId, job.SaveId);

        Assert.True(escrow.IsReleased(escrowId));
        Assert.Equal(EscrowState.Released, await _escrows.StateAsync(escrowId));
        Assert.Equal(30, (await _characters.LoadAsync(_b.CharacterId))!.Gold);
        var relogin = await _kit.AttachAsync(_b.CharacterId);
        long sword = relogin.CurrentInventory(_b.CharacterId).At(SwordAt(relogin, _b.CharacterId)).InstanceId;
        Assert.True(relogin.TryGetProperties(_b.CharacterId, sword, out var properties));
        Assert.Equal(new[] { _a.CharacterId }, properties.OwnerHistory);
        await AssertConservedAsync();
        await _kit.AssertGoldMovesAsync("escrow:");
        Assert.Empty(await _escrows.LoadOpenAsync());
    }

    [Fact]
    public async Task Two_worlds_releasing_one_escrow_at_once_release_it_once()
    {
        string escrowId = await HeldAsync();
        var c = await _kit.CreateAsync();
        _before = await _kit.HoldingsAsync(_a.CharacterId, _b.CharacterId, c.CharacterId);
        var jobs = new List<SaveJob>();
        foreach (var (receiver, key) in new[] { (_b, "escrow:r:b"), (c, "escrow:r:c") })
        {
            var world = await _kit.AttachAsync(receiver.CharacterId);
            var escrow = Escrow(world);
            escrow.Load(await _escrows.LoadOpenAsync()); // both worlds think it is held
            Assert.Equal(EscrowRefusal.None, escrow.ReleaseToCharacter(escrowId, receiver.CharacterId, key, 16).Refusal);
            jobs.Add(Job(world, receiver, SaveReason.Escrow, escrow));
        }

        var results = await Task.WhenAll(jobs.Select(j => _characters.SaveJobAsync(j)));

        Assert.Equal(1, results.Count(r => r.Status == SaveStatus.Saved));
        Assert.Equal(1, results.Count(r => r.Status == SaveStatus.DuplicateOperation)); // the guard: faulted, never applied twice
        await AssertConservedAsync(_a.CharacterId, _b.CharacterId, c.CharacterId);
    }

    [Fact]
    public async Task A_release_to_mail_writes_the_escrow_and_the_mail_together_once()
    {
        string escrowId = await HeldAsync();

        Assert.Equal(EscrowReleaseStatus.Released, await _escrows.ReleaseToMailAsync(escrowId, _a.CharacterId, 1, "escrow:m:1", 1_000));
        Assert.Equal(EscrowReleaseStatus.Released, await _escrows.ReleaseToMailAsync(escrowId, _a.CharacterId, 1, "escrow:m:1", 1_000)); // retry
        Assert.Equal(EscrowReleaseStatus.Conflict, await _escrows.ReleaseToMailAsync(escrowId, _b.CharacterId, 1, "escrow:m:2", 1_000));

        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM test_mail"));
        Assert.Equal(2, await _kit.CountAsync("SELECT count(*) FROM test_mail_items"));
        Assert.Equal(EscrowState.Released, await _escrows.StateAsync(escrowId));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task Two_releases_to_mail_at_once_write_one_mail()
    {
        string escrowId = await HeldAsync();

        var results = await Task.WhenAll(
            _escrows.ReleaseToMailAsync(escrowId, _a.CharacterId, 1, "escrow:m:a", 1_000),
            _escrows.ReleaseToMailAsync(escrowId, _b.CharacterId, 1, "escrow:m:b", 1_000));

        Assert.Equal(1, results.Count(r => r == EscrowReleaseStatus.Released));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM test_mail"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_the_middle_of_a_release_to_mail_leaves_the_escrow_held()
    {
        string escrowId = await HeldAsync();
        await _kit.ExecuteAsync("""
            CREATE FUNCTION test_mail_crash() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_mail_crash BEFORE INSERT ON test_mail_items FOR EACH ROW EXECUTE FUNCTION test_mail_crash();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _escrows.ReleaseToMailAsync(escrowId, _a.CharacterId, 1, "escrow:m:1", 1_000));

        Assert.Equal(EscrowState.Held, await _escrows.StateAsync(escrowId));
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM test_mail"));
        await AssertConservedAsync();
        await _kit.ExecuteAsync("DROP TRIGGER test_mail_crash ON test_mail_items");
        Assert.Equal(EscrowReleaseStatus.Released, await _escrows.ReleaseToMailAsync(escrowId, _a.CharacterId, 1, "escrow:m:1", 1_000));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task The_service_releases_to_mail_off_the_world_thread_and_learns_it_committed()
    {
        string escrowId = await HeldAsync();
        var world = await _kit.AttachAsync(_b.CharacterId);
        var escrow = Escrow(world);
        escrow.MailReleaser = _escrows;
        escrow.RetryMs = 10;
        escrow.Load(await _escrows.LoadOpenAsync());

        Assert.Equal(EscrowRefusal.None, escrow.ReleaseToMail(escrowId, _a.CharacterId, 1, "escrow:m:1"));
        Assert.Equal(EscrowRefusal.NotHeld, escrow.ReleaseToMail(escrowId, _a.CharacterId, 1, "escrow:m:2"));
        for (int i = 0; i < 200 && !escrow.IsReleased(escrowId); i++)
        {
            await Task.Delay(10);
            escrow.Tick();
        }

        Assert.True(escrow.IsReleased(escrowId));
        await AssertConservedAsync();
    }
}
