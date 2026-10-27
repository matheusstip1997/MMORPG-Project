using BotClient.LoadTest;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Economy;

/// <summary>
/// R-4.2-07, spec 7.2 (WP-M2-4): the faucet and sink totals by reason (<see cref="EconomyLedger"/>, the spec's SinkLedger
/// plus faucets), the economy events as telemetry records, and the BotClient economy report reading the files the real
/// writer writes (so a renamed kind or field breaks this test rather than silently giving an empty report).
/// </summary>
public class EconomyTelemetryTests
{
    [Fact]
    public void The_ledger_counts_faucets_and_sinks_by_reason_once_per_event()
    {
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var ledger = new EconomyLedger();
        ledger.Subscribe(bus);

        bus.Publish(new GoldChanged(7, 150, 150, InventoryReason.OutpostSale), 7);
        bus.Publish(new GoldChanged(7, -25, 125, InventoryReason.VendorPurchase), 7);
        bus.Publish(new GoldDestroyed(7, 25, InventoryReason.VendorPurchase), 7);
        bus.Publish(new ItemDestroyed(7, 1, 0, 20, InventoryReason.OutpostSale), 7);
        bus.DispatchQueued();
        ledger.Handle(new EventEnvelope(1, 0, 1, 7, 0), new GoldChanged(7, 150, 150, InventoryReason.OutpostSale)); // redelivered

        Assert.Equal((150L, 25L), (ledger.GoldCreated, ledger.GoldDestroyed));
        Assert.Equal(150, ledger.GoldCreatedBy(InventoryReason.OutpostSale));
        Assert.Equal(25, ledger.GoldDestroyedBy(InventoryReason.VendorPurchase));
        Assert.Equal(20, ledger.ItemsDestroyedBy(InventoryReason.OutpostSale));
        Assert.Equal(0, ledger.GoldCreatedBy(InventoryReason.VendorPurchase));
    }

    [Fact]
    public void Economy_events_become_telemetry_records_and_the_report_sums_gold_per_hour_and_reason()
    {
        using var directory = new TempDirectory();
        var clock = new FakeClock();
        var settings = TestPipelineSettings.Telemetry();
        var queue = new TelemetryQueue(1_024);
        var bus = new EventBus(1, clock, TestPipelineSettings.BusLimits());
        TelemetryEventConsumer.Attach(bus, queue);
        using (var writer = new TelemetryWriter(queue, new JsonLinesTelemetrySink(directory.Path, "run", clock, settings.MaxFileBytes), settings, clock))
        {
            long hour = EconomyReport.HourMs;
            clock.Advance(hour + 1_000); // hour 1
            bus.Publish(new GoldChanged(7, 150, 150, InventoryReason.OutpostSale), 7);
            bus.Publish(new ItemSold(7, 1, 0, 20, 150, 1), 7);
            bus.Publish(new PriceChanged(1, 20_000, 80));
            bus.DispatchQueued();
            clock.Advance(hour); // hour 2
            bus.Publish(new GoldChanged(7, -25, 125, InventoryReason.VendorPurchase), 7);
            bus.Publish(new GoldDestroyed(7, 25, InventoryReason.VendorPurchase), 7);
            bus.Publish(new GoldChanged(8, 40, 40, InventoryReason.OutpostSale), 8);
            bus.Publish(new PriceChanged(1, 18_000, 85));
            bus.Publish(new OutpostVisited(7, 3, 1), 7);
            bus.Publish(new DemandUpdated(1, 2, 9_000, 3_000, 160, 70), regionId: 2);
            bus.DispatchQueued();
            writer.Start();
            writer.Flush();
        }

        var records = Directory.GetFiles(directory.Path, "*.jsonl").SelectMany(p => TelemetryJsonFormat.ReadFile(p).Records).ToList();
        var demand = Assert.Single(records, r => r.Kind == TelemetryKind.DemandUpdated);
        Assert.Equal((2, 1L, 9_000L, 3_000L, 160L, 70L), (demand.RegionId, demand.Subject, demand.V0, demand.V1, demand.V2, demand.V3));
        Assert.Single(records, r => r.Kind == TelemetryKind.OutpostVisited && r.CharacterId == 7 && r.Subject == 3 && r.V0 == 1);

        var report = EconomyReport.Read(directory.Path);

        Assert.Equal(2, report.Hours.Count);
        Assert.Equal((150L, 0L), (report.Hours[0].GoldCreated, report.Hours[0].GoldDestroyed));
        Assert.Equal((40L, 25L), (report.Hours[1].GoldCreated, report.Hours[1].GoldDestroyed));
        Assert.Equal(25, report.Hours[1].DestroyedByReason["vendorPurchase"]);
        Assert.Equal(40, report.Hours[1].CreatedByReason["outpostSale"]);
        Assert.Equal((190L, 25L, 20L), (report.GoldCreated, report.GoldDestroyed, report.UnitsSold));
        Assert.Equal(85, report.PricePercentByItem[1]); // the newest
    }

    [Fact]
    public void The_report_names_every_inventory_reason()
    {
        foreach (InventoryReason reason in Enum.GetValues<InventoryReason>())
            Assert.Equal(reason.ToString(), EconomyReport.ReasonName((long)reason), ignoreCase: true);
    }
}
