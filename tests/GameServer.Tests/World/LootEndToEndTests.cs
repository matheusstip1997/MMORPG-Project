using System.Diagnostics;
using GameServer.Host;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog G over the whole server: the committed M1 world and tables, a real LiteNetLib client, sessions on Redis and
/// PostgreSQL. A warrior kills the demon at <c>m1.spawn.combat.a</c>; its loot appears as a LootDrop spawn; the client asks
/// to pick it up (a sealed reliable MemoryPack request) and gets the item in its bag once the class 2 save committed; the
/// item is in the database; after a server restart the character logs in again and gets the same bag.
/// Runs about 15 s of real time.
/// </summary>
public sealed class LootEndToEndTests : IAsyncLifetime
{
    private const string SpawnPointId = "m1.spawn.combat.a";
    private const long Account = 410;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);
    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private ServerTestEnvironment _env = null!;
    private Bootstrap _server = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync()
    {
        _env = await ServerTestEnvironment.CreateAsync();
        _server = _env.StartServer(_config);
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task Kill_the_demon_pick_up_its_drop_and_find_it_again_after_a_restart()
    {
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
        });
        Run(client, () => { Idle(client); return client.Inventories.Count > 0; });
        Assert.Empty(client.Inventories[0].Slots); // the empty bag of a new character

        int frame = 0;
        Run(client, () =>
        {
            frame++;
            var actions = frame % 15 == 1 ? InputActions.Attack : InputActions.None;
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, actions, ViewTick(client));
            return client.Hits.Any(h => h.Hit.TargetId == monsterId && h.Hit.Kind == HitKind.Killed);
        });

        // The demon's loot lands where it died (phase 11) and reaches the client as a LootDrop spawn.
        SpawnEvent drop = default;
        Run(client, () =>
        {
            Idle(client);
            drop = client.Snapshots.SelectMany(s => s.Spawns).FirstOrDefault(s => s.Kind == EntityKind.LootDrop);
            return drop.EntityId != 0;
        });
        Assert.True(_config.Items!.TryGetItem(drop.DefId, out var item));

        client.SendPickup(drop.EntityId);
        Run(client, () => { Idle(client); return client.PickupResults.Count > 0; });
        var result = Assert.Single(client.PickupResults);
        Assert.Equal((drop.EntityId, PickupReason.None), (result.DropEntityId, result.Reason));
        Assert.True(result.Taken > 0);

        // The bag arrives once the batch is in the database (rule 5), and the drop is gone from the ground.
        Run(client, () => { Idle(client); return client.Inventories[^1].Slots.Length > 0; });
        var slot = Assert.Single(client.Inventories[^1].Slots);
        Assert.Equal(((ushort)item.NetId, result.Taken), (slot.ItemNetId, slot.Count));
        Run(client, () => { Idle(client); return client.Snapshots.Any(s => s.Despawns.Contains(drop.EntityId)); });
        var saved = Assert.Single(await _env.Inventory.LoadAsync(character.CharacterId));
        Assert.Equal((item.Id, (int)result.Taken), (saved.DefId, saved.Count));
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
        Run(again, () => { Idle(again); return again.Inventories.Count > 0; });
        var reloaded = Assert.Single(again.Inventories[^1].Slots);
        Assert.Equal((slot.Slot, slot.ItemNetId, slot.Count), (reloaded.Slot, reloaded.ItemNetId, reloaded.Count));
    }

    /// <summary>The server tick this client would render others at: its newest snapshot, 2 ticks of interpolation behind.</summary>
    private static uint ViewTick(LoopbackGameClient client) =>
        client.Snapshots.Count == 0 ? 0 : Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2);

    private void Idle(LoopbackGameClient client)
    {
        if (client.Snapshots.Count > 0)
            client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None, ViewTick(client));
    }

    /// <summary>Calls <paramref name="step"/> about once per server tick (polling the client) until it returns true.</summary>
    private void Run(LoopbackGameClient client, Func<bool> step)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < Deadline, "Timed out waiting for the server.");
            Assert.Null(_server.WorldThread.Failure);
            client.Poll();
            if (client.Connected && step())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
