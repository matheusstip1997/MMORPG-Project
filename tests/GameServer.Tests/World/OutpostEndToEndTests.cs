using System.Diagnostics;
using BotClient.LoadTest;
using GameServer.Events;
using GameServer.Host;
using GameServer.Items;
using GameServer.Persistence;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// The WP-M2-4 "done" scenario over the whole server (spec 4.2; the committed M1 tables and sample world, a real LiteNetLib
/// client, sessions on Redis and PostgreSQL): a character standing by the city outpost sells demon horns and sees the
/// quote fall; after a restart it logs in by the vendor with the gold it earned, buys a potion, and the telemetry files
/// hold the gold created and destroyed, which the BotClient economy report sums per hour.
/// </summary>
public sealed class OutpostEndToEndTests : IAsyncLifetime
{
    private const long Account = 640;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    // Next to the sample world's npc markers (data/world/m1/markers.json): 1 m from each, inside its 3 m reach.
    private const int OutpostStandX = -1_400, OutpostStandY = 600;
    private const int VendorStandX = -2_400, VendorStandY = -1_000;

    private readonly ServerConfig _config = ServerTestEnvironment.LoadConfig();
    private readonly List<Bootstrap> _servers = [];
    private ServerTestEnvironment _env = null!;
    private LoopbackGameClient? _client;

