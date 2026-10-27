using System.Diagnostics;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog G over the whole server: the committed M1 world and tables, a real LiteNetLib client, sessions on Redis and
/// PostgreSQL. A warrior kills the demon at <c>m1.spawn.combat.a</c>; its loot appears as a LootDrop spawn; the client asks
/// to pick it up (a sealed reliable MemoryPack request) and gets the item in the container its category goes to (R-4.1-02)
/// once the class 2 save committed; the item is in the database; after a server restart the character logs in again and
/// gets the same item. Runs once per receiving container, with the demon's loot narrowed to items that go there, so both
/// routes run every time (the demon's table mixes M1 materials and a few M2 equipment drops). Runs about 15 s each.
/// </summary>
public sealed class LootEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private const string DemonLootTable = "m1.demon";
    private const long Account = 410;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private ServerConfig _config = null!;
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync() => _env = await ServerTestEnvironment.CreateAsync();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Theory]
    [InlineData("normalBag")]
    [InlineData("safeEquipment")]
    public async Task Kill_the_demon_pick_up_its_drop_and_find_it_again_after_a_restart(string receivingContainer)
    {
        _config = ConfigWithDemonDroppingInto(receivingContainer);
        var rules = _config.Items!;
        var container = rules.Containers.Single(c => c.Id == receivingContainer);
        _server = _env.StartServer(_config);
        Assert.True(_server.World.Map.TryGetMarker(SpawnPointId, out var marker));
        int markerX = FixedPoint.ToCmRound(marker.Position.X);
        int markerY = FixedPoint.ToCmRound(marker.Position.Y);
        var character = await _env.CreateCharacterAsync(Account, xCm: markerX, yCm: markerY - 150);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, Account));
        var client = _client;

        uint? monsterId = null;
        Run(client, () =>
        {
            Idle(client);
            var monster = client.Snapshots.SelectMany(s => s.Entities).FirstOrDefault(e => e.XCm == markerX && e.ZCm == markerY);
            monsterId = monster.EntityId == 0 ? null : monster.EntityId;
            return monsterId != null;
        }, "the demon in a snapshot");
        Run(client, () => { Idle(client); return client.Inventories.Count > 0; }, "the first bag");
        Assert.Empty(SlotsOf(client.Inventories[0], container)); // a new character's empty receiving container

        int frame = 0;
        Run(client, () =>
        {
            frame++;
            var actions = frame % 15 == 1 ? InputActions.Attack : InputActions.None;
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, actions, ViewTick(client));
            return client.Hits.Any(h => h.Hit.TargetId == monsterId && h.Hit.Kind == HitKind.Killed);
        }, "the demon killed");

        // The demon's loot lands where it died (phase 11) and reaches the client as a LootDrop spawn.
        SpawnEvent drop = default;
        Run(client, () =>
        {
            Idle(client);
            drop = client.Snapshots.SelectMany(s => s.Spawns).FirstOrDefault(s => s.Kind == EntityKind.LootDrop);
            return drop.EntityId != 0;
        }, "the LootDrop spawn");
        Assert.True(rules.TryGetItem(drop.DefId, out var item));
        Assert.Equal(receivingContainer, rules.ReceiverOf(item.Category)?.Id);

        client.SendPickup(drop.EntityId);
        Run(client, () => { Idle(client); return client.PickupResults.Count > 0; }, "the pickup result");
        var result = Assert.Single(client.PickupResults);
        Assert.Equal((drop.EntityId, PickupReason.None), (result.DropEntityId, result.Reason));
        Assert.True(result.Taken > 0);

        // The container arrives once the batch is in the database (rule 5), and the drop is gone from the ground.
        Run(client, () => { Idle(client); return SlotsOf(client.Inventories[^1], container).Length > 0; },
            $"the item in the {receivingContainer} container");
        var slot = Assert.Single(SlotsOf(client.Inventories[^1], container));
        Assert.Equal(((ushort)item.NetId, result.Taken), (slot.ItemNetId, slot.Count));
        Run(client, () => { Idle(client); return client.Snapshots.Any(s => s.Despawns.Contains(drop.EntityId)); }, "the drop's despawn");
        var saved = await _env.Inventory.LoadInventoryAsync(character.CharacterId);
        var savedItems = saved.Stacks.Where(s => s.Container == container.Code).Select(s => (s.Slot, s.DefId, s.Count))
            .Concat(saved.Instances.Where(i => i.Container == container.Code).Select(i => (i.Slot, i.DefId, Count: 1)));
        Assert.Equal((slot.Slot, item.Id, (int)result.Taken), Assert.Single(savedItems));
        // Besides the pickup: the starter weapon granted at the first world entry (WP-M2-1).
        var keys = await _env.Inventory.OpKeysAsync(character.CharacterId);
        Assert.Contains(InventoryOpKeys.Starter(character.CharacterId), keys);
        var opKey = Assert.Single(keys, k => k != InventoryOpKeys.Starter(character.CharacterId));
        Assert.StartsWith(_server.ServerInstanceId + ":", opKey);

        // A restart: the drop is gone with the old world (volatile), the item is not.
        client.Dispose();
        _client = null;
        _server.Stop(); // graceful: saves, releases the lock; the environment disposes it at the end
        _server = _env.StartServer(_config);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, Account));
        var again = _client;
        Run(again, () => { Idle(again); return again.Inventories.Count > 0; }, "the bag after the restart");
        var reloaded = Assert.Single(SlotsOf(again.Inventories[^1], container));
        Assert.Equal((slot.Slot, slot.ItemNetId, slot.Count), (reloaded.Slot, reloaded.ItemNetId, reloaded.Count));
    }

    /// <summary>The repository's tables with the demon dropping only items whose category goes to <paramref name="containerId"/>.</summary>
    private static ServerConfig ConfigWithDemonDroppingInto(string containerId)
    {
        var rules = ServerTestEnvironment.LoadConfig().Items!;
        var tables = ServerTestEnvironment.LoadTables();
        Assert.True(tables.TryGet(typeof(ItemDef), out var items));
        Assert.True(tables.TryGet(typeof(LootTableEntry), out var entries));
        var categories = items.Rows.Cast<ItemDef>().ToDictionary(i => i.Id, i => i.Category);
        var kept = entries.Rows.Cast<LootTableEntry>()
            .Where(e => e.TableId != DemonLootTable || rules.ReceiverOf(categories[e.ItemId])?.Id == containerId)
            .ToArray<object>();
        Assert.Contains(kept.Cast<LootTableEntry>(), e => e.TableId == DemonLootTable);
        tables.Add(new ConfigTable(typeof(LootTableEntry), entries.Version, kept));
        return ServerConfig.From(tables);
    }

    /// <summary>A container of a committed-contents message: the Normal bag in the M1 fields, the others in <c>Containers</c>.</summary>
    private InventorySlotEntry[] SlotsOf(InventoryContents contents, ContainerDef container) =>
        container.Code == _config.Items!.NormalBag.Code ? contents.Slots : contents.Containers.Single(c => c.Code == container.Code).Slots;

    /// <summary>The server tick this client would render others at: its newest snapshot, 2 ticks of interpolation behind.</summary>
    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
    }

    /// <summary>
    /// Calls <paramref name="step"/> about once per server tick (polling the client) until it returns true. A disconnected
    /// client fails at once: no step can succeed after that.
    /// </summary>
    private void Run(LoopbackGameClient client, Func<bool> step, string what)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            if (watch.Elapsed >= Deadline || client.Disconnected)
            {
                var text = new System.Text.StringBuilder(client.Disconnected
                    ? $"The client was disconnected after {watch.Elapsed.TotalSeconds:F1} s waiting for {what}."
                    : $"Timed out after {watch.Elapsed.TotalSeconds:F1} s waiting for {what}.");
                text.Append($"\nServer tick {_server.World.ServerTick}");
                ServerTestEnvironment.AppendClients(text, [client]);
                Assert.Fail(text.ToString());
            }
            Assert.Null(_server.WorldThread.Failure);
            client.Poll();
            if (client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
