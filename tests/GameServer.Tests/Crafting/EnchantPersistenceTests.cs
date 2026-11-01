using Dapper;
using GameServer.Crafting;
using GameServer.Events;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;

namespace GameServer.Tests.Crafting;

/// <summary>
/// The enchant operation against a real PostgreSQL (WP-M3-8, spec 5.3 R-5.3-04, rule 5, R-7.1-01/05; CLAUDE.md: double
/// submit, retry, crash in the middle, concurrent access). <see cref="EnchantService"/> consumes the reserved stones and
/// writes the level into the item's properties through <see cref="InventoryService"/> and builds its <c>craft_ops</c> row
/// (kind 5, realm V0015); the character save writes all of it in one transaction, and a login reads the level back.
/// </summary>
public sealed class EnchantPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();
    private const byte Safe = 2;

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _itemRules = ItemRules.From(Tables)!;
    private readonly EnchantRules _enchantRules = EnchantRules.From(Tables)!;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;
    private InventoryRepository _inventory = null!;
    private CraftRepository _craft = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, _clock);
        _inventory = new InventoryRepository(_realmDb);
        _craft = new CraftRepository(_realmDb, _clock);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    /// <summary>A Blacksmith with a forged sword (+<paramref name="level"/>) in the Safe bag and 3 enchant stones, saved.</summary>
    private async Task<CharacterRecord> CreateAsync(int level = 0)
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Smith{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, created.Status);
        var character = (await _characters.LoadAsync(created.Character!.CharacterId))!;
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        var sword = ItemProperties.New(character.CharacterId);
        sword.CraftedStats.Add(new ItemStatBonus("atk", 20, 0));
        sword.Enchant = level;
        Assert.Equal(1, inventory.TryAdd(character.CharacterId, Item("m3.forgedSword"), 0, 1, "seed:sword", InventoryReason.Admin, "test",
            sword).Added);
        Assert.Equal(3, inventory.TryAdd(character.CharacterId, Item("m3.enchantStone"), 0, 3, "seed:stones", InventoryReason.Admin, "test").Added);
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
            inventory.TakeBatch(character.CharacterId, saveId))).Status);
        return (await _characters.LoadAsync(character.CharacterId))!;
    }

    private ItemDef Item(string id) => _itemRules.TryGetItem(id, out var item) ? item : throw new ArgumentException(id);

    private InventoryService Attach(CharacterRecord character, InventoryLoad load)
    {
        var inventory = new InventoryService(_itemRules, null);
        Assert.True(inventory.Attach(character.CharacterId, load, character.ClassId, character.State.Level));
        return inventory;
    }

    /// <summary>A world's view of the loaded character with one attempt applied: the save it would issue.</summary>
    private async Task<(EnchantService Enchant, InventoryService Inventory, InventoryBatch? Items, CraftBatch Craft, Guid SaveId)> ApplyAsync(
        CharacterRecord character, ulong worldSeed = 7)
    {
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        var enchant = new EnchantService(_enchantRules, inventory, new ForgeServiceTests.FakeJobs(), null, () => _clock.UtcNowMs, worldSeed);
        enchant.Attach(character.CharacterId);
        var safe = inventory.CurrentInventory(character.CharacterId);
        Assert.True(safe.TryIndex(Safe, out int index));
        var bag = safe.BagAt(index);
        int slot = Enumerable.Range(0, bag.SlotCount).First(s => bag[s].IsInstance && bag[s].ItemNetId == Item("m3.forgedSword").NetId);
        Assert.Null(enchant.Request(character.CharacterId, new EnchantAsk(1, 1, NpcInteraction.Forge, new InventoryLocation(Safe, slot),
            bag[slot].InstanceId, InReach: true, Blocked: false, Connection: 1))); // applied, waiting for its save
        var saveId = Guid.NewGuid();
        return (enchant, inventory, inventory.TakeBatch(character.CharacterId, saveId), enchant.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, InventoryBatch? items, CraftBatch craft) =>
        _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, items, craft: craft);

    /// <summary>What the database holds: the craft rows, the stones and the sword's level.</summary>
    private async Task<(int Ops, int Stones, int Level)> StoredAsync(long characterId)
    {
        var ops = await _craft.OpsAsync(characterId);
        var load = await _inventory.LoadInventoryAsync(characterId);
        int stones = load.Stacks.Where(s => s.DefId == "m3.enchantStone").Sum(s => s.Count);
        var sword = load.Instances.Single(i => i.DefId == "m3.forgedSword");
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson, out var properties, out _));
        return (ops.Count, stones, properties.Enchant);
    }

    [Fact]
    public async Task An_attempt_commits_its_stones_level_and_audit_row_together_and_a_login_reads_the_level()
    {
        var character = await CreateAsync();
        var (enchant, _, items, craft, saveId) = await ApplyAsync(character);

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);

        Assert.Equal((1, 2, 1), await StoredAsync(character.CharacterId)); // +0 rises at 100 % (placeholder), one stone gone
        var op = Assert.Single(await _craft.OpsAsync(character.CharacterId));
        var applied = Assert.Single(craft.Ops);
        Assert.Equal((applied.OpKey, CraftOpKind.Enchant, "m3.forgedSword", 0, 1, applied.Seed), (op.OpKey, op.Kind, op.DefId, op.EnchantFrom,
            op.EnchantTo, op.Seed)); // the seed is logged (rule 6)
        Assert.Equal(new[] { "m3.enchantStone:0:1" }, op.Inputs);
        await using (var connection = await _realmDb.OpenConnectionAsync())
            Assert.Equal(1, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM audit_log WHERE op_key = @key AND kind = @kind", new { key = op.OpKey + ":lvl", kind = (short)AuditKind.Modify }));

        // A relogin attaches the saved level.
        var reloaded = (await _characters.LoadAsync(character.CharacterId))!;
        var inventory = Attach(reloaded, await _inventory.LoadInventoryAsync(character.CharacterId));
        Assert.True(inventory.TryGetProperties(character.CharacterId, op.OutputInstanceId, out var properties));
        Assert.Equal(1, properties.Enchant);

        // Only the commit answers it (rule 5).
        CommittedEnchantOp? committed = null;
        enchant.OpCommitted = (_, c) => committed = c;
        enchant.Committed(character.CharacterId, saveId);
        Assert.Equal((EnchantReason.None, (byte)1), (committed!.Answer.Reason, committed.Answer.ToLevel));
    }

    [Fact]
    public async Task A_double_submit_applies_it_once()
    {
        var character = await CreateAsync();
        var (_, _, items, craft, saveId) = await ApplyAsync(character);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, saveId, items, craft))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal((1, 2, 1), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Fact]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more()
    {
        var character = await CreateAsync();
        var (_, _, items, craft, saveId) = await ApplyAsync(character);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);
        var once = await StoredAsync(character.CharacterId);

        var retry = await SaveAsync(character, saveId, items, craft); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(once, await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task A_crash_between_the_reservation_and_the_commit_leaves_the_stones_and_the_level_untouched()
    {
        var character = await CreateAsync();
        var (_, _, items, craft, saveId) = await ApplyAsync(character);
        // The row update and the item changes are written, then the audit row fails: the whole save rolls back.
        await using (var connection = await _realmDb.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                CREATE FUNCTION test_crash() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated crash'; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER test_crash BEFORE INSERT ON craft_ops FOR EACH STATEMENT EXECUTE FUNCTION test_crash();
                """);
        }

        await Assert.ThrowsAsync<PostgresException>(() => SaveAsync(character, saveId, items, craft));

        Assert.Equal((0, 3, 0), await StoredAsync(character.CharacterId)); // nothing was consumed, nothing rose: nothing to reroll
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON craft_ops");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status); // the same job again
        Assert.Equal((1, 2, 1), await StoredAsync(character.CharacterId));
    }

    [Fact]
    public async Task Two_attempts_on_one_item_from_one_loaded_state_have_exactly_one_winner()
    {
        var character = await CreateAsync(level: 3);
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each enchanting
        // the same sword with the same stones under its own seed, from the same loaded version.
        var a = await ApplyAsync(character, worldSeed: 1);
        var b = await ApplyAsync(character, worldSeed: 2);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.SaveId, a.Items, a.Craft)),
            Task.Run(() => SaveAsync(character, b.SaveId, b.Items, b.Craft)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        var stored = await StoredAsync(character.CharacterId);
        Assert.Equal((1, 2), (stored.Ops, stored.Stones));
        var winner = results[0].Status == SaveStatus.Saved ? a : b;
        Assert.Equal(Assert.Single(winner.Craft.Ops).EnchantTo, stored.Level);
    }
}
