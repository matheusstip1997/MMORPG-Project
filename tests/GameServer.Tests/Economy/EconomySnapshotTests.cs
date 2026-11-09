using BotClient.LoadTest;
using GameServer.Economy;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Economy;

/// <summary>
/// Economy snapshots (WP-M4-5, R-7.2-02, spec 7.1 <c>faucet_sink_snapshots</c>): what changed since the previous snapshot by
/// reason, taken after a demand control step and no more often than <c>EconomySettings.snapshotIntervalMs</c>; the ledger
/// counting every reason; the reason names the dashboard and the BotClient report share.
/// </summary>
public class EconomySnapshotTests
{
    private const long Start = 1_800_000_000_000;

    private static (EventBus Bus, EconomyLedger Ledger) Ledger()
    {
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var ledger = new EconomyLedger();
        ledger.Subscribe(bus);
        return (bus, ledger);
    }

    [Fact]
    public void A_snapshot_holds_what_changed_since_the_previous_one_by_reason()
    {
        var (bus, ledger) = Ledger();
        var snapshots = new EconomySnapshots(ledger, intervalMs: 60_000, startedMs: Start);
        bus.Publish(new GoldChanged(7, 150, 150, InventoryReason.OutpostSale), 7);
        bus.Publish(new GoldChanged(7, -25, 125, InventoryReason.VendorPurchase), 7);
        bus.Publish(new GoldDestroyed(7, 25, InventoryReason.VendorPurchase), 7);
        bus.Publish(new ItemDestroyed(7, 1, 0, 20, InventoryReason.OutpostSale), 7);
        bus.DispatchQueued();

        var first = snapshots.TryTake(controlSteps: 1, Start + 60_000, goldOnline: 125, activePlayers: 1)!;

        Assert.Equal((Start + 60_000, Start, 150L, 25L, 125L, 1), (first.TakenAtMs, first.PeriodStartMs, first.GoldCreated,
            first.GoldDestroyed, first.GoldOnline, first.ActivePlayers));
        Assert.Equal([new EconomyReasonDelta(InventoryReason.OutpostSale, 150, 0, 20), new EconomyReasonDelta(InventoryReason.VendorPurchase, 0, 25, 0)],
            first.ByReason);

        bus.Publish(new GoldChanged(8, 40, 40, InventoryReason.OutpostSale), 8);
        bus.Publish(new ItemDestroyed(8, 2, 0, 3, InventoryReason.GemSocketFailed), 8);
        bus.DispatchQueued();
        var second = snapshots.TryTake(2, Start + 120_000, 165, 2)!;

        Assert.Equal((Start + 60_000, 40L, 0L), (second.PeriodStartMs, second.GoldCreated, second.GoldDestroyed));
        Assert.Equal([new EconomyReasonDelta(InventoryReason.OutpostSale, 40, 0, 0), new EconomyReasonDelta(InventoryReason.GemSocketFailed, 0, 0, 3)],
            second.ByReason);
    }

    [Fact]
    public void Snapshots_wait_for_a_control_step_and_the_interval()
    {
        var (_, ledger) = Ledger();
        var snapshots = new EconomySnapshots(ledger, intervalMs: 60_000, startedMs: Start);

        Assert.Null(snapshots.TryTake(0, Start + 10_000, 0, 0)); // no control step ran yet
        Assert.NotNull(snapshots.TryTake(1, Start + 10_000, 0, 0)); // the first step
        Assert.Null(snapshots.TryTake(1, Start + 90_000, 0, 0)); // no new step
        Assert.Null(snapshots.TryTake(2, Start + 69_999, 0, 0)); // a step, but within the interval
        Assert.NotNull(snapshots.TryTake(3, Start + 70_000, 0, 0));

        var everyStep = new EconomySnapshots(ledger, intervalMs: 0, startedMs: Start);
        Assert.NotNull(everyStep.TryTake(1, Start + 1, 0, 0));
        Assert.NotNull(everyStep.TryTake(2, Start + 2, 0, 0));
    }

    [Fact]
    public void The_final_snapshot_is_taken_only_when_something_moved()
    {
        var (bus, ledger) = Ledger();
        var snapshots = new EconomySnapshots(ledger, 60_000, Start);
        Assert.Null(snapshots.TakeFinal(Start + 5_000, 0, 0));

        bus.Publish(new ItemDestroyed(7, 1, 0, 2, InventoryReason.ItemUsed), 7);
        bus.DispatchQueued();
        var last = snapshots.TakeFinal(Start + 5_000, 0, 0)!;

        Assert.Equal(new EconomyReasonDelta(InventoryReason.ItemUsed, 0, 0, 2), Assert.Single(last.ByReason));
        Assert.Null(snapshots.TakeFinal(Start + 6_000, 0, 0));
    }

