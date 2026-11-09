using BotClient.LoadTest;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using Shared.Simulation.Economy;

namespace GameServer.Tests.Economy;

/// <summary>
/// A fee in a running world (spec 4.2 R-4.2-04/05, 7.2; WP-M4-1, "Done"): the world's <see cref="FeeService"/> charges a test
/// sale's tax inside the sale's operation; once the class 1 save committed, <see cref="FeeCharged"/> and
/// <see cref="GoldDestroyed"/> (reason <c>Fee</c>) go out, <see cref="EconomyLedger"/> counts the destroyed gold by reason,
/// the telemetry records them (<c>FeeCharged</c>, kind 104), and <c>BotClient report --kind economy</c> shows the sink as
/// <c>fee</c>.
/// </summary>
public class FeeWorldTests
{
    [Fact]
    public void A_test_sale_charges_its_tax_and_the_destroyed_gold_shows_in_the_ledger_the_telemetry_and_the_report()
    {
        using var directory = new TempDirectory();
        var fixture = new EconomyFixture(gold: 0, bag: [EconomyFixture.Row(0, EconomyFixture.Horn, 10)]);
        var fees = Assert.IsType<FeeService>(fixture.Harness.World.Fees);
        var recorder = new FeeRecorder();
        fixture.Bus.Subscribe<FeeCharged>(recorder);
        var clock = new FakeClock();
        var settings = TestPipelineSettings.Telemetry();
        var queue = new TelemetryQueue(1_024);
        TelemetryEventConsumer.Attach(fixture.Bus, queue);
        long id = fixture.CharacterId;
        byte region = fixture.Player.RegionIndex;

        // The test caller: a sale of 4 horns for 400 gold to another player (WP-M4-7 brings the real one), the tax in its operation.
        Assert.True(fixture.Inventory.TryRemove(id, 0, 4, "test:sale:1:items", InventoryReason.Admin, "test:sale"));
        Assert.True(fixture.Inventory.TryAddGold(id, 400, "test:sale:1:gold", InventoryReason.Admin, "test:sale").Applied);
        var tax = fees.Quote(id, region, FeeType.SaleTax, 400);
        Assert.Equal(FeeApplyOutcome.Applied, fees.Apply(id, tax, "test:sale:1", "test:sale").Outcome);
        fixture.Inventory.RequestClass1Save(id);
        fixture.Commit();

        Assert.Equal(380, fixture.Inventory.CommittedGold(id));
        Assert.Equal(new FeeCharged(id, FeeType.SaleTax, region, 400, 20, 0, 20, "test:sale:1:fee"), Assert.Single(recorder.Fees));
        Assert.Contains(new GoldDestroyed(id, 20, InventoryReason.Fee), fixture.Events.GoldDestroyed);
        var ledger = fixture.Harness.World.Ledger!;
        Assert.Equal(20, ledger.GoldDestroyedBy(InventoryReason.Fee));

        using (var writer = new TelemetryWriter(queue, new JsonLinesTelemetrySink(directory.Path, "run", clock, settings.MaxFileBytes), settings, clock))
        {
            writer.Start();
            writer.Flush();
        }
        var records = Directory.GetFiles(directory.Path, "*.jsonl").SelectMany(p => TelemetryJsonFormat.ReadFile(p).Records).ToList();
        var charged = Assert.Single(records, r => r.Kind == TelemetryKind.FeeCharged);
        Assert.Equal(((long)FeeType.SaleTax, (long)region, 400L, 20L, 0L, 20L, id),
            (charged.Subject, charged.Target, charged.V0, charged.V1, charged.V2, charged.V3, charged.CharacterId));
        Assert.Single(records, r => r.Kind == TelemetryKind.GoldDestroyed && r.V0 == 20 && r.V1 == (long)InventoryReason.Fee);

        var report = EconomyReport.Read(directory.Path);
        Assert.Equal(20, report.GoldDestroyedByReason["fee"]);
        Assert.Equal(20, Assert.Single(report.Hours).DestroyedByReason["fee"]);
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
