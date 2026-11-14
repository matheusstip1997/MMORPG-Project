using System.Diagnostics;
using Dapper;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Market;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Items;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Market;

/// <summary>
/// Listings against a real PostgreSQL (WP-M4-7; spec 4.5, 7.1, 8.2; rule 5): a create writes the listing, the escrow hold and the
/// listing fee in the seller's save; a purchase writes the listing's move, the sale row, the escrow release (and the rest held
/// again), the payment into the seller's proceeds mail and the destroyed sale tax in the buyer's save; a cancel and an expiry
/// close the listing in the escrow's release-to-mail transaction. The four transaction tests on each (double submit, retry after
/// an unknown outcome, a crash in the middle, concurrent access), and after each the conservation check: the units and gold of
/// both characters, the held escrows and the open mails, plus the destroyed fees, are unchanged; the ledger's moves sum to zero.
/// The search budget: 10,000 active listings, every filter, three pages each, p95 at most 50 ms on an index.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class ListingPersistenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private ListingRepository _listings = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _seller = null!, _buyer = null!;
    private long _before;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _listings = new ListingRepository(_realmDb);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        _seller = await _kit.CreateAsync(ash: 10, gold: 100);
        _buyer = await _kit.CreateAsync(gold: 1_000);
        _before = await ValueAsync();
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A world's listing side over both characters as loaded from the database (the services the world builds).</summary>
    private async Task<World> WorldAsync(params long[] more)
    {
        var tables = TestCombat.Tables();
        long[] ids = [_seller.CharacterId, _buyer.CharacterId, .. more];
        var inventory = await _kit.AttachAsync(ids);
        var mailRules = MailRules.From(tables, TransferTestKit.Items)!;
        var escrow = new EscrowService(inventory, _clock, null)
        {
            MailReleaser = new EscrowRepository(_realmDb, new EscrowMailSink(mailRules), _listings), RetryMs = 1_000,
        };
        escrow.Load(await new EscrowRepository(_realmDb).LoadOpenAsync());
        var fees = new FeeService(EconomyRules.From(tables)!.Fees!, inventory, NoTerritoryOwners.Instance, null);
        var mail = new MailService(mailRules, inventory, () => _clock.UtcNowMs);
        foreach (long id in ids)
            mail.Attach(id, MailRecord.Empty);
        var listings = new ListingService(tables.Single<ListingSettings>(), inventory, escrow, fees, mail, _clock, null, _ => 0, 1_000_000);
        var (active, max) = await _listings.LoadActiveAsync();
        listings.Load(active, max);
        escrow.MailReleaseSettled = listings.OnMailReleaseSettled;
        return new World(inventory, escrow, mail, listings);
    }

    private sealed record World(InventoryService Inventory, EscrowService Escrow, MailService Mail, ListingService Listings);

    /// <summary>A save of everything the character has pending in <paramref name="world"/>, at its current version.</summary>
    private async Task<SaveJob> JobAsync(World world, long characterId)
    {
        var record = (await _characters.LoadAsync(characterId))!;
        var saveId = Guid.NewGuid();
        return new SaveJob(characterId, record.State, record.Version, saveId, SaveReason.Class1,
            world.Inventory.TakeBatch(characterId, saveId), Escrow: world.Escrow.TakeBatch(characterId, saveId),
            Mail: world.Mail.TakeBatch(characterId, saveId), Listing: world.Listings.TakeBatch(characterId, saveId));
    }

    private static void Committed(World world, SaveJob job)
    {
        world.Inventory.Committed(job.CharacterId, job.SaveId);
        world.Escrow.Committed(job.CharacterId, job.SaveId);
        world.Mail.Committed(job.CharacterId, job.SaveId);
        world.Listings.Committed(job.CharacterId, job.SaveId);
    }

    private ListingAsk Ask(long characterId, string op) => new(characterId, 1, 1, 0, false, $"{op}:test:{characterId}");

    /// <summary>The seller lists 4 ash at 100 (fee 8); returns the committed listing id.</summary>
    private async Task<long> ListedAsync(World world)
    {
        Assert.Null(world.Listings.Create(Ask(_seller.CharacterId, "listing"), new InventoryLocation(Normal, 0), 4, 100, 0));
        var job = await JobAsync(world, _seller.CharacterId);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Committed(world, job);
        return job.Listing!.Creates.Single().ListingId;
    }

    /// <summary>The buyer's save of a purchase of <paramref name="count"/> units (not yet saved).</summary>
    private async Task<SaveJob> BuyJobAsync(World world, long listingId, int count, string op = "listingBuy", long buyer = 0)
    {
        buyer = buyer == 0 ? _buyer.CharacterId : buyer;
        Assert.Null(world.Listings.Buy(Ask(buyer, op), listingId, count, 100));
        return await JobAsync(world, buyer);
    }

    /// <summary>
    /// Everything of value: every character's units and gold, the held escrows, the open mails (units and gold) and the gold the
    /// fees destroyed, as one number of units plus gold (weighted apart so units and gold cannot cancel out).
    /// </summary>
    private async Task<long> ValueAsync()
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        long units = await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(count) FROM item_stacks), 0)
                 + coalesce((SELECT sum(i.count) FROM escrow_items i JOIN escrow e USING (escrow_id) WHERE e.state = 1), 0)
                 + coalesce((SELECT sum(i.count) FROM mail_items i JOIN mail m USING (mail_id) WHERE m.state = 0), 0)
            """);
        long gold = await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(gold) FROM characters), 0)
                 + coalesce((SELECT sum(gold) FROM mail WHERE state = 0), 0)
                 - coalesce((SELECT sum(delta) FROM gold_ledger WHERE reason = @fee), 0)
            """, new { fee = nameof(InventoryReason.Fee) });
        return units * 1_000_000_000L + gold;
    }

    private async Task AssertConservedAsync()
    {
        Assert.Equal(_before, await ValueAsync());
        await _kit.AssertRebuildsFromAuditAsync(_seller.CharacterId);
        await _kit.AssertRebuildsFromAuditAsync(_buyer.CharacterId);
    }

    private Task<int> CountAsync(string sql) => _kit.CountAsync(sql);

    [Fact]
    public async Task A_create_commits_the_listing_the_escrow_and_the_fee_together()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);

        var (row, state) = (await _listings.FindAsync(id))!.Value;
        Assert.Equal((ListingState.Active, 4, 100L, 8L), (state, row.CountLeft, row.UnitPrice, row.FeePaid));
        Assert.Equal(EscrowState.Held, await new EscrowRepository(_realmDb).StateAsync(row.EscrowId));
        Assert.Equal(92, (await _characters.LoadAsync(_seller.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gold_ledger WHERE reason = '{nameof(InventoryReason.Fee)}' AND delta = -8"));
        Assert.True(world.Listings.IsActive(id));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_part_purchase_moves_the_listing_the_escrow_the_payment_and_the_tax_in_one_transaction()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        var job = await BuyJobAsync(world, id, 3);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Committed(world, job);

        var (row, state) = (await _listings.FindAsync(id))!.Value;
        Assert.Equal((ListingState.Active, 1), (state, row.CountLeft));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM listing_sales WHERE count = 3 AND price = 300 AND tax = 15"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM escrow WHERE state = 2 AND released_to_kind = {(short)EscrowTargetKind.Split}"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM escrow e JOIN escrow_items i USING (escrow_id) WHERE e.state = 1 AND i.count = 1 AND e.escrow_id = '{row.EscrowId}'"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE recipient_id = {_seller.CharacterId} AND kind = {(short)MailKind.ListingProceeds} AND gold = 285"));
        Assert.Equal(700, (await _characters.LoadAsync(_buyer.CharacterId))!.Gold);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM gold_ledger WHERE reason = '{nameof(InventoryReason.Fee)}' AND delta = -15"));
        Assert.Equal(1, world.Listings.Find(id)!.CountLeft);
        await AssertConservedAsync();
        await _kit.AssertGoldMovesAsync(ListingService.PayKey($"listingBuy:test:{_buyer.CharacterId}"), heldOrMailed: 285); // the payment is in the seller's mail; the tax is a sink
    }

    [Fact]
    public async Task A_double_submit_of_a_purchase_writes_it_once()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        var job = await BuyJobAsync(world, id, 4);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _characters.SaveJobAsync(job)));

        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM listing_sales"));
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE kind = {(short)MailKind.ListingProceeds}"));
        Assert.Equal(ListingState.SoldOut, (await _listings.FindAsync(id))!.Value.State);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_retry_of_a_purchase_after_an_unknown_outcome_is_recognised()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        var job = await BuyJobAsync(world, id, 2);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // the outcome was lost: the same job again

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM listing_sales"));
        Assert.Equal(800, (await _characters.LoadAsync(_buyer.CharacterId))!.Gold);
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_crash_between_the_payment_and_the_sale_row_writes_nothing()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        var job = await BuyJobAsync(world, id, 2);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_sale() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_sale BEFORE INSERT ON listing_sales FOR EACH ROW EXECUTE FUNCTION test_crash_sale();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_sale ON listing_sales");
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM listing_sales"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM mail"));
        Assert.Equal(1_000, (await _characters.LoadAsync(_buyer.CharacterId))!.Gold);
        Assert.Equal((ListingState.Active, 4), ((await _listings.FindAsync(id))!.Value.State, (await _listings.FindAsync(id))!.Value.Row.CountLeft));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task Two_worlds_selling_one_listing_at_once_commit_one_sale()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        var buyer2 = await _kit.CreateAsync(gold: 1_000);
        _before = await ValueAsync();
        var first = await BuyJobAsync(world, id, 4, "listingBuy:a");
        var other = await WorldAsync(buyer2.CharacterId); // a second world that saw the same listing (the database is the backstop)
        var second = await BuyJobAsync(other, id, 4, "listingBuy:b", buyer2.CharacterId);

        var results = await Task.WhenAll(_characters.SaveJobAsync(first), _characters.SaveJobAsync(second));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM listing_sales"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_cancel_returns_the_units_by_mail_and_closes_the_listing_once()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        string escrowId = world.Listings.Find(id)!.EscrowId;
        var close = new EscrowHolderClose((short)ListingState.Cancelled);
        var releaser = new EscrowRepository(_realmDb, new EscrowMailSink(MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!), _listings);

        Assert.Equal(EscrowReleaseStatus.Released, await releaser.ReleaseToMailAsync(escrowId, _seller.CharacterId,
            (short)MailKind.ListingReturn, "listingCancel:test:1", _clock.UtcNowMs, close));
        Assert.Equal(EscrowReleaseStatus.Released, await releaser.ReleaseToMailAsync(escrowId, _seller.CharacterId,
            (short)MailKind.ListingReturn, "listingCancel:test:1", _clock.UtcNowMs, close)); // a retry

        Assert.Equal(ListingState.Cancelled, (await _listings.FindAsync(id))!.Value.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail m JOIN mail_items i USING (mail_id) WHERE m.kind = {(short)MailKind.ListingReturn} AND i.count = 4"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_cancel_racing_a_committed_purchase_releases_nothing()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        string held = world.Listings.Find(id)!.EscrowId;
        var job = await BuyJobAsync(world, id, 4);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        var releaser = new EscrowRepository(_realmDb, new EscrowMailSink(MailRules.From(TestCombat.Tables(), TransferTestKit.Items)!), _listings);

        Assert.Equal(EscrowReleaseStatus.Conflict, await releaser.ReleaseToMailAsync(held, _seller.CharacterId,
            (short)MailKind.ListingReturn, "listingCancel:late", _clock.UtcNowMs, new EscrowHolderClose((short)ListingState.Cancelled)));

        Assert.Equal(ListingState.SoldOut, (await _listings.FindAsync(id))!.Value.State);
        Assert.Equal(0, await CountAsync($"SELECT count(*) FROM mail WHERE kind = {(short)MailKind.ListingReturn}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task An_expiry_through_the_world_closes_the_listing_and_mails_the_units_back()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);
        _clock.Advance(TestCombat.Tables().Single<ListingSettings>().DurationsMs[0]);

        world.Listings.ExpireDue(_clock.UtcNowMs);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (world.Listings.Find(id) != null && DateTime.UtcNow < until)
        {
            world.Escrow.Tick();
            await Task.Delay(10);
        }

        Assert.Null(world.Listings.Find(id));
        Assert.Equal(ListingState.Expired, (await _listings.FindAsync(id))!.Value.State);
        Assert.Equal(1, await CountAsync($"SELECT count(*) FROM mail WHERE recipient_id = {_seller.CharacterId} AND kind = {(short)MailKind.ListingReturn}"));
        await AssertConservedAsync();
    }

    [Fact]
    public async Task A_restart_loads_the_active_listings_and_continues_their_ids()
    {
        var world = await WorldAsync();
        long id = await ListedAsync(world);

        var (active, max) = await _listings.LoadActiveAsync();

        Assert.Equal(id, Assert.Single(active).ListingId);
        Assert.Equal(id, max);
        var restarted = await WorldAsync();
        Assert.True(restarted.Listings.IsActive(id));
    }

    [Fact]
    public async Task The_search_over_ten_thousand_listings_stays_within_its_budget_on_an_index()
    {
        await _kit.ExecuteAsync($"""
            INSERT INTO escrow (escrow_id, owner_character_id, holder_kind, holder_id, gold, state, created_at, hold_save_id)
            SELECT 'seed:' || n, {_seller.CharacterId}, 2, n::text, 0, 1, now(), gen_random_uuid() FROM generate_series(1, 10000) n;
            INSERT INTO escrow_items (escrow_id, seq, def_id, rarity, stolen, count, instance_id, non_tradable, properties)
            SELECT 'seed:' || n, 0, 'm1.demonAsh', n % 4, false, 1 + n % 20, 0, false, NULL FROM generate_series(1, 10000) n;
            INSERT INTO listings (listing_id, create_op_key, kind, seller_id, escrow_id, def_id, item_net_id, category, rarity, min_level,
                job_net_id, armor_tier, unit_price, count_initial, count_left, fee_paid, created_at, expires_at, state, create_save_id)
            SELECT n, 'seed:' || n, 1, {_seller.CharacterId}, 'seed:' || n, 'm1.demonAsh', 1 + n % 200, n % 5, n % 4, n % 60, n % 3, n % 4,
                1 + (n * 7919) % 100000, 1 + n % 20, 1 + n % 20, 0, now(), now() + interval '1 day', 1, gen_random_uuid()
            FROM generate_series(1, 10000) n;
            ANALYZE listings; ANALYZE escrow_items;
            """);
        long now = DbNowMs();
        var queries = new List<ListingQuery>
        {
            Query(now),
            Query(now) with { ItemNetIds = [7, 42, 99] },
            Query(now) with { Category = 2 },
            Query(now) with { Category = 1, RarityMin = 1, RarityMax = 2 },
            Query(now) with { LevelMin = 10, LevelMax = 30, JobNetId = 1 },
            Query(now) with { Category = 2, ArmorTier = 2 }, // medium armour (decision 9a, WP-M4-13)
            Query(now) with { ItemNetIds = [5], RarityMin = 3, RarityMax = 3, LevelMin = 1, LevelMax = 50 },
        };
        await _listings.SearchAsync(Query(now)); // warm: the first query opens the pool and plans
        var times = new List<double>();
        foreach (var query in queries)
        {
            var page = query;
            for (int p = 0; p < 3; p++)
            {
                var watch = Stopwatch.StartNew();
                var rows = await _listings.SearchAsync(page);
                times.Add(watch.Elapsed.TotalMilliseconds);
                Assert.True(rows.Zip(rows.Skip(1)).All(pair => (pair.First.UnitPrice, pair.First.ListingId).CompareTo((pair.Second.UnitPrice, pair.Second.ListingId)) < 0));
                if (rows.Count == 0)
                    break;
                page = page with { AfterUnitPrice = rows[^1].UnitPrice, AfterListingId = rows[^1].ListingId };
            }
        }
        times.Sort();
        double p95 = times[(int)Math.Ceiling(times.Count * 0.95) - 1];
        Assert.True(p95 <= 50, $"search p95 {p95:F1} ms over {times.Count} pages (budget 50 ms)");

        await using var connection = await _realmDb.OpenConnectionAsync();
        var (sql, parameters) = ListingRepository.BuildSearch(Query(now) with { Category = 2 });
        string plan = string.Join("\n", await connection.QueryAsync<string>("EXPLAIN " + sql, parameters));
        Assert.DoesNotContain("Seq Scan on listings", plan, StringComparison.Ordinal);
    }

    private static ListingQuery Query(long now) => new([], null, 0, 0, 0, 0, 0, 0, 0, 0, 21, now);

    private long DbNowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // the seeded rows expire a day after the database's now
}
