using BotClient.LoadTest;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Market;
using GameServer.Tests.Support;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Economy;

/// <summary>
/// A fee in a running world (spec 4.2 R-4.2-04/05, 7.2; WP-M4-1, "Done"; the real callers since WP-M4-7): the world's
/// <see cref="FeeService"/> charges a listing's fee and a sale's tax inside their operations; once the class 1 save committed, <see cref="FeeCharged"/> and
/// <see cref="GoldDestroyed"/> (reason <c>Fee</c>) go out, <see cref="EconomyLedger"/> counts the destroyed gold by reason,
/// the telemetry records them (<c>FeeCharged</c>, kind 104), and <c>BotClient report --kind economy</c> shows the sink as
/// <c>fee</c>.
/// </summary>
public class FeeWorldTests
{
    [Fact]
    public void A_listing_and_its_sale_charge_their_fees_and_the_destroyed_gold_shows_in_the_ledger_the_telemetry_and_the_report()
    {
        using var directory = new TempDirectory();
        // The real callers (WP-M4-7): an auction listing pays the listing fee in the seller's save, its sale the sale tax in the
        // buyer's save (the test sale of WP-M4-1 is gone, plan section 7).
        var f = new ListingWorldTests.Fixture();
        var recorder = new FeeRecorder();
        f.Bus.Subscribe<FeeCharged>(recorder);
        var clock = new FakeClock();
        var settings = TestPipelineSettings.Telemetry();
        var queue = new TelemetryQueue(1_024);
        TelemetryEventConsumer.Attach(f.Bus, queue);

        long listing = f.List(count: 4, unitPrice: 100); // the listing fee: 2 % of 400
        f.Harness.SendListingBuy(2, 1, f.Npc, listing, 4, 100); // the sale tax: 5 % of 400
        f.Harness.Ticks(4);

        Assert.Equal(new[] { (f.Seller, FeeType.Listing, 400L, 8L), (f.Buyer, FeeType.SaleTax, 400L, 20L) },
            recorder.Fees.Select(c => (c.CharacterId, c.FeeType, c.Amount, c.Destroyed)));
        Assert.Equal(92, f.Inventory.CommittedGold(f.Seller));
        Assert.Equal(600, f.Inventory.CommittedGold(f.Buyer)); // the buyer pays the price; the seller's 380 waits in the mail
        Assert.Equal(28, f.Ledger.GoldDestroyedBy(InventoryReason.Fee));
        Assert.Equal(0, f.Ledger.GoldCreated);

        using (var writer = new TelemetryWriter(queue, new JsonLinesTelemetrySink(directory.Path, "run", clock, settings.MaxFileBytes), settings, clock))
        {
            writer.Start();
            writer.Flush();
        }
        var records = Directory.GetFiles(directory.Path, "*.jsonl").SelectMany(p => TelemetryJsonFormat.ReadFile(p).Records).ToList();
        var charged = records.Where(r => r.Kind == TelemetryKind.FeeCharged).ToList();
        Assert.Equal(new[] { ((long)FeeType.Listing, 400L, 8L, 8L), ((long)FeeType.SaleTax, 400L, 20L, 20L) },
            charged.Select(r => (r.Subject, r.V0, r.V1, r.V3)));
        Assert.Equal(2, records.Count(r => r.Kind == TelemetryKind.GoldDestroyed && r.V1 == (long)InventoryReason.Fee));
        Assert.Single(records, r => r.Kind == TelemetryKind.ListingCreated && r.V3 == 8);
        Assert.Single(records, r => r.Kind == TelemetryKind.ListingSold && r.V2 == 400 && r.V3 == 20);

        var report = EconomyReport.Read(directory.Path);
        Assert.Equal(28, report.GoldDestroyedByReason["fee"]);
        Assert.Equal(28, Assert.Single(report.Hours).DestroyedByReason["fee"]);
    }

    [Fact]
    public void The_ledger_counts_every_reason_up_to_the_last_one_appended()
    {
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var ledger = new EconomyLedger();
        ledger.Subscribe(bus);

        bus.Publish(new GoldDestroyed(7, 3, InventoryReason.GemRemove), 7);
        bus.Publish(new GoldDestroyed(7, 20, InventoryReason.Fee), 7);
        bus.Publish(new ItemDestroyed(7, 1, 0, 2, InventoryReason.GemSocketFailed), 7);
        bus.DispatchQueued();

        Assert.Equal(Enum.GetValues<InventoryReason>().Max(r => (int)r) + 1, EconomyLedger.ReasonCount);
        Assert.Equal(3, ledger.GoldDestroyedBy(InventoryReason.GemRemove));
        Assert.Equal(20, ledger.GoldDestroyedBy(InventoryReason.Fee));
        Assert.Equal(2, ledger.ItemsDestroyedBy(InventoryReason.GemSocketFailed));
        Assert.Equal(23, ledger.GoldDestroyed);
    }

    [Fact]
    public void The_fee_reason_is_a_gold_sink()
    {
        Assert.Equal(GameServer.Persistence.GoldFlow.Sink, EconomyFlows.Of(InventoryReason.Fee, -5));
    }

    private sealed class FeeRecorder : IEventConsumer<FeeCharged>
    {
        public List<FeeCharged> Fees { get; } = [];

        public void Handle(in EventEnvelope envelope, in FeeCharged payload) => Fees.Add(payload);
    }
}
