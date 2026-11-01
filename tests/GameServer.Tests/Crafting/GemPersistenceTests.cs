using Dapper;
using GameServer.Crafting;
using GameServer.Events;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// Gem operations against a real PostgreSQL (WP-M3-9, spec 5.3, rule 5, R-7.1-01/05; CLAUDE.md: double submit, retry, crash in
/// the middle, concurrent access) for a socket that sets its gem, a socket that loses it, and a removal.
/// <see cref="GemService"/> applies the operation through <see cref="InventoryService"/> (the gem or the cost consumed, the
/// item's properties edited, the gem returned) and builds its <c>craft_ops</c> row (kind 6 or 7, the check of realm V0015); the character
/// save writes them in one transaction, and a relogin loads the gems.
/// </summary>
public sealed class GemPersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();
    private const byte Normal = 1;

    public enum Kind { Socket, Lost, Remove }

    private sealed class Bonus(int socketOdds) : IJobBonusQuery
    {
        public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) =>
            kind == JobBonusKind.SocketOddsPercent ? socketOdds : 0;
        public bool MoldTierUnlocked(long characterId, string jobId, int tier) => false;
    }

    private sealed class Jeweler : IJobQuery
    {
        public bool HasJob(long characterId, string jobId) => jobId == "jeweler";
        public int JobLevel(long characterId, string jobId) => jobId == "jeweler" ? 12 : 0;
        public string? JobIn(long characterId, JobSlot slot) => null;
        public int Repeats(long characterId, string jobId, JobActivity activity, int subjectNetId, int lastN) => 0;
    }

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _itemRules = ItemRules.From(Tables)!;
    private readonly GemRules _gemRules = GemRules.From(Tables)!;
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

    /// <summary>A Jeweler with a forged sword, rubies and rough gems, saved; for a removal the sword already holds a ruby.</summary>
    private async Task<CharacterRecord> CreateAsync(Kind kind)
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Jeweler{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, created.Status);
        var character = (await _characters.LoadAsync(created.Character!.CharacterId))!;
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        foreach (var (item, count) in new[] { ("m3.forgedSword", 1), ("m3.rubyT1", 3), ("m3.roughGemT1", 4) })
            Assert.Equal(count, inventory.TryAdd(character.CharacterId, Item(item), 0, count, "seed:" + item, InventoryReason.Admin, "test").Added);
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
            inventory.TakeBatch(character.CharacterId, saveId))).Status);
        character = (await _characters.LoadAsync(character.CharacterId))!;
        if (kind != Kind.Remove)
            return character;
        var (_, items, craft, socketSave) = await ApplyAsync(character, Kind.Socket, worldSeed: 99); // its own operation key
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, socketSave, items, craft)).Status);
        return (await _characters.LoadAsync(character.CharacterId))!;
    }

    private ItemDef Item(string id) => _itemRules.TryGetItem(id, out var item) ? item : throw new ArgumentException(id);

    private InventoryService Attach(CharacterRecord character, InventoryLoad load)
    {
        var inventory = new InventoryService(_itemRules, null);
        Assert.True(inventory.Attach(character.CharacterId, load, character.ClassId, character.State.Level));
        return inventory;
    }

    /// <summary>A world's view of the loaded character with one gem operation applied: the save it would issue.</summary>
    private async Task<(GemService Gems, InventoryBatch? Items, CraftBatch Craft, Guid SaveId)> ApplyAsync(CharacterRecord character,
        Kind kind, ulong worldSeed = 7)
    {
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        var gems = new GemService(_gemRules, inventory, new Jeweler(), new Bonus(kind == Kind.Lost ? -100 : 100), () => _clock.UtcNowMs,
            worldSeed);
        gems.Attach(character.CharacterId);
        var bag = inventory.Current(character.CharacterId);
        int ruby = Enumerable.Range(0, bag.SlotCount).First(s => !bag[s].IsEmpty && bag[s].ItemNetId == Item("m3.rubyT1").NetId);
        var sword = (await _inventory.LoadInventoryAsync(character.CharacterId)).Instances.Single(i => i.DefId == "m3.forgedSword");
        var at = new InventoryLocation((byte)sword.Container, sword.Slot);
        var op = kind == Kind.Remove ? GemOp.Remove : GemOp.Socket;
        Assert.Null(gems.Handle(character.CharacterId, new GemAsk(1, op, 1, true, false, at, sword.InstanceId, ruby, 0, 1)));
        var saveId = Guid.NewGuid();
        return (gems, inventory.TakeBatch(character.CharacterId, saveId), gems.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, InventoryBatch? items, CraftBatch craft) =>
        _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, items, craft: craft);

    /// <summary>What the database holds: the gem rows, the items as "item:count" in id order, and the sword's gems.</summary>
    private async Task<(int Ops, string Items, string Gems)> StoredAsync(long characterId)
    {
        var ops = await _craft.OpsAsync(characterId);
        var load = await _inventory.LoadInventoryAsync(characterId);
        var counts = load.Stacks.GroupBy(s => s.DefId).Select(g => (g.Key, g.Sum(s => s.Count)))
            .Concat(load.Instances.GroupBy(i => i.DefId).Select(g => (g.Key, g.Count())))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => $"{p.Item1}:{p.Item2}");
        var sword = load.Instances.Single(i => i.DefId == "m3.forgedSword");
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson, out var properties, out _));
        return (ops.Count(o => o.Kind is CraftOpKind.Socket or CraftOpKind.Remove), string.Join(",", counts), string.Join("|", properties.Gems));
    }

    private const string Seeded = "m3.forgedSword:1,m3.roughGemT1:4,m3.rubyT1:3";

    [Theory]
    [InlineData(Kind.Socket, 1, "m3.forgedSword:1,m3.roughGemT1:4,m3.rubyT1:2", "m3.rubyT1")]
    [InlineData(Kind.Lost, 1, "m3.forgedSword:1,m3.roughGemT1:4,m3.rubyT1:2", "")]
    [InlineData(Kind.Remove, 2, "m3.forgedSword:1,m3.roughGemT1:2,m3.rubyT1:3", "")]
    public async Task An_operation_commits_its_items_properties_and_audit_row_together_and_loads_again(Kind kind, int ops, string items,
        string socketed)
    {
        var character = await CreateAsync(kind);
        var (gems, batch, craft, saveId) = await ApplyAsync(character, kind);

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, batch, craft)).Status);

        Assert.Equal((ops, items, socketed), await StoredAsync(character.CharacterId));
        var applied = Assert.Single(craft.Ops);
        var op = (await _craft.OpsAsync(character.CharacterId)).Single(o => o.OpKey == applied.OpKey);
        Assert.Equal((applied.OpKey, applied.Kind, "m3.rubyT1", applied.Seed, kind != Kind.Lost),
            (op.OpKey, op.Kind, op.DefId, op.Seed, op.Success)); // the seed is logged (rule 6)

        // Only the commit answers it (rule 5).
        CommittedGemOp? committed = null;
        gems.OpCommitted = (_, c) => committed = c;
        gems.Committed(character.CharacterId, saveId);
        Assert.Equal(GemReason.None, committed!.Answer.Reason);
    }

    [Theory]
    [InlineData(Kind.Socket)]
    [InlineData(Kind.Lost)]
    [InlineData(Kind.Remove)]
    public async Task A_double_submit_applies_it_once(Kind kind)
    {
        var character = await CreateAsync(kind);
        var (_, items, craft, saveId) = await ApplyAsync(character, kind);
        var before = (await StoredAsync(character.CharacterId)).Ops;

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, saveId, items, craft))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(before + 1, (await StoredAsync(character.CharacterId)).Ops);
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Theory]
    [InlineData(Kind.Socket)]
    [InlineData(Kind.Lost)]
    [InlineData(Kind.Remove)]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more(Kind kind)
    {
        var character = await CreateAsync(kind);
        var (_, items, craft, saveId) = await ApplyAsync(character, kind);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);
        var once = await StoredAsync(character.CharacterId);

        var retry = await SaveAsync(character, saveId, items, craft); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(once, await StoredAsync(character.CharacterId));
    }

    [Theory]
    [InlineData(Kind.Socket)]
    [InlineData(Kind.Lost)]
    [InlineData(Kind.Remove)]
    public async Task A_crash_between_the_reservation_and_the_commit_leaves_the_gem_and_the_item_untouched(Kind kind)
    {
        var character = await CreateAsync(kind);
        var before = await StoredAsync(character.CharacterId);
        var (_, items, craft, saveId) = await ApplyAsync(character, kind);
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

        Assert.Equal(before, await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON craft_ops");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status); // the same job again
        Assert.Equal(before.Ops + 1, (await StoredAsync(character.CharacterId)).Ops);
    }

    [Theory]
    [InlineData(Kind.Socket)]
    [InlineData(Kind.Remove)]
    public async Task Two_operations_on_one_item_from_one_loaded_state_have_exactly_one_winner(Kind kind)
    {
        var character = await CreateAsync(kind);
        var before = await StoredAsync(character.CharacterId);
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each working on
        // the same sword under its own seed, from the same loaded version.
        var a = await ApplyAsync(character, kind, worldSeed: 1);
        var b = await ApplyAsync(character, kind, worldSeed: 2);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.SaveId, a.Items, a.Craft)),
            Task.Run(() => SaveAsync(character, b.SaveId, b.Items, b.Craft)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(before.Ops + 1, (await StoredAsync(character.CharacterId)).Ops);
    }

    [Fact]
    public async Task Gem_operations_do_not_count_for_the_conversion_daily_cap()
    {
        var character = await CreateAsync(Kind.Remove);
        Assert.Empty((await _craft.LoadAsync(character.CharacterId)).Conversions);
    }
}
