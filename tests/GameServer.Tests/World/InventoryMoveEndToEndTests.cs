using System.Diagnostics;
using GameServer.Events;
using GameServer.Host;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Bag moves of the inventory window over the whole server (backlog G client part, WP16; CLAUDE.md: double submit and
/// concurrent access for every transaction): the committed M1 tables, a real LiteNetLib client, sessions on Redis and
/// PostgreSQL. A character with a seeded bag sends sealed move requests; a request sent twice moves once; two moves
/// racing for one stack in one burst apply in order and the second is refused; the answers arrive with the bag once the
/// save committed; the database holds the moved slots with their move audit rows; after a server restart the
/// character logs in again with the same bag.
/// </summary>
public sealed class InventoryMoveEndToEndTests : IAsyncLifetime
{
    private const long Account = 510;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
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
    public async Task Double_submit_and_racing_moves_change_the_saved_bag_once_and_survive_a_restart()
    {
        var character = await _env.CreateCharacterAsync(Account);
        await SeedAsync(character, ("m1.demonHorn", 5), ("m1.demonAsh", 3)); // slots 0 and 1
        var horn = Item("m1.demonHorn");
        var ash = Item("m1.demonAsh");

        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, Account));
        var client = _client;
        Run(client, () => { Idle(client); return client.Inventories.Count > 0; });
        Assert.Equal(2, client.Inventories[^1].Slots.Length);

        // Double submit: the same request twice; applied once, it would swap back the second time.
        client.SendMove(1, 0, 1);
        client.SendMove(1, 0, 1);
        // Racing for one stack: both name slot 1 (the horn after the swap); the second finds it gone.
        client.SendMove(2, 1, 9);
        client.SendMove(3, 1, 10);
        Run(client, () => { Idle(client); return client.MoveResults.Count == 4 && client.Inventories[^1].Slots.Any(s => s.Slot == 9); });

        Assert.Equal((3u, InventoryMoveReason.ItemSlotEmpty), (client.MoveResults[0].RequestId, client.MoveResults[0].Reason));
        Assert.Equal([(1u, InventoryMoveOutcome.Swapped), (1u, InventoryMoveOutcome.Swapped), (2u, InventoryMoveOutcome.Moved)],
            client.MoveResults.Skip(1).Select(r => (r.RequestId, r.Outcome)));
        var shown = client.Inventories[^1].Slots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.ItemNetId, s.Count)).ToList();
        Assert.Equal([((byte)0, (ushort)ash.NetId, (ushort)3), ((byte)9, (ushort)horn.NetId, (ushort)5)], shown);

        var saved = await _env.Inventory.LoadAsync(character.CharacterId);
        Assert.Equal([new ItemStackRow(1, 0, ash.Id, 0, false, 3), new ItemStackRow(1, 9, horn.Id, 0, false, 5)], saved);
        var moveKeys = (await _env.Inventory.OpKeysAsync(character.CharacterId)).Where(k => k.StartsWith("move:")).ToList();
        Assert.Equal(2, moveKeys.Count); // the swap once and the move; the refusal and the repeat wrote nothing
        Assert.All(moveKeys, k => Assert.StartsWith($"move:{_server.ServerInstanceId}:", k));
        var audit = await _env.Inventory.AuditAsync(character.CharacterId);
        Assert.Equal(6, audit.Count(a => a.Kind == AuditKind.Move)); // a swap and a move: two rows per changed slot each

        // A restart: the character logs in again with the bag as it was saved.
        client.Dispose();
        _client = null;
        _server.Stop();
        _server = _env.StartServer(_config);
        _client = new LoopbackGameClient(_server.Transport.LocalPort, _env.Ticket(_config, Account));
        var again = _client;
        Run(again, () => { Idle(again); return again.Inventories.Count > 0; });
        Assert.Equal(shown, again.Inventories[^1].Slots.OrderBy(s => s.Slot).Select(s => (s.Slot, s.ItemNetId, s.Count)));

        // Its request counter starts again at 1: a new move, not a repeat of the first one.
        again.SendMove(1, 9, 2);
        Run(again, () => { Idle(again); return again.MoveResults.Count == 1 && again.Inventories[^1].Slots.Any(s => s.Slot == 2); });
        Assert.Equal(InventoryMoveOutcome.Moved, again.MoveResults[0].Outcome);
    }

    private Shared.Data.Tables.ItemDef Item(string id)
    {
        Assert.True(_config.Items!.TryGetItem(id, out var item));
        return item;
    }

    /// <summary>Puts stacks in the character's bag through InventoryService and a character save (the only item writer).</summary>
    private async Task SeedAsync(CharacterRecord character, params (string Item, int Count)[] stacks)
    {
        var service = new InventoryService(_config.Items!, events: null);
        Assert.True(service.Attach(character.CharacterId, []));
        int i = 0;
        foreach (var (itemId, count) in stacks)
            service.TryAdd(character.CharacterId, Item(itemId), 0, count, $"seed:{character.CharacterId}:{i++}", InventoryReason.Admin, "test");
        var saveId = Guid.NewGuid();
        var result = await _env.Characters.SaveAsync(character.CharacterId, character.State, character.Version, saveId,
            service.TakeBatch(character.CharacterId, saveId));
        Assert.Equal(SaveStatus.Saved, result.Status);
    }

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
