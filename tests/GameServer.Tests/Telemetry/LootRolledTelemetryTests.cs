using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Telemetry;

/// <summary>
/// A party loot roll (spec 4.3 R-4.3-02, 8.1 <c>LootRolled</c>; WP-M4-6) becomes one record per candidate with its weight, the units
/// it won and mailed and the roll's seed (rule 6: every roll logged with its seed); a re-dispatched roll writes nothing twice.
/// </summary>
public class LootRolledTelemetryTests
{
    private const int WorldId = 3;

    [Fact]
    public void A_roll_writes_one_record_per_candidate_with_the_seed()
    {
        var clock = new FakeClock();
        var bus = new EventBus(WorldId, clock, TestPipelineSettings.BusLimits());
        var queue = new TelemetryQueue(16);
        var consumer = TelemetryEventConsumer.Attach(bus, queue);
        ulong seed = 0xF00D_0000_0000_0001UL;
        var roll = new LootRolled(7, "run:9:0", 55, 1, 0, 3, seed, 100, [100, 200], [1_000_000, 500_000], [1, 2], [0, 2]);

        var envelope = bus.Publish(roll, 100, 1);
        bus.DispatchQueued();
        consumer.Handle(envelope, roll); // replayed: the watermark keeps it once

        var buffer = new TelemetryRecord[queue.Capacity];
        var records = buffer.Take(queue.DrainTo(buffer)).ToList();
        long t = clock.UtcNowMs;
        long logged = unchecked((long)seed);
        Assert.Equal(
            [
                new TelemetryRecord(TelemetryKind.LootRolled, t, 1, WorldId, 1, 100, Subject: 7, Target: 100, V0: 1_000_000, V1: 1, V2: 0, V3: logged),
                new TelemetryRecord(TelemetryKind.LootRolled, t, 1, WorldId, 1, 100, Subject: 7, Target: 200, V0: 500_000, V1: 2, V2: 2, V3: logged),
            ],
            records);
        Assert.Contains(TelemetrySchema.Kinds, s => s.Kind == TelemetryKind.LootRolled && s.Name == "LootRolled" && s.Target == "member" && s.V3 == "seed");
    }
}
