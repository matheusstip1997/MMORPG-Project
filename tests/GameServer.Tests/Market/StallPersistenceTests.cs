using Dapper;
using GameServer.Economy;
using GameServer.Entities;
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
using Shared.Simulation;
using Shared.Simulation.Items;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Market;

/// <summary>
/// Stalls against a real PostgreSQL (WP-M4-8; spec 4.5, 7.1, 8.2; rule 5): an open writes the stall's <c>shops</c> row with its
/// first listing, the escrow hold and the listing fee in the owner's save, or nothing at all; a double submit and a retry write
/// it once; a close is idempotent; the database holds one open stall per place and per character; a restart brings an open stall
/// back with its listing and closes a stale one. After each the units and gold are conserved.
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class StallPersistenceTests : IAsyncLifetime
{
    private const string Place = "test.stall.a";
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private ShopRepository _shops = null!;
    private ListingRepository _listings = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _owner = null!, _other = null!;
    private uint _nextEntity = 100;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _shops = new ShopRepository(_realmDb);
        _listings = new ListingRepository(_realmDb);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        _owner = await _kit.CreateAsync(ash: 10, gold: 100);
        _other = await _kit.CreateAsync(ash: 10, gold: 100);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A world's stall side over both characters as loaded from the database (the services the world builds).</summary>
    private async Task<World> WorldAsync()
    {
        var tables = TestCombat.Tables();
        long[] ids = [_owner.CharacterId, _other.CharacterId];
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
        var stalls = new StallService(tables.Single<StallSettings>(), listings, _clock, null,
            [new StallPlace(0, Place, FixVec2.FromCm(0, 0), 0)])
        {
            Store = _shops,
            Spawn = (row, place) => new ShopStall(_nextEntity++, row.ShopId, row.OwnerId, row.OwnerName, place.Ordinal, place.MarkerId,
                row.OpenedUtcMs, row.ClosesUtcMs, new MovementState(place.Position, place.Facing), 0),
        };
        listings.Answer = (characterId, connection, answer) => stalls.TryTakeListingAnswer(characterId, connection, answer);
        stalls.Load(await _shops.LoadOpenAsync());
        return new World(inventory, escrow, mail, listings, stalls);
    }

    private sealed record World(InventoryService Inventory, EscrowService Escrow, MailService Mail, ListingService Listings, StallService Stalls);

    private async Task<SaveJob> JobAsync(World world, long characterId)
    {
        var record = (await _characters.LoadAsync(characterId))!;
        var saveId = Guid.NewGuid();
        return new SaveJob(characterId, record.State, record.Version, saveId, SaveReason.Class1,
            world.Inventory.TakeBatch(characterId, saveId), Escrow: world.Escrow.TakeBatch(characterId, saveId),
            Mail: world.Mail.TakeBatch(characterId, saveId), Listing: world.Listings.TakeBatch(characterId, saveId),
            Shop: world.Stalls.TakeBatch(characterId, saveId));
    }

    private static void Committed(World world, SaveJob job)
    {
        world.Inventory.Committed(job.CharacterId, job.SaveId);
        world.Escrow.Committed(job.CharacterId, job.SaveId);
        world.Mail.Committed(job.CharacterId, job.SaveId);
        world.Listings.Committed(job.CharacterId, job.SaveId);
        world.Stalls.Committed(job.CharacterId, job.SaveId);
    }

    private static ListingAsk Ask(long characterId, string op) => new(characterId, 1, 1, 0, false, $"{op}:test:{characterId}");

    /// <summary>The owner opens a stall with 4 ash at 100 (fee 8); returns the save job, not yet saved.</summary>
    private static async Task<SaveJob> OpenJobAsync(StallPersistenceTests test, World world, long owner)
    {
        Assert.Null(world.Stalls.Open(Ask(owner, "stallOpen"), "Owner", 0, 0, 0, new InventoryLocation(Normal, 0), 4, 100));
        return await test.JobAsync(world, owner);
    }

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

    private Task<int> CountAsync(string sql) => _kit.CountAsync(sql);

    [Fact]
    public async Task An_open_commits_the_stall_row_its_listing_the_escrow_and_the_fee_together()
    {
        long before = await ValueAsync();
        var world = await WorldAsync();
        var job = await OpenJobAsync(this, world, _owner.CharacterId);
        var shop = Assert.Single(job.Shop!.Opens);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Committed(world, job);

        var (row, state, reason) = (await _shops.FindAsync(shop.ShopId))!.Value;
        Assert.Equal((Place, _owner.CharacterId, (short)1, ShopCloseReason.None), (row.PlaceMarkerId, row.OwnerId, state, reason));
        var listing = Assert.Single(world.Listings.ActiveForStall(shop.ShopId));
        Assert.Equal((ListingKind.Stall, shop.ClosesUtcMs), (listing.Kind, listing.ExpiresUtcMs));
        Assert.Equal(EscrowState.Held, await new EscrowRepository(_realmDb).StateAsync(listing.EscrowId));
        Assert.Equal(92, (await _characters.LoadAsync(_owner.CharacterId))!.Gold);
        Assert.NotNull(world.Stalls.FindShop(shop.ShopId));
        Assert.Equal(before, await ValueAsync());
    }

    [Fact]
    public async Task A_double_submit_and_a_retry_of_an_open_write_it_once()
    {
        var world = await WorldAsync();
        var job = await OpenJobAsync(this, world, _owner.CharacterId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _characters.SaveJobAsync(job)));
        Assert.All(results, r => Assert.True(r.Status is SaveStatus.Saved or SaveStatus.DuplicateOperation, r.Status.ToString()));
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // a retry after a lost outcome

        Assert.Equal(1, await CountAsync("SELECT count(*) FROM shops"));
        Assert.Equal(1, await CountAsync("SELECT count(*) FROM listings WHERE kind = 2"));
        Assert.Equal(92, (await _characters.LoadAsync(_owner.CharacterId))!.Gold);
    }

    [Fact]
    public async Task A_crash_between_the_listing_and_the_stall_row_writes_nothing()
    {
        long before = await ValueAsync();
        var world = await WorldAsync();
        var job = await OpenJobAsync(this, world, _owner.CharacterId);
        await _kit.ExecuteAsync("""
            CREATE OR REPLACE FUNCTION test_crash_shop() RETURNS trigger AS $$ BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
            CREATE TRIGGER test_crash_shop BEFORE INSERT ON shops FOR EACH ROW EXECUTE FUNCTION test_crash_shop();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => _characters.SaveJobAsync(job));

        await _kit.ExecuteAsync("DROP TRIGGER test_crash_shop ON shops");
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM shops"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM listings"));
        Assert.Equal(0, await CountAsync("SELECT count(*) FROM escrow"));
        Assert.Equal(100, (await _characters.LoadAsync(_owner.CharacterId))!.Gold);
        Assert.Equal(before, await ValueAsync());
    }

    [Fact]
    public async Task The_database_holds_one_open_stall_per_place_and_per_character()
    {
        long now = _clock.UtcNowMs;
        await _kit.ExecuteAsync($"""
            INSERT INTO shops (shop_id, owner_character_id, place_marker_id, owner_name, opened_at, closes_at, state, open_save_id)
            VALUES ('shop:a', {_owner.CharacterId}, '{Place}', 'Owner', now(), now() + interval '1 hour', 1, gen_random_uuid())
            """);
        var samePlace = await Assert.ThrowsAsync<PostgresException>(() => _kit.ExecuteAsync($"""
            INSERT INTO shops (shop_id, owner_character_id, place_marker_id, owner_name, opened_at, closes_at, state, open_save_id)
            VALUES ('shop:b', {_other.CharacterId}, '{Place}', 'Other', now(), now() + interval '1 hour', 1, gen_random_uuid())
            """));
        Assert.Equal("shops_open_place", samePlace.ConstraintName);
        var sameOwner = await Assert.ThrowsAsync<PostgresException>(() => _kit.ExecuteAsync($"""
            INSERT INTO shops (shop_id, owner_character_id, place_marker_id, owner_name, opened_at, closes_at, state, open_save_id)
            VALUES ('shop:c', {_owner.CharacterId}, 'test.stall.b', 'Owner', now(), now() + interval '1 hour', 1, gen_random_uuid())
            """));
        Assert.Equal("shops_open_owner", sameOwner.ConstraintName);

        Assert.True(await _shops.CloseAsync("shop:a", ShopCloseReason.Owner, now, CancellationToken.None));
        Assert.False(await _shops.CloseAsync("shop:a", ShopCloseReason.Owner, now, CancellationToken.None)); // idempotent
        Assert.Equal(((short)2, ShopCloseReason.Owner), ((await _shops.FindAsync("shop:a"))!.Value.State, (await _shops.FindAsync("shop:a"))!.Value.Reason));
        await _kit.ExecuteAsync($"""
            INSERT INTO shops (shop_id, owner_character_id, place_marker_id, owner_name, opened_at, closes_at, state, open_save_id)
            VALUES ('shop:d', {_other.CharacterId}, '{Place}', 'Other', now(), now() + interval '1 hour', 1, gen_random_uuid())
            """); // the place is free once the close committed
    }

    [Fact]
    public async Task An_open_stall_survives_a_restart_and_one_past_its_time_is_closed()
    {
        var world = await WorldAsync();
        var job = await OpenJobAsync(this, world, _owner.CharacterId);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Committed(world, job);
        string shopId = job.Shop!.Opens.Single().ShopId;

        var restarted = await WorldAsync(); // a new world reads the database
        var stall = restarted.Stalls.FindShop(shopId);
        Assert.NotNull(stall);
        Assert.Equal((0, job.Shop.Opens.Single().ClosesUtcMs), (stall!.PlaceOrdinal, stall.ClosesUtcMs));
        Assert.Single(restarted.Listings.ActiveForStall(shopId));

        _clock.Advance(TestCombat.Tables().Single<StallSettings>().DurationMs);
        var later = await WorldAsync(); // past its time: closed at the load (its listing expires with the listing job)
        Assert.Null(later.Stalls.FindShop(shopId));
        var until = DateTime.UtcNow.AddSeconds(5);
        while ((await _shops.FindAsync(shopId))!.Value.State != 2 && DateTime.UtcNow < until)
        {
            later.Stalls.Tick(_clock.UtcNowMs);
            await Task.Delay(20);
        }
        Assert.Equal(((short)2, ShopCloseReason.Stale), ((await _shops.FindAsync(shopId))!.Value.State, (await _shops.FindAsync(shopId))!.Value.Reason));
    }
}
