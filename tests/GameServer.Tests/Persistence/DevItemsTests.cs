using Dapper;
using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;
using Shared.Data;

namespace GameServer.Tests.Persistence;

/// <summary>
/// DbMigrate's dev item seed (wave 1 QA F6: the m3-loop load test's <c>BotScenario.seedItemIds</c>) against a real
/// PostgreSQL: a tool becomes a new instance in the Safe bag's equipment area, a material a stack in the Normal bag, each
/// with its operation and audit row, and a second run of the same seed gives nothing more.
/// </summary>
public sealed class DevItemsTests : IAsyncLifetime
{
    private static readonly CharacterState Spawn = new(12, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly ConfigSet _tables;
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _characters = null!;

    public DevItemsTests()
    {
        var loaded = GameServer.Host.Config.ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All);
        Assert.True(loaded.Ok, string.Join("\n", loaded.Errors));
        _tables = loaded.Set;
    }

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _characters = new CharacterRepository(_realmDb, 1, new FakeClock());
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public async Task The_seed_gives_each_item_once_per_character_with_its_ledger_rows()
    {
        var created = await _characters.CreateAsync(new NewCharacter(1, 0, "Seeded", "warrior", Spawn, Guid.NewGuid()));
        long id = created.Character!.CharacterId;

        for (int run = 0; run < 2; run++)
        {
            var expected = run == 0 ? DevItems.Outcome.Given : DevItems.Outcome.AlreadyGiven;
            Assert.Equal(expected, await DevItems.GiveAsync(_realmDb, _tables, "Seeded", "m3.pickaxeT1", 1));
            Assert.Equal(expected, await DevItems.GiveAsync(_realmDb, _tables, "Seeded", "m3.oreT1", 4));
        }

        await using var connection = await _realmDb.OpenConnectionAsync();
        var pickaxes = (await connection.QueryAsync<(long InstanceId, short Container)>(
            "SELECT instance_id, container FROM item_instances WHERE character_id = @id AND def_id = 'm3.pickaxeT1'", new { id })).ToList();
        var (instanceId, container) = Assert.Single(pickaxes); // a second run does not duplicate the pickaxe
        Assert.Equal(GameServer.Items.ItemInstanceIds.Make(id, 1), instanceId);
        Assert.Equal(Code("safeEquipment"), container);
        var ore = (await connection.QueryAsync<(short Container, int Count)>(
            "SELECT container, count FROM item_stacks WHERE character_id = @id AND def_id = 'm3.oreT1'", new { id })).ToList();
        Assert.Equal((Code(Shared.Data.Tables.ContainerDef.NormalBag), 4), Assert.Single(ore));
        var ops = (await connection.QueryAsync<(string OpKey, string Reason, string Source)>(
            "SELECT op_key, reason, source FROM inventory_ops WHERE character_id = @id ORDER BY op_key", new { id })).ToList();
        Assert.Equal([(DevItems.OpKey(id, "m3.oreT1"), "Admin", DevItems.Source), (DevItems.OpKey(id, "m3.pickaxeT1"), "Admin", DevItems.Source)], ops);
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM audit_log WHERE character_id = @id", new { id }));
        Assert.Equal(instanceId, await connection.ExecuteScalarAsync<long?>(
            "SELECT instance_id FROM audit_log WHERE op_key = @key", new { key = DevItems.OpKey(id, "m3.pickaxeT1") }));
    }

    [Fact]
    public async Task An_unknown_character_item_or_count_is_refused_and_writes_nothing()
    {
        await _characters.CreateAsync(new NewCharacter(2, 0, "Other", "warrior", Spawn, Guid.NewGuid()));

        Assert.Equal(DevItems.Outcome.NoCharacter, await DevItems.GiveAsync(_realmDb, _tables, "Nobody", "m3.oreT1", 1));
        Assert.Equal(DevItems.Outcome.UnknownItem, await DevItems.GiveAsync(_realmDb, _tables, "Other", "no.such.item", 1));
        Assert.Equal(DevItems.Outcome.BadCount, await DevItems.GiveAsync(_realmDb, _tables, "Other", "m3.pickaxeT1", 2));
        Assert.Equal(DevItems.Outcome.BadCount, await DevItems.GiveAsync(_realmDb, _tables, "Other", "m3.oreT1", 0));

        await using var connection = await _realmDb.OpenConnectionAsync();
        Assert.Equal(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM inventory_ops"));
    }

    private short Code(string containerId)
    {
        Assert.True(_tables.TryGet(typeof(Shared.Data.Tables.ContainerDef), out var table));
        return (short)table.Rows.Cast<Shared.Data.Tables.ContainerDef>().Single(c => c.Id == containerId).Code;
    }
}