    public async Task InitializeAsync() => _env = await ServerTestEnvironment.CreateAsync();

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        await _env.DisposeAsync();
    }

    [Fact]
    public async Task A_character_sells_horns_sees_the_quote_fall_buys_a_potion_and_telemetry_shows_the_gold()
    {
        var character = await _env.CreateCharacterAsync(Account, xCm: OutpostStandX, yCm: OutpostStandY);
        await SeedHornsAsync(character, 40);
        var server = Start();

        // At the outpost: open it, quote 20 horns, sell them at that quote.
        var client = Connect(server);
        Run(server, client, () => client.Inventories.Count > 0 && Npc(client, "m1.outpost") != 0);
        uint outpost = Npc(client, "m1.outpost");
        client.SendInteract(1, outpost, NpcInteraction.Sell);
        client.SendQuote(2, outpost, 0, 20);
        Run(server, client, () => client.InteractResults.Count == 1 && client.Quotes.Count == 1);
        Assert.Equal(OutpostReason.None, client.InteractResults[0].Reason);
        var first = client.Quotes[0];
        Assert.Equal((OutpostReason.None, 10), (first.Reason, first.FirstUnit));

        client.SendSell(3, outpost, 0, 20, first.Total);
        Run(server, client, () => client.SellResults.Count == 1 && client.Gold.Count > 0 && client.Gold[^1] == first.Total);
        Assert.Equal((OutpostReason.None, (ushort)20, first.Total), (client.SellResults[0].Reason, client.SellResults[0].Count, client.SellResults[0].Gold));
        Assert.Equal(20, client.Inventories[^1].Slots.Single().Count);

        // The quote for the next 20 is lower: the dynamic multiplier fell with the sale (R-4.2-02).
        client.SendQuote(4, outpost, 0, 20);
        Run(server, client, () => client.Quotes.Count == 2);
        Assert.True(client.Quotes[1].Total < first.Total, $"{client.Quotes[1].Total} is not below {first.Total}");
        Assert.True(client.Quotes[1].FirstUnit < first.FirstUnit);
        Assert.Equal(first.Total, await new GoldRepository(_env.RealmDatabase).BalanceAsync(character.CharacterId));

        // A restart; the character comes back by the vendor with its gold and buys a potion.
        Stop(server, client);
        var saved = (await _env.Characters.LoadAsync(character.CharacterId))!;
        Assert.Equal(first.Total, saved.Gold);
        Assert.Equal(SaveStatus.Saved, (await _env.Characters.SaveAsync(character.CharacterId,
            saved.State with { PosXCm = VendorStandX, PosYCm = VendorStandY }, saved.Version, Guid.NewGuid())).Status);
        server = Start();
        client = Connect(server);
        Run(server, client, () => client.Gold.Count > 0 && Npc(client, "m1.vendor") != 0);
        Assert.Equal(first.Total, client.Gold[^1]);
        uint vendor = Npc(client, "m1.vendor");
        Assert.True(_config.Items!.TryGetItem("m2.potionHpSmall", out var potion));

        client.SendInteract(1, vendor, NpcInteraction.Buy);
        client.SendBuy(2, vendor, (ushort)potion.NetId, 1, 25);
        Run(server, client, () => client.BuyResults.Count == 1 && client.Gold[^1] == first.Total - 25);
        Assert.Equal((OutpostReason.None, 25L), (client.BuyResults[0].Reason, client.BuyResults[0].GoldSpent));
        Assert.Contains(client.Inventories[^1].Slots, s => s.ItemNetId == potion.NetId && s.Count == 1);
        Stop(server, client);

        // R-4.2-07: the telemetry holds the gold created by the sale and destroyed by the purchase.
        var records = _servers.SelectMany(s => Directory.GetFiles(_env.TelemetryDirectoryOf(s), "*.jsonl"))
            .SelectMany(path => TelemetryJsonFormat.ReadFile(path).Records).ToList();
        Assert.Contains(records, r => r.Kind == TelemetryKind.GoldChanged && r.V0 == first.Total && r.V2 == (long)InventoryReason.OutpostSale);
        Assert.Contains(records, r => r.Kind == TelemetryKind.GoldChanged && r.V0 == -25 && r.V2 == (long)InventoryReason.VendorPurchase);
        Assert.Contains(records, r => r.Kind == TelemetryKind.GoldDestroyed && r.V0 == 25);
        Assert.Contains(records, r => r.Kind == TelemetryKind.ItemSold && r.V1 == 20 && r.V2 == first.Total);
        Assert.Contains(records, r => r.Kind == TelemetryKind.OutpostVisited);
        var report = EconomyReport.Read(_env.TelemetryDirectoryOf(_servers[0]));
        Assert.Equal((first.Total, 0L, 20L), (report.GoldCreated, report.GoldDestroyed, report.UnitsSold));
        Assert.Equal(25, EconomyReport.Read(_env.TelemetryDirectoryOf(_servers[1])).GoldDestroyed);
    }

    private Bootstrap Start()
    {
        var server = _env.StartServer(_config);
        _servers.Add(server);
        return server;
    }

    private LoopbackGameClient Connect(Bootstrap server)
    {
        _client = new LoopbackGameClient(server.Transport.LocalPort, _env.Ticket(_config, Account));
        return _client;
    }

    private void Stop(Bootstrap server, LoopbackGameClient client)
    {
        client.Dispose();
        _client = null;
        server.Stop(); // graceful: saves the character, flushes the telemetry
    }

    /// <summary>The entity id of the NPC of <paramref name="npcDefId"/> this client was sent, or 0.</summary>
    private uint Npc(LoopbackGameClient client, string npcDefId)
    {
        Assert.True(_config.Economy!.TryGetNpc(npcDefId, out var def));
        return client.Snapshots.SelectMany(s => s.Spawns).Where(s => s.Kind == EntityKind.Npc && s.DefId == def.NetId)
            .Select(s => s.EntityId).FirstOrDefault();
    }

    private async Task SeedHornsAsync(CharacterRecord character, int count)
    {
        var service = new InventoryService(_config.Items!, events: null);
        Assert.True(service.Attach(character.CharacterId, []));
        Assert.True(_config.Items!.TryGetItem("m1.demonHorn", out var horn));
        service.TryAdd(character.CharacterId, horn, 0, count, $"seed:{character.CharacterId}", InventoryReason.Admin, "test");
        var saveId = Guid.NewGuid();
        Assert.Equal(SaveStatus.Saved, (await _env.Characters.SaveAsync(character.CharacterId, character.State, character.Version,
            saveId, service.TakeBatch(character.CharacterId, saveId))).Status);
    }

    /// <summary>Polls the client about once per server tick, sending idle inputs, until <paramref name="done"/>.</summary>
    private void Run(Bootstrap server, LoopbackGameClient client, Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            Assert.True(watch.Elapsed < Deadline, "Timed out waiting for the server.");
            Assert.Null(server.WorldThread.Failure);
            client.Poll();
            if (client.Connected && client.Snapshots.Count > 0)
                client.SendFrame(MoveKeys.None, facing: 0, _config.StartingValues.InputRedundancy, InputActions.None,
                    Math.Max(1, client.Snapshots[^1].Header.ServerTick - 2));
            if (client.Connected && done())
                return;
            Thread.Sleep(1000 / _config.StartingValues.TickRateHz);
        }
    }
}
