using Dapper;
using GameServer.Crafting;
using GameServer.Events;
using GameServer.Persistence.Migrations;
using GameServer.Items;
using GameServer.Jobs;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Crafting;
using Shared.Simulation.Items;
using Shared.Simulation.Jobs;

namespace GameServer.Tests.Crafting;

/// <summary>
/// The forge operations against a real PostgreSQL (WP-M3-6, spec 5.3, rule 5, R-7.1-01/05; CLAUDE.md: double submit, retry,
/// crash in the middle, concurrent access) for each kind: a mold craft, a refine, a conversion and a mold recipe.
/// <see cref="ForgeService"/> applies the result through <see cref="InventoryService"/> (the reserved inputs consumed, the
/// output made) and builds its <c>craft_ops</c> row; the character save writes them in one transaction.
/// </summary>
public sealed class ForgePersistenceTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);
    private static readonly Shared.Data.ConfigSet Tables = ServerTestEnvironment.LoadTables();
    private const byte Normal = 1;

    public enum Kind { Craft, Refine, Convert, Mold }

    private sealed class AllBonuses : IJobBonusQuery
    {
        public int Get(long characterId, string jobId, JobBonusKind kind, JobActivity activity) => 0;
        public bool MoldTierUnlocked(long characterId, string jobId, int tier) => true;
    }

    private sealed class Smith : IJobQuery
    {
        public bool HasJob(long characterId, string jobId) => jobId == "blacksmith";
        public int JobLevel(long characterId, string jobId) => jobId == "blacksmith" ? 12 : 0;
        public string? JobIn(long characterId, JobSlot slot) => null;
        public int Repeats(long characterId, string jobId, JobActivity activity, int subjectNetId, int lastN) => 0;
    }

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private readonly ItemRules _itemRules = ItemRules.From(Tables)!;
    private readonly ForgeRules _forgeRules = ForgeRules.From(Tables)!;
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

    /// <summary>A Blacksmith with ore, a hide, ingots and the sword mold, saved: the state every test starts from.</summary>
    private async Task<CharacterRecord> CreateAsync()
    {
        long account = _nextAccount++;
        var created = await _characters.CreateAsync(new NewCharacter(account, 0, $"Smith{account}", "warrior", Spawn, Guid.NewGuid()));
        Assert.Equal(CreateCharacterStatus.Created, created.Status);
        var character = (await _characters.LoadAsync(created.Character!.CharacterId))!;
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        foreach (var (item, count) in new[] { ("m3.oreT1", 12), ("m1.cyclopsHide", 2), ("m3.ingotT1", 8), ("m3.moldSword", 1) })
            Assert.Equal(count, inventory.TryAdd(character.CharacterId, Item(item), 0, count, "seed:" + item, InventoryReason.Admin, "test").Added);
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

    /// <summary>A world's view of the loaded character with one forge operation applied: the save it would issue.</summary>
    private async Task<(ForgeService Forge, InventoryBatch? Items, CraftBatch Craft, Guid SaveId)> ApplyAsync(CharacterRecord character,
        Kind kind, ulong worldSeed = 7)
    {
        var inventory = Attach(character, await _inventory.LoadInventoryAsync(character.CharacterId));
        var forge = new ForgeService(_forgeRules, inventory, new Smith(), new AllBonuses(), null, () => _clock.UtcNowMs, worldSeed);
        forge.Attach(character.CharacterId, await _craft.LoadAsync(character.CharacterId));
        var bag = inventory.Current(character.CharacterId);
        int SlotOf(string id) => Enumerable.Range(0, bag.SlotCount).First(s => !bag[s].IsEmpty && bag[s].ItemNetId == Item(id).NetId);
        var (forgeKind, def, inputs) = kind switch
        {
            Kind.Craft => (ForgeKind.Mold, 1, new[] { SlotOf("m3.ingotT1"), SlotOf("m1.cyclopsHide") }),
            Kind.Refine => (ForgeKind.Recipe, 101, new[] { SlotOf("m3.oreT1") }),
            Kind.Convert => (ForgeKind.Recipe, 102, new[] { SlotOf("m3.oreT1") }),
            _ => (ForgeKind.Recipe, 104, new[] { SlotOf("m3.ingotT1") }),
        };
        var reason = forge.Start(character.CharacterId, new ForgeStartAsk(1, 1, NpcInteraction.Forge, forgeKind, def,
            inputs.Select(s => new InventoryLocation(Normal, s)).ToArray(), InReach: true, Blocked: false, Connection: 1));
        Assert.Equal(ForgeReason.None, reason);
        Assert.Null(forge.Finish(character.CharacterId, 2, 1)); // applied, waiting for its save
        var saveId = Guid.NewGuid();
        return (forge, inventory.TakeBatch(character.CharacterId, saveId), forge.TakeBatch(character.CharacterId, saveId)!, saveId);
    }

    private Task<SaveResult> SaveAsync(CharacterRecord character, Guid saveId, InventoryBatch? items, CraftBatch craft) =>
        _characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId, items, craft: craft);

    /// <summary>What the database holds: the forge rows and the items, as "item:count" in id order.</summary>
    private async Task<(int Ops, string Items)> StoredAsync(long characterId)
    {
        var ops = await _craft.OpsAsync(characterId);
        var load = await _inventory.LoadInventoryAsync(characterId);
        var counts = load.Stacks.GroupBy(s => s.DefId).Select(g => (g.Key, g.Sum(s => s.Count)))
            .Concat(load.Instances.GroupBy(i => i.DefId).Select(g => (g.Key, g.Count())))
            .OrderBy(p => p.Item1, StringComparer.Ordinal).Select(p => $"{p.Item1}:{p.Item2}");
        return (ops.Count, string.Join(",", counts));
    }

    private const string Seeded = "m1.cyclopsHide:2,m3.ingotT1:8,m3.moldSword:1,m3.oreT1:12";

    [Fact]
    public async Task A_craft_commits_its_inputs_output_and_audit_row_together_and_loads_again()
    {
        var character = await CreateAsync();
        var (forge, items, craft, saveId) = await ApplyAsync(character, Kind.Craft);

        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);

        var stored = await StoredAsync(character.CharacterId);
        Assert.Equal((1, "m1.cyclopsHide:1,m3.forgedSword:1,m3.ingotT1:6,m3.moldSword:1,m3.oreT1:12"), stored);
        var op = Assert.Single(await _craft.OpsAsync(character.CharacterId));
        var applied = Assert.Single(craft.Ops);
        Assert.Equal((applied.OpKey, CraftOpKind.Craft, "m3.moldSword", applied.Quality, applied.Seed, character.CharacterId),
            (op.OpKey, op.Kind, op.DefId, op.Quality, op.Seed, op.RecipientId)); // the seed is logged (rule 6)
        var sword = (await _inventory.LoadInventoryAsync(character.CharacterId)).Instances.Single(i => i.DefId == "m3.forgedSword");
        Assert.Equal(op.OutputInstanceId, sword.InstanceId);
        Assert.True(ItemProperties.TryParse(sword.PropertiesJson, out var properties, out _));
        Assert.Equal((applied.Quality, character.CharacterId), (properties.Quality, properties.CrafterId));
        Assert.True(properties.HasCraftedStats);

        // Only the commit answers it (rule 5).
        CommittedForgeOp? committed = null;
        forge.OpCommitted = (_, c) => committed = c;
        forge.Committed(character.CharacterId, saveId);
        Assert.Equal(ForgeReason.None, committed!.Answer.Reason);
    }

    [Theory]
    [InlineData(Kind.Craft)]
    [InlineData(Kind.Refine)]
    [InlineData(Kind.Convert)]
    [InlineData(Kind.Mold)]
    public async Task A_double_submit_applies_it_once(Kind kind)
    {
        var character = await CreateAsync();
        var (_, items, craft, saveId) = await ApplyAsync(character, kind);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => SaveAsync(character, saveId, items, craft))));

        Assert.All(results, r => Assert.Equal(SaveStatus.Saved, r.Status));
        Assert.Equal(1, (await StoredAsync(character.CharacterId)).Ops);
        Assert.Equal(character.Version + 1, (await _characters.LoadAsync(character.CharacterId))!.Version);
    }

    [Theory]
    [InlineData(Kind.Craft)]
    [InlineData(Kind.Refine)]
    [InlineData(Kind.Convert)]
    [InlineData(Kind.Mold)]
    public async Task A_retry_after_an_unknown_outcome_that_did_commit_writes_nothing_more(Kind kind)
    {
        var character = await CreateAsync();
        var (_, items, craft, saveId) = await ApplyAsync(character, kind);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);
        var once = await StoredAsync(character.CharacterId);

        var retry = await SaveAsync(character, saveId, items, craft); // the worker never heard the answer

        Assert.Equal(new SaveResult(SaveStatus.Saved, character.Version + 1), retry);
        Assert.Equal(once, await StoredAsync(character.CharacterId));
    }

    [Theory]
    [InlineData(Kind.Craft)]
    [InlineData(Kind.Refine)]
    [InlineData(Kind.Convert)]
    [InlineData(Kind.Mold)]
    public async Task A_crash_between_the_reservation_and_the_commit_leaves_the_inputs_untouched(Kind kind)
    {
        var character = await CreateAsync();
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

        Assert.Equal((0, Seeded), await StoredAsync(character.CharacterId));
        Assert.Equal(character.Version, (await _characters.LoadAsync(character.CharacterId))!.Version);

        await using (var connection = await _realmDb.OpenConnectionAsync())
            await connection.ExecuteAsync("DROP TRIGGER test_crash ON craft_ops");
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status); // the same job again
        Assert.Equal(1, (await StoredAsync(character.CharacterId)).Ops);
    }

    [Theory]
    [InlineData(Kind.Craft)]
    [InlineData(Kind.Refine)]
    [InlineData(Kind.Convert)]
    [InlineData(Kind.Mold)]
    public async Task Two_operations_on_one_loaded_state_have_exactly_one_winner(Kind kind)
    {
        var character = await CreateAsync();
        // Two worlds that both think they own the character (a split brain the ownership lock should prevent), each using the
        // same stack under its own seed, from the same loaded version.
        var a = await ApplyAsync(character, kind, worldSeed: 1);
        var b = await ApplyAsync(character, kind, worldSeed: 2);

        var results = await Task.WhenAll(
            Task.Run(() => SaveAsync(character, a.SaveId, a.Items, a.Craft)),
            Task.Run(() => SaveAsync(character, b.SaveId, b.Items, b.Craft)));

        Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Single(results, r => r.Status == SaveStatus.VersionConflict);
        Assert.Equal(1, (await StoredAsync(character.CharacterId)).Ops);
    }

    [Fact]
    public async Task A_conversion_counts_for_the_next_logins_daily_cap_until_the_world_clocks_utc_midnight()
    {
        var character = await CreateAsync();
        var (_, items, craft, saveId) = await ApplyAsync(character, Kind.Convert);
        Assert.Equal(SaveStatus.Saved, (await SaveAsync(character, saveId, items, craft)).Status);

        var record = await _craft.LoadAsync(character.CharacterId);

        var conversion = Assert.Single(record.Conversions);
        Assert.Equal(2, conversion.ToTier);
        Assert.Equal(ForgeRolls.UtcDay(_clock.UtcNowMs), ForgeRolls.UtcDay(conversion.AtUtcMs));

        // The next UTC day of the world clock (rule 6; not the database's now()): the cap starts again.
        _clock.Advance(ForgeRolls.MsPerDay);
        Assert.Empty((await _craft.LoadAsync(character.CharacterId)).Conversions);
    }
}
