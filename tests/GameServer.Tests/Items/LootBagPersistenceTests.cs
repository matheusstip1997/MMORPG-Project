using Dapper;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Items;
using GameServer.Items.LootBags;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;
using static GameServer.Tests.Items.TransferTestKit;

namespace GameServer.Tests.Items;

/// <summary>
/// Death loot bags against a real PostgreSQL (WP-M4-9; spec 4.3 R-4.3-03, 7.1 schema v21, 8.2; rule 5): the death transaction
/// writes the units that left the character and the bag rows in one save (both or neither); a double submit and a retry apply
/// it once; a crash in the middle writes nothing; a restart reads the bag again and the owner takes it back; two takers of one
/// row and a take racing the expiry let one win; a stolen mark survives the database. After each: every unit over the
/// characters and the open bags is unchanged, and each character rebuilds from its audit ledger (R-7.1-06).
/// </summary>
[Trait("Category", "NoDuplication")]
public sealed class LootBagPersistenceTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private LootBagRepository _bags = null!;
    private TransferTestKit _kit = null!;
    private CharacterRecord _owner = null!, _other = null!;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _bags = new LootBagRepository(_realmDb);
        _kit = new TransferTestKit(_realmDb, _characters, _clock);
        _owner = await _kit.CreateAsync(ash: 10, gold: 30);
        _other = await _kit.CreateAsync();
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>One world: its inventory (the characters from the database), its bag service and a fake world around them.</summary>
    private sealed class World
    {
        public required InventoryService Inventory { get; init; }
        public required DeathLootService Bags { get; init; }
        public required EventBus Bus { get; init; }
        public required Adapter Map { get; init; }

        public sealed class Adapter : ILootBagWorld
        {
            private uint _nextId = 500;
            public Dictionary<uint, Entity> Entities { get; } = new();
            public Dictionary<long, Player> Players { get; } = new();
            public Entity? FindEntity(uint id) => Entities.GetValueOrDefault(id);
            public Player? PlayerOf(long characterId) => Players.GetValueOrDefault(characterId);
            public (string RegionId, string AreaType, RegionFlags Flags)? RegionOf(Entity entity) =>
                ("field1.ring2", "openWorld", new RegionFlags(PvpMode.ArmedOnly, true, DeathDrop.On));
            public LootBag AddBag(string bagId, int xCm, int yCm, int heightCm)
            {
                var bag = new LootBag(_nextId++, bagId, new MovementState(FixVec2.FromCm(xCm, yCm), 0), heightCm);
                Entities.Add(bag.Id, bag);
                return bag;
            }
            public void RemoveBag(LootBag bag) => Entities.Remove(bag.Id);
            public bool CanSee(Player observer, Entity target) => true;
            public bool SameParty(long a, long b) => false;
        }

        public Player Join(uint id, long characterId)
        {
            var player = new Player(id, Player.NoPeer, characterId, new InputQueue(4), new MovementState(FixVec2.FromCm(0, 0), 0), 0, characterId);
            Map.Entities.Add(id, player);
            Map.Players.Add(characterId, player);
            return player;
        }

        public void Die(Player player)
        {
            Bus.Publish(new Died(player.Id, 0), player.CharacterId);
            Bus.DispatchQueued();
        }

        public DeathBag OnlyBag() => Bags.Find(Assert.Single(Map.Entities.Values.OfType<LootBag>()).Id)!;
    }

    private async Task<World> WorldAsync(IEnumerable<LootBagRecord>? open = null, params long[] ids)
    {
        var tables = TestCombat.Tables();
        var inventory = await _kit.AttachAsync(ids.Length == 0 ? [_owner.CharacterId, _other.CharacterId] : ids);
        var bus = new EventBus(1, _clock, TestPipelineSettings.BusLimits());
        var adapter = new World.Adapter();
        var config = LootBagConfig.From(tables, TransferTestKit.Items.Settings)!;
        var world = new World
        {
            Inventory = inventory, Bus = bus, Map = adapter,
            Bags = new DeathLootService(config, TransferTestKit.Items, inventory, adapter, bus, "run" + Guid.NewGuid().ToString("N")[..6], () => _clock.UtcNowMs),
        };
        world.Bags.Subscribe(bus);
        world.Join(1, _owner.CharacterId);
        world.Join(2, _other.CharacterId);
        if (open != null)
            world.Bags.Load(open);
        return world;
    }

    private static SaveJob Job(World world, CharacterRecord character, SaveReason reason)
    {
        var saveId = Guid.NewGuid();
        return new SaveJob(character.CharacterId, character.State, character.Version, saveId, reason,
            world.Inventory.TakeBatch(character.CharacterId, saveId), LootBags: world.Bags.TakeBatch(character.CharacterId, saveId));
    }

    /// <summary>Ash units over both characters and every open bag: equal before and after a case means nothing was made or lost.</summary>
    private async Task<long> AshAsync()
    {
        await using var connection = await _realmDb.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>("""
            SELECT coalesce((SELECT sum(count) FROM item_stacks WHERE def_id = @Ash), 0)
                 + coalesce((SELECT sum(i.count) FROM loot_bag_items i JOIN loot_bags b USING (bag_id) WHERE b.state = 1 AND i.def_id = @Ash), 0)
            """, new { Ash });
    }

    private async Task AssertRebuildsAsync()
    {
        await _kit.AssertRebuildsFromAuditAsync(_owner.CharacterId);
        await _kit.AssertRebuildsFromAuditAsync(_other.CharacterId);
    }

    /// <summary>The owner died and the death's save committed.</summary>
    private async Task<(World World, string BagId)> DroppedAsync()
    {
        var world = await WorldAsync();
        world.Die(world.Map.Players[_owner.CharacterId]);
        var job = Job(world, _owner, SaveReason.Death);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        world.Inventory.Committed(_owner.CharacterId, job.SaveId);
        world.Bags.Committed(_owner.CharacterId, job.SaveId);
        _owner = (await _characters.LoadAsync(_owner.CharacterId))!;
        return (world, job.LootBags!.Creates.Single().BagId);
    }

    [Fact]
    public async Task The_death_commits_the_units_out_of_the_bag_and_the_bag_rows_together()
    {
        var (_, bagId) = await DroppedAsync();

        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM item_stacks WHERE character_id = @id", new { id = _owner.CharacterId }));
        var stored = await _bags.FindAsync(bagId);
        Assert.Equal(LootBagState.Open, stored!.Value.State);
        Assert.Equal(10, stored.Value.Bag.Items.Sum(i => i.Count));
        Assert.Equal(_owner.CharacterId, stored.Value.Bag.OwnerCharacterId);
        Assert.Equal(30, (await _characters.LoadAsync(_owner.CharacterId))!.Gold); // gold never drops
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task A_double_submit_of_the_death_save_writes_it_once()
    {
        var world = await WorldAsync();
        world.Die(world.Map.Players[_owner.CharacterId]);
        var job = Job(world, _owner, SaveReason.Death);

        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status); // a retry after an unknown outcome

        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM loot_bags"));
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM loot_bag_ops"));
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task A_crash_between_the_bag_rows_and_the_bag_op_writes_neither_and_the_retry_writes_both()
    {
        var world = await WorldAsync();
        world.Die(world.Map.Players[_owner.CharacterId]);
        var job = Job(world, _owner, SaveReason.Death);
        await _kit.CrashOnAsync("loot_bag_ops", _owner.CharacterId);

        await Assert.ThrowsAnyAsync<Exception>(() => _characters.SaveJobAsync(job));

        Assert.Equal(10, await _kit.CountAsync("SELECT coalesce(sum(count), 0)::int FROM item_stacks WHERE character_id = @id",
            new { id = _owner.CharacterId })); // the units never left
        Assert.Equal(0, await _kit.CountAsync("SELECT count(*) FROM loot_bags"));
        await _kit.NoCrashAsync("loot_bag_ops");
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM loot_bags"));
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task A_restart_reads_the_bag_again_and_the_owner_takes_it_back()
    {
        var (_, bagId) = await DroppedAsync();

        var restarted = await WorldAsync(await _bags.LoadOpenAsync());
        var bag = restarted.OnlyBag();
        Assert.Equal((bagId, 10), (bag.Id, bag.Units));
        restarted.Bags.Request(restarted.Map.Players[_owner.CharacterId], 1, bag.Entity!.Id, LootBagOp.TakeAll, 0);
        var job = Job(restarted, _owner, SaveReason.LootBag);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(job)).Status);

        Assert.Equal(LootBagState.Emptied, (await _bags.FindAsync(bagId))!.Value.State);
        Assert.Equal(10, await _kit.CountAsync("SELECT coalesce(sum(count), 0)::int FROM item_stacks WHERE character_id = @id",
            new { id = _owner.CharacterId }));
        Assert.Empty(await _bags.LoadOpenAsync());
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task Two_worlds_taking_one_row_let_one_win_and_the_other_save_conflicts()
    {
        var (_, _) = await DroppedAsync();
        var open = await _bags.LoadOpenAsync();
        var first = await WorldAsync(open);
        var second = await WorldAsync(open);
        _clock.Advance(60_000);
        first.Bags.Request(first.Map.Players[_owner.CharacterId], 1, first.OnlyBag().Entity!.Id, LootBagOp.TakeAll, 0);
        second.Bags.Request(second.Map.Players[_other.CharacterId], 1, second.OnlyBag().Entity!.Id, LootBagOp.TakeAll, 0);
        var ownerJob = Job(first, _owner, SaveReason.LootBag);
        var otherJob = Job(second, _other, SaveReason.LootBag);

        var results = await Task.WhenAll(_characters.SaveJobAsync(ownerJob), _characters.SaveJobAsync(otherJob));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict); // the guard: one take of the row ever
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task A_take_racing_the_expiry_loses_and_the_expiry_retried_reports_what_it_destroyed()
    {
        var (world, bagId) = await DroppedAsync();
        world.Bags.Request(world.Map.Players[_owner.CharacterId], 1, world.OnlyBag().Entity!.Id, LootBagOp.TakeAll, 0);
        var take = Job(world, _owner, SaveReason.LootBag);

        var expired = await _bags.ExpireAsync(bagId, _clock.UtcNowMs);
        Assert.Equal((LootBagExpireStatus.Expired, 10), (expired.Status, expired.Items.Sum(i => i.Count)));
        Assert.Equal(SaveStatus.VersionConflict, (await _characters.SaveJobAsync(take)).Status); // the bag is gone: nothing is written

        var again = await _bags.ExpireAsync(bagId, _clock.UtcNowMs); // a retry after an unknown outcome
        Assert.Equal((LootBagExpireStatus.AlreadyExpired, 10), (again.Status, again.Items.Sum(i => i.Count)));
        Assert.Equal(0, await AshAsync()); // destroyed once: a sink
        Assert.Equal(1, await _kit.CountAsync("SELECT count(*) FROM loot_bag_ops WHERE kind = 3"));
        await AssertRebuildsAsync();
    }

    [Fact]
    public async Task A_stolen_mark_survives_the_database()
    {
        var (world, _) = await DroppedAsync();
        _clock.Advance(60_000);
        long at = _clock.UtcNowMs;
        world.Bags.Request(world.Map.Players[_other.CharacterId], 1, world.OnlyBag().Entity!.Id, LootBagOp.TakeAll, 0);
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveJobAsync(Job(world, _other, SaveReason.LootBag))).Status);

        var reloaded = await new InventoryRepository(_realmDb).LoadAsync(_other.CharacterId);
        var stack = Assert.Single(reloaded);
        Assert.Equal((true, _owner.CharacterId, at, 10), (stack.Stolen, stack.StolenFrom, stack.StolenAtMs, stack.Count));
        Assert.Equal(10, await AshAsync());
        await AssertRebuildsAsync();
        var audit = await new InventoryRepository(_realmDb).AuditAsync(_other.CharacterId);
        Assert.Contains(audit, a => a.StolenFrom == _owner.CharacterId && a.StolenAtMs == at);
    }
}
