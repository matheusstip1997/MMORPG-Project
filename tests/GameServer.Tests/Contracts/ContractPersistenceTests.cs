using Dapper;
using GameServer.Contracts;
using GameServer.Crafting;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Items;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Contracts;

/// <summary>
/// Contracts against a real PostgreSQL (WP-M4-10; spec 5.4, 7.1, 8.2; rule 5): a proposal writes the contract row and the escrow
/// hold (the customer's gold and materials) in the customer's save; an acceptance writes the guarded update in the crafter's save;
/// a completion writes, in the crafter's one save, the forge operation, the escrow's settlement (the gold to the crafter, the
/// materials consumed), the contract fee, the customer's output mail, the completed row and the portfolio count; the crafter's
/// cancel writes its cost, the return mail and the closed row in the crafter's save; the customer's cancel and an expiry close the
/// row in the escrow's release-to-mail transaction. The four transaction tests on each (double submit, retry after an unknown
/// outcome, a crash in the middle, concurrent access), a crash between the output and the payment, a completion racing a cancel,
/// and after each the conservation check: the gold of both characters, the held escrows and the open mails, plus the gold the fees
/// and the cancel cost destroyed, is unchanged; the units change only by what a forge consumed and made.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class ContractPersistenceTests : IAsyncLifetime
{
    private const long Gold = 100, Fee = 5, Cost = 10;
    private const string Mold = "m3.moldSword";

    private sealed class Smiths : IJobQuery
    {
        public bool HasJob(long characterId, string jobId) => jobId == "blacksmith";
        public int JobLevel(long characterId, string jobId) => jobId == "blacksmith" ? 12 : 0;
        public string? JobIn(long characterId, JobSlot slot) => null;
        public int Repeats(long characterId, string jobId, JobActivity activity, int subjectNetId, int lastN) => 0;
    }

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private ContractRepository _contracts = null!;
    private CraftRepository _craft = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _customer = null!, _crafter = null!;
    private (long Units, long Gold) _before;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _contracts = new ContractRepository(_realmDb, (int)(Cost * 100 / Gold)); // ContractSettings.cancelCostPercent (10)
        _craft = new CraftRepository(_realmDb, _clock);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        _customer = await _kit.CreateAsync(ash: 10, gold: 1_000);
        _crafter = await CrafterAsync(gold: 100);
        _before = await ValueAsync();
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A Blacksmith with the sword mold in its Safe bag and gold, committed.</summary>
    private async Task<CharacterRecord> CrafterAsync(long gold)
    {
        var crafter = await _kit.CreateAsync(gold: gold);
        var service = await _kit.AttachAsync(crafter.CharacterId);
        Assert.Equal(1, service.TryAdd(crafter.CharacterId, Item(Mold), 0, 1, $"seed:{crafter.CharacterId}:mold", InventoryReason.Admin, "test").Added);
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(crafter.CharacterId, crafter.State, crafter.Version, saveId,
            service.TakeBatch(crafter.CharacterId, saveId))).Status);
        return (await _characters.LoadAsync(crafter.CharacterId))!;
    }

    /// <summary>A world's contract side over the characters as loaded from the database (the services the world builds).</summary>
    private async Task<World> WorldAsync(params long[] more)
    {
        var tables = TestCombat.Tables();
        long[] ids = [_customer.CharacterId, _crafter.CharacterId, .. more];
        var inventory = await _kit.AttachAsync(ids);
        var mailRules = MailRules.From(tables, TransferTestKit.Items)!;
        var escrow = new EscrowService(inventory, _clock, null)
        {
            MailReleaser = new EscrowRepository(_realmDb, new EscrowMailSink(mailRules),
                new EscrowHolderSinks(new ListingRepository(_realmDb), _contracts)),
            RetryMs = 1_000,
        };
        escrow.Load(await new EscrowRepository(_realmDb).LoadOpenAsync());
        var fees = new FeeService(EconomyRules.From(tables)!.Fees!, inventory, NoTerritoryOwners.Instance, null);
        var mail = new MailService(mailRules, inventory, () => _clock.UtcNowMs);
        var forgeRules = ForgeRules.From(tables)!;
        var forge = new ForgeService(forgeRules, inventory, new Smiths(), null, null, () => _clock.UtcNowMs, 7);
        foreach (long id in ids)
        {
            mail.Attach(id, MailRecord.Empty);
            forge.Attach(id, await _craft.LoadAsync(id));
        }
        var contracts = new ContractService(tables.Single<ContractSettings>(), inventory, escrow, fees, mail, forgeRules, new Smiths(), _clock,
            null, 1_000_000);
        contracts.Load(await _contracts.LoadAsync(0));
        escrow.MailReleaseSettled += contracts.OnMailReleaseSettled;
        forge.Contracts = contracts;
        return new World(inventory, escrow, mail, forge, contracts);
    }

    private sealed record World(InventoryService Inventory, EscrowService Escrow, MailService Mail, ForgeService Forge, ContractService Contracts);

    /// <summary>A save of everything the character has pending in <paramref name="world"/>, at its current version.</summary>
    private async Task<SaveJob> JobAsync(World world, long characterId)
    {
        var record = (await _characters.LoadAsync(characterId))!;
        var saveId = Guid.NewGuid();
        return new SaveJob(characterId, record.State, record.Version, saveId, SaveReason.Class1,
            world.Inventory.TakeBatch(characterId, saveId), Craft: world.Forge.TakeBatch(characterId, saveId),
            Escrow: world.Escrow.TakeBatch(characterId, saveId), Mail: world.Mail.TakeBatch(characterId, saveId),
            Contract: world.Contracts.TakeBatch(characterId, saveId));
    }

    private static void Committed(World world, SaveJob job)
    {
        world.Inventory.Committed(job.CharacterId, job.SaveId);
        world.Forge.Committed(job.CharacterId, job.SaveId);
        world.Escrow.Committed(job.CharacterId, job.SaveId);
        world.Mail.Committed(job.CharacterId, job.SaveId);
        world.Contracts.Committed(job.CharacterId, job.SaveId);
    }

    private async Task SavedAsync(World world, SaveJob job)
    {
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Committed(world, job);
    }

    private static ContractAsk Ask(long characterId, string op) => new(characterId, 1, 1, 0, false, $"{op}:test:{characterId}");

    private static ContractSlot Ash0 => new() { Supplier = ContractSupplier.Customer, Container = Normal, Slot = 0, ItemNetId = (ushort)Item(Ash).NetId, Count = 1 };

    /// <summary>The customer's save of a sword contract with two ash and <see cref="Gold"/> (not yet saved).</summary>
    private async Task<SaveJob> ProposeJobAsync(World world, string op = "contract")
    {
        Assert.Null(world.Contracts.Propose(Ask(_customer.CharacterId, op), ForgeKind.Mold, 1, Gold, 1, [Ash0, Ash0]));
        return await JobAsync(world, _customer.CharacterId);
    }

    private async Task<long> ProposedAsync(World world)
    {
        var job = await ProposeJobAsync(world);
        await SavedAsync(world, job);
        return job.Contract!.Proposals.Single().ContractId;
    }

    private async Task<SaveJob> AcceptJobAsync(World world, long id, long crafter = 0, string op = "contractAccept")
    {
        crafter = crafter == 0 ? _crafter.CharacterId : crafter;
        Assert.Null(world.Contracts.Accept(Ask(crafter, op), id));
        return await JobAsync(world, crafter);
    }

    private async Task<long> AcceptedAsync(World world)
    {
        long id = await ProposedAsync(world);
        await SavedAsync(world, await AcceptJobAsync(world, id));
        return id;
    }

    /// <summary>The crafter forges the contract's sword: its save of the completion (not yet saved).</summary>
    private async Task<SaveJob> CompleteJobAsync(World world, long id)
    {
        Assert.Equal(ForgeReason.None, world.Forge.Start(_crafter.CharacterId,
            new ForgeStartAsk(1, 1, NpcInteraction.Forge, ForgeKind.Mold, 1, [], InReach: true, Blocked: false, Connection: 1, ContractId: id)));
        Assert.Null(world.Forge.Finish(_crafter.CharacterId, 2, 1));
        return await JobAsync(world, _crafter.CharacterId);
    }

    /// <summary>Units (stacks, held escrow units, open mail units) and gold (characters, held escrows, open mails, plus the sinks).</summary>
    private async Task<(long Units, long Gold)> ValueAsync()
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        long units = await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(count) FROM item_stacks), 0)
                 + coalesce((SELECT sum(i.count) FROM escrow_items i JOIN escrow e USING (escrow_id) WHERE e.state = 1), 0)
                 + coalesce((SELECT sum(i.count) FROM mail_items i JOIN mail m USING (mail_id) WHERE m.state = 0), 0)
            """);
        long gold = await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(gold) FROM characters), 0)
                 + coalesce((SELECT sum(gold) FROM escrow WHERE state = 1), 0)
                 + coalesce((SELECT sum(gold) FROM mail WHERE state = 0), 0)
                 - coalesce((SELECT sum(delta) FROM gold_ledger WHERE reason IN (@fee, @cost)), 0)
            """, new { fee = nameof(InventoryReason.Fee), cost = nameof(InventoryReason.ContractCancel) });
        return (units, gold);
    }

    /// <summary>The gold is conserved; the units moved only by <paramref name="unitsDelta"/> (a forge consumed two ash and made one sword).</summary>
    private async Task AssertConservedAsync(long unitsDelta = 0)
    {
        var now = await ValueAsync();
        Assert.Equal(_before.Gold, now.Gold);
        Assert.Equal(_before.Units + unitsDelta, now.Units);
        await _kit.AssertRebuildsFromAuditAsync(_customer.CharacterId);
        await _kit.AssertRebuildsFromAuditAsync(_crafter.CharacterId);
    }

    private Task<int> CountAsync(string sql) => _kit.CountAsync(sql);

    // ---- Propose ----

    [Fact]
    public async Task A_proposal_commits_the_contract_and_the_escrow_of_gold_and_materials_together()
    {
        var world = await WorldAsync();
        long id = await ProposedAsync(world);

        var row = (await _contracts.FindAsync(id))!;
        Assert.Equal((ContractDbState.Proposed, Gold, 0L), (row.State, row.Gold, row.CrafterId));
        Assert.Equal(EscrowState.Held, await new EscrowRepository(_realmDb).StateAsync(row.EscrowId));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM escrow e JOIN escrow_items i USING (escrow_id) WHERE e.gold = {Gold} AND i.count = 2 AND e.holder_kind = {(short)EscrowHolderKind.Contract}"));
        Assert.Equal(900, (await _characters.LoadAsync(_customer.CharacterId))!.Gold);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_double_submit_of_a_proposal_writes_it_once()
    {
        var world = await WorldAsync();
        var job = await ProposeJobAsync(world);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _characters.SaveJobAsync(job)));

        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM contracts"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM escrow"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_retry_of_a_proposal_after_an_unknown_outcome_is_recognised()
    {
        var world = await WorldAsync();
        var job = await ProposeJobAsync(world);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // the outcome was lost: the same job again

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM contracts"));
        Assert.Equal(900, (await _characters.LoadAsync(_customer.CharacterId))!.Gold);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_between_the_escrow_and_the_contract_row_writes_nothing()
    {
        var world = await WorldAsync();
        var job = await ProposeJobAsync(world);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_contract() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_contract BEFORE INSERT ON contracts FOR EACH ROW EXECUTE FUNCTION test_crash_contract();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_contract ON contracts");
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM contracts"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM escrow"));
        Assert.Equal(1_000, (await _characters.LoadAsync(_customer.CharacterId))!.Gold);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task Two_worlds_proposing_with_the_same_ash_at_once_commit_one()
    {
        var world = await WorldAsync();
        var other = await WorldAsync(); // a second world that saw the same bag (the character's version is the backstop)
        var first = await ProposeJobAsync(world, "contract:a");
        var second = await ProposeJobAsync(other, "contract:b");

        var results = await Task.WhenAll(_characters.SaveJobAsync(first), _characters.SaveJobAsync(second));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM contracts"));
        await AssertConservedAsync();
    }

    // ---- Accept ----

    [Fact]
    public async Task An_acceptance_commits_the_crafter_and_the_deadline_from_now()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);

        var row = (await _contracts.FindAsync(id))!;
        Assert.Equal((ContractDbState.Accepted, _crafter.CharacterId), (row.State, row.CrafterId));
        Assert.Equal(row.AcceptedUtcMs + 3_600_000, row.DeadlineUtcMs); // the shortest deadline, from the acceptance
        Assert.Equal(ContractStatus.Accepted, world.Contracts.StatusOf(id));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_double_submit_and_a_retry_of_an_acceptance_write_it_once()
    {
        var world = await WorldAsync();
        long id = await ProposedAsync(world);
        var job = await AcceptJobAsync(world, id);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _characters.SaveJobAsync(job)));
        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // a retry of the committed save

        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM contracts WHERE state = 2 AND crafter_id = {_crafter.CharacterId}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_an_acceptance_writes_nothing()
    {
        var world = await WorldAsync();
        long id = await ProposedAsync(world);
        var job = await AcceptJobAsync(world, id);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_accept() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_accept BEFORE UPDATE ON contracts FOR EACH ROW EXECUTE FUNCTION test_crash_accept();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_accept ON contracts");
        Assert.Equal(ContractDbState.Proposed, (await _contracts.FindAsync(id))!.State);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task Two_crafters_accepting_one_proposal_at_once_commit_one()
    {
        var world = await WorldAsync();
        long id = await ProposedAsync(world);
        var second = await CrafterAsync(gold: 0);
        _before = await ValueAsync();
        var other = await WorldAsync(second.CharacterId); // a second world that saw the same proposal (the row's guard is the backstop)
        var first = await AcceptJobAsync(world, id, op: "contractAccept:a");
        var rival = await AcceptJobAsync(other, id, second.CharacterId, "contractAccept:b");

        var results = await Task.WhenAll(_characters.SaveJobAsync(first), _characters.SaveJobAsync(rival));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM contracts WHERE state = 2"));
        await AssertConservedAsync();
    }

    // ---- Complete ----

    [Fact]
    public async Task A_completion_commits_the_craft_the_settlement_the_fee_the_output_mail_and_the_count_in_one_save()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        await SavedAsync(world, await CompleteJobAsync(world, id));

        var row = (await _contracts.FindAsync(id))!;
        Assert.Equal(ContractDbState.Completed, row.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM contracts WHERE contract_id = {id} AND fee = {Fee} AND work_started"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM escrow WHERE state = 2 AND released_to_kind = {(short)EscrowTargetKind.Consumed} AND released_to_id = {_crafter.CharacterId}"));
        Assert.Equal(100 + Gold - Fee, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gold_ledger WHERE reason = '{nameof(InventoryReason.Fee)}' AND delta = -{Fee}"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail m JOIN mail_items i USING (mail_id) WHERE m.recipient_id = {_customer.CharacterId} AND m.kind = {(short)MailKind.ContractOutput} AND i.def_id = 'm3.forgedSword'"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM craft_ops WHERE contract_id = {id} AND recipient_id = {_customer.CharacterId}"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM portfolio_counters WHERE character_id = {_crafter.CharacterId} AND outcome = {(short)PortfolioOutcome.ContractCompleted}"));
        await AssertConservedAsync(unitsDelta: -2 + 1); // two ash consumed, one sword made (in the customer's mail)
    }

    [Fact]
    public async Task A_double_submit_and_a_retry_of_a_completion_write_it_once()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        var job = await CompleteJobAsync(world, id);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _characters.SaveJobAsync(job)));
        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // a retry of the committed save

        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE kind = {(short)MailKind.ContractOutput}"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM craft_ops"));
        Assert.Equal(100 + Gold - Fee, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        await AssertConservedAsync(unitsDelta: -1);
    }

    [Fact]
    public async Task A_crash_between_the_output_mail_and_the_payment_writes_neither()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        var job = await CompleteJobAsync(world, id);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_output() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_output BEFORE INSERT ON mail FOR EACH ROW EXECUTE FUNCTION test_crash_output();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_output ON mail");
        Assert.Equal(ContractDbState.Accepted, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(EscrowState.Held, await new EscrowRepository(_realmDb).StateAsync((await _contracts.FindAsync(id))!.EscrowId));
        Assert.Equal(100, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold); // no payment without the output
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM craft_ops"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_completion_racing_a_customer_cancel_commits_exactly_one()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        string escrowId = world.Contracts.Find(id)!.EscrowId;
        var job = await CompleteJobAsync(world, id);
        var releaser = new EscrowRepository(_realmDb, new EscrowMailSink(MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!),
            new EscrowHolderSinks(new ListingRepository(_realmDb), _contracts)); // a second world's cancel (the database decides)

        var save = _characters.SaveJobAsync(job);
        var cancel = releaser.ReleaseToMailAsync(escrowId, _customer.CharacterId, (short)MailKind.ContractReturn, "contractCancel:race",
            _clock.UtcNowMs, new EscrowHolderClose((short)ContractDbState.Cancelled));
        await Task.WhenAll(save, cancel);

        bool completed = save.Result.Status == SaveStatus.Saved;
        Assert.NotEqual(completed, cancel.Result == EscrowReleaseStatus.Released);
        Assert.Equal(completed ? ContractDbState.Completed : ContractDbState.Cancelled, (await _contracts.FindAsync(id))!.State);
        await AssertConservedAsync(unitsDelta: completed ? -1 : 0);
    }

    // ---- Cancels and expiry ----

    [Fact]
    public async Task The_crafter_cancel_commits_the_cost_the_return_mail_and_the_close_in_one_save()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        Assert.Equal(ForgeReason.None, world.Forge.Start(_crafter.CharacterId,
            new ForgeStartAsk(1, 1, NpcInteraction.Forge, ForgeKind.Mold, 1, [], true, false, 1, id)));
        Assert.Equal(ForgeReason.None, world.Forge.Cancel(_crafter.CharacterId)); // work started, the session ended
        Assert.Null(world.Contracts.Cancel(Ask(_crafter.CharacterId, "contractCancel"), id));
        var job = await JobAsync(world, _crafter.CharacterId);

        var results = await Task.WhenAll(_characters.SaveJobAsync(job), _characters.SaveJobAsync(job)); // a double submit

        Assert.Contains(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(ContractDbState.Cancelled, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(100 - Cost, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail m JOIN mail_items i USING (mail_id) WHERE m.recipient_id = {_customer.CharacterId} AND m.kind = {(short)MailKind.ContractReturn} AND m.gold = {Gold} AND i.count = 2"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM portfolio_counters WHERE character_id = {_crafter.CharacterId} AND outcome = {(short)PortfolioOutcome.ContractCancelled}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_the_crafter_cancel_writes_nothing()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        Assert.Null(world.Contracts.Cancel(Ask(_crafter.CharacterId, "contractCancel"), id));
        var job = await JobAsync(world, _crafter.CharacterId);
        await _kit.CrashOnAsync("portfolio_ops", _crafter.CharacterId);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.NoCrashAsync("portfolio_ops");
        Assert.Equal(ContractDbState.Accepted, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM mail"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_customer_cancel_closes_the_contract_and_mails_everything_back_once()
    {
        var world = await WorldAsync();
        long id = await ProposedAsync(world);
        string escrowId = world.Contracts.Find(id)!.EscrowId;
        var close = new EscrowHolderClose((short)ContractDbState.Cancelled);
        var releaser = new EscrowRepository(_realmDb, new EscrowMailSink(MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!),
            new EscrowHolderSinks(new ListingRepository(_realmDb), _contracts));

        Assert.Equal(EscrowReleaseStatus.Released, await releaser.ReleaseToMailAsync(escrowId, _customer.CharacterId,
            (short)MailKind.ContractReturn, "contractCancel:test:1", _clock.UtcNowMs, close));
        Assert.Equal(EscrowReleaseStatus.Released, await releaser.ReleaseToMailAsync(escrowId, _customer.CharacterId,
            (short)MailKind.ContractReturn, "contractCancel:test:1", _clock.UtcNowMs, close)); // a retry

        Assert.Equal(ContractDbState.Cancelled, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE kind = {(short)MailKind.ContractReturn} AND gold = {Gold}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task An_expiry_of_an_accepted_contract_through_the_world_returns_everything_and_counts_for_the_crafter()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);
        _clock.Advance(3_600_000);

        world.Contracts.ExpireDue(_clock.UtcNowMs);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (world.Contracts.Find(id) != null && DateTime.UtcNow < until)
        {
            world.Escrow.Tick();
            await Task.Delay(10);
        }

        Assert.Null(world.Contracts.Find(id));
        Assert.Equal(ContractDbState.Expired, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE recipient_id = {_customer.CharacterId} AND kind = {(short)MailKind.ContractReturn}"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM portfolio_counters WHERE character_id = {_crafter.CharacterId} AND outcome = {(short)PortfolioOutcome.ContractExpired}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_restart_loads_the_open_contracts_their_ids_and_the_acceptances_of_the_day()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);

        var load = await _contracts.LoadAsync(0);

        Assert.Equal(id, Assert.Single(load.Open).ContractId);
        Assert.Equal(id, load.MaxContractId);
        Assert.Single(load.AcceptedToday);
        var restarted = await WorldAsync();
        Assert.Equal(ContractStatus.Accepted, restarted.Contracts.StatusOf(id));
    }

    // ---- The expiry charge ("Yes, charge on expiry", user, 2026-11-16) ----

    /// <summary>An accepted contract the crafter started work on (the session cancelled, work_started saved).</summary>
    private async Task<long> StartedAsync(World world)
    {
        long id = await AcceptedAsync(world);
        Assert.Equal(ForgeReason.None, world.Forge.Start(_crafter.CharacterId,
            new ForgeStartAsk(1, 1, NpcInteraction.Forge, ForgeKind.Mold, 1, [], true, false, 1, id)));
        Assert.Equal(ForgeReason.None, world.Forge.Cancel(_crafter.CharacterId));
        await SavedAsync(world, await JobAsync(world, _crafter.CharacterId)); // work_started
        Assert.True((await _contracts.FindAsync(id))!.WorkStarted);
        return id;
    }

    private EscrowRepository Releaser() =>
        new(_realmDb, new EscrowMailSink(MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!),
            new EscrowHolderSinks(new ListingRepository(_realmDb), _contracts));

    /// <summary>The expiry's release (off the tick, as the world runs it) of contract <paramref name="id"/>.</summary>
    private Task<EscrowReleaseStatus> ExpireAsync(World world, long id) => Releaser().ReleaseToMailAsync(world.Contracts.Find(id)!.EscrowId,
        _customer.CharacterId, (short)MailKind.ContractReturn, ContractService.ExpireKey(id), _clock.UtcNowMs,
        new EscrowHolderClose((short)ContractDbState.Expired));

    /// <summary>The crafter's save collecting its pending charges, read as at a login.</summary>
    private async Task<SaveJob> CollectJobAsync(World world)
    {
        world.Contracts.AttachCharges(_crafter.CharacterId, await _contracts.LoadChargesAsync(_crafter.CharacterId));
        world.Contracts.ExpireDue(_clock.UtcNowMs); // phase 7 collects
        return await JobAsync(world, _crafter.CharacterId);
    }

    [Fact]
    public async Task A_started_contract_s_expiry_writes_the_close_the_return_mail_and_one_pending_charge_together()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);

        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id));
        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id)); // a retry

        Assert.Equal(ContractDbState.Expired, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE recipient_id = {_customer.CharacterId} AND kind = {(short)MailKind.ContractReturn} AND gold = {Gold}"));
        var charge = Assert.Single(await _contracts.LoadChargesAsync(_crafter.CharacterId));
        Assert.Equal((id, Cost), (charge.ContractId, charge.Amount));
        Assert.Equal(100, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold); // the crafter's row is not written by the expiry
        await AssertConservedAsync();
    }

    [Fact]
    public async Task An_expiry_before_work_started_charges_nothing()
    {
        var world = await WorldAsync();
        long id = await AcceptedAsync(world);

        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id));

        Assert.Equal(0, await CountAsync("SELECT count(*) FROM contract_charges"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_the_expiry_writes_neither_the_close_nor_the_charge()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_charge() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_charge BEFORE INSERT ON contract_charges FOR EACH ROW EXECUTE FUNCTION test_crash_charge();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => ExpireAsync(world, id));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_charge ON contract_charges");
        Assert.Equal(ContractDbState.Accepted, (await _contracts.FindAsync(id))!.State);
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM contract_charges"));
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM mail WHERE kind = {(short)MailKind.ContractReturn}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task The_collection_takes_the_gold_and_closes_the_charge_once_at_a_login()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id)); // the crafter "offline": nothing collected yet
        var login = await WorldAsync(); // its next login reads the pending charge
        var job = await CollectJobAsync(login);

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => _characters.SaveJobAsync(job))); // a double submit
        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // a retry of the committed save

        Assert.Equal(100 - Cost, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM contract_charges WHERE state = 2 AND collected_amount = {Cost}"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gold_ledger WHERE reason = '{nameof(InventoryReason.ContractCancel)}' AND delta = -{Cost}"));
        Assert.Empty(await _contracts.LoadChargesAsync(_crafter.CharacterId));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_in_the_collection_takes_nothing_and_leaves_the_charge_pending()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id));
        var job = await CollectJobAsync(await WorldAsync());
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_collect() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_collect BEFORE UPDATE ON contract_charges FOR EACH ROW EXECUTE FUNCTION test_crash_collect();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_collect ON contract_charges");
        Assert.Equal(100, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Single(await _contracts.LoadChargesAsync(_crafter.CharacterId));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crafter_holding_less_pays_what_it_holds_and_the_rest_is_dropped()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        Assert.Equal(EscrowReleaseStatus.Released, await ExpireAsync(world, id));
        var poor = await WorldAsync();
        Assert.True(poor.Inventory.TryRemoveGold(_crafter.CharacterId, 97, "test:spend", InventoryReason.Admin, "test").Applied);
        await SavedAsync(poor, await JobAsync(poor, _crafter.CharacterId)); // the crafter now holds 3
        _before = await ValueAsync();

        await SavedAsync(poor, await CollectJobAsync(poor));

        Assert.Equal(0, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM contract_charges WHERE state = 2 AND amount = {Cost} AND collected_amount = 3"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task An_online_crafter_is_charged_right_after_its_expiry_through_the_world()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        _clock.Advance(3_600_000);

        world.Contracts.ExpireDue(_clock.UtcNowMs);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (world.Contracts.Find(id) != null && DateTime.UtcNow < until)
        {
            world.Escrow.Tick();
            await Task.Delay(10);
        }
        Assert.Equal(1, world.Contracts.ChargesWaiting); // known at once, collected on the next tick
        world.Contracts.ExpireDue(_clock.UtcNowMs);
        await SavedAsync(world, await JobAsync(world, _crafter.CharacterId));

        Assert.Equal(0, world.Contracts.ChargesWaiting);
        Assert.Equal(100 - Cost, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        Assert.Empty(await _contracts.LoadChargesAsync(_crafter.CharacterId));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task An_expiry_racing_a_completion_commits_one_and_charges_only_an_expiry()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        var job = await CompleteJobAsync(world, id);

        var save = _characters.SaveJobAsync(job);
        var expiry = ExpireAsync(world, id); // a second world's expiry (the database decides)
        await Task.WhenAll(save, expiry);

        bool completed = save.Result.Status == SaveStatus.Saved;
        Assert.NotEqual(completed, expiry.Result == EscrowReleaseStatus.Released);
        Assert.Equal(completed ? 0 : 1, await CountAsync("SELECT count(*) FROM contract_charges"));
        await AssertConservedAsync(unitsDelta: completed ? -1 : 0);
    }

    [Fact]
    public async Task An_expiry_racing_the_crafter_s_cancel_costs_the_crafter_once()
    {
        var world = await WorldAsync();
        long id = await StartedAsync(world);
        Assert.Null(world.Contracts.Cancel(Ask(_crafter.CharacterId, "contractCancel"), id));
        var cancel = await JobAsync(world, _crafter.CharacterId);

        var save = _characters.SaveJobAsync(cancel);
        var expiry = ExpireAsync(world, id);
        await Task.WhenAll(save, expiry);

        bool cancelled = save.Result.Status == SaveStatus.Saved;
        Assert.NotEqual(cancelled, expiry.Result == EscrowReleaseStatus.Released);
        Assert.Equal(cancelled ? 0 : 1, await CountAsync("SELECT count(*) FROM contract_charges"));
        Assert.Equal(cancelled ? 100 - Cost : 100, (await _characters.LoadAsync(_crafter.CharacterId))!.Gold);
        await AssertConservedAsync();
    }
}