    [Fact]
    public void The_final_snapshot_is_strictly_after_the_previous_one_so_its_row_is_never_taken_for_a_repeat()
    {
        var (bus, ledger) = Ledger();
        var snapshots = new EconomySnapshots(ledger, 60_000, Start);
        bus.Publish(new GoldChanged(7, 10, 10, InventoryReason.OutpostSale), 7);
        bus.DispatchQueued();
        var step = snapshots.TryTake(1, Start + 60_000, 10, 1)!;

        bus.Publish(new GoldChanged(7, 5, 15, InventoryReason.OutpostSale), 7);
        bus.DispatchQueued();
        var last = snapshots.TakeFinal(Start + 60_000, 15, 1)!; // the clock did not move since the control step

        Assert.Equal(Start + 60_000, step.TakenAtMs);
        Assert.Equal((Start + 60_001, Start + 60_000, 5L), (last.TakenAtMs, last.PeriodStartMs, last.GoldCreated));

        // Without a previous snapshot the final one is simply taken now.
        var (bus2, ledger2) = Ledger();
        var first = new EconomySnapshots(ledger2, 60_000, Start);
        bus2.Publish(new GoldChanged(8, 3, 3, InventoryReason.OutpostSale), 8);
        bus2.DispatchQueued();
        Assert.Equal(Start, first.TakeFinal(Start, 3, 1)!.TakenAtMs);
    }

    [Fact]
    public void The_ledger_counts_every_reason_of_the_enum_the_gem_reasons_included()
    {
        var (bus, ledger) = Ledger();
        int highest = Enum.GetValues<InventoryReason>().Max(r => (int)r);
        Assert.Equal(highest + 1, EconomyLedger.ReasonCount);

        bus.Publish(new ItemDestroyed(7, 1, 0, 1, InventoryReason.GemSocketFailed), 7);
        bus.Publish(new ItemDestroyed(7, 1, 0, 2, InventoryReason.GemRemove), 7);
        bus.DispatchQueued();

        Assert.Equal((1L, 2L), (ledger.ItemsDestroyedBy(InventoryReason.GemSocketFailed), ledger.ItemsDestroyedBy(InventoryReason.GemRemove)));
    }

    [Fact]
    public void The_dashboard_names_reasons_as_the_economy_report_does()
    {
        // The report lists every value up to the highest (a gap left for a reason merged later prints its number); compare the
        // defined ones (WP-M4-4 holds 23-25 while mail's 21-22 are not on main yet).
        Assert.Equal(EconomyReport.InventoryReasonNames.Where((_, value) => value > 0 && Enum.IsDefined((InventoryReason)value)),
            EconomyReasonNames.All);
        Assert.Equal("outpostSale", EconomyReasonNames.OfLedger(nameof(InventoryReason.OutpostSale)));
        Assert.Equal("someFutureReason", EconomyReasonNames.OfLedger("SomeFutureReason"));
    }

    [Fact]
    public void The_world_hands_a_snapshot_to_its_sink_after_a_control_step_and_never_writes_it_itself()
    {
        // A one-second control step and a snapshot after every step, so the test steps the clock as the fixture's saves do.
        var f = new EconomyFixture(0, [EconomyFixture.Row(0, EconomyFixture.Horn, 20)], tables =>
        {
            tables.Single<Shared.Data.Tables.DemandSettings>().ControlIntervalMs = 1_000;
            tables.Single<Shared.Data.Tables.EconomySettings>().SnapshotIntervalMs = 0;
        });
        var sink = new RecordingSink();
        f.Harness.World.EconomySnapshotSink = sink;
        f.Harness.SendSell(1, 1, f.NpcId(EconomyFixture.Outpost), 0, 10, 1);
        f.Commit();
        long sold = f.Inventory.CommittedGold(f.CharacterId);
        Assert.True(sold > 0);
        int before = sink.Snapshots.Count;

        f.Harness.Clock.Advance(1_000);
        f.Harness.Ticks(1);

        Assert.Equal(before + 1, sink.Snapshots.Count);
        var snapshot = sink.Snapshots[^1];
        Assert.All(sink.ThreadIds, id => Assert.Equal(Environment.CurrentManagedThreadId, id)); // queued on the tick, nothing else
        Assert.Equal((sold, 0L), (sink.Snapshots.Sum(s => s.GoldCreated), sink.Snapshots.Sum(s => s.GoldDestroyed)));
        Assert.Equal((sold, 1), (snapshot.GoldOnline, snapshot.ActivePlayers));
        Assert.Equal(new EconomyReasonDelta(InventoryReason.OutpostSale, sold, 0, 10),
            Assert.Single(sink.Snapshots.SelectMany(s => s.ByReason)));

        f.Harness.Ticks(3); // no new control step: no new snapshot
        Assert.Equal(before + 1, sink.Snapshots.Count);
    }

    private sealed class RecordingSink : IEconomySnapshotSink
    {
        public List<EconomySnapshot> Snapshots { get; } = [];
        public List<int> ThreadIds { get; } = [];

        public void Save(EconomySnapshot snapshot)
        {
            Snapshots.Add(snapshot);
            ThreadIds.Add(Environment.CurrentManagedThreadId);
        }
    }
}
