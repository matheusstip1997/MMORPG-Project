using GameServer.Events;
using GameServer.Telemetry;
using GameServer.Tests.Support;

namespace GameServer.Tests.Telemetry;

/// <summary>The status, heal and build-up events of backlog D6 (spec 3.7, 3.8, 8.1) become one telemetry record each.</summary>
public class StatusTelemetryTests
{
    private const int WorldId = 3;

    [Fact]
    public void Status_heal_and_build_up_events_become_records_in_bus_order()
    {
        var clock = new FakeClock();
        var bus = new EventBus(WorldId, clock, TestPipelineSettings.BusLimits());
        var queue = new TelemetryQueue(16);
        TelemetryEventConsumer.Attach(bus, queue);

        bus.Publish(new EffectApplied(20, 10, 4, 2, 900, Refreshed: true), 42, 1);
        bus.Publish(new EffectRemoved(20, 4, StatusEndReason.Expired), 0, 1);
        bus.Publish(new EffectCleansed(20, 10, 5, 104), 42, 1);
        bus.Publish(new Healed(10, 20, 102, 0, 24, 130), 42, 1);
        bus.Publish(new BuildupTriggered(20, 10, 1, 4), 42, 1);
        bus.DispatchQueued();

        var buffer = new TelemetryRecord[queue.Capacity];
        var records = buffer.Take(queue.DrainTo(buffer)).ToList();
        long t = clock.UtcNowMs;
        Assert.Equal(
            [
                new TelemetryRecord(TelemetryKind.EffectApplied, t, 1, WorldId, 1, 42, Subject: 20, Target: 10, V0: 4, V1: 2, V2: 900, V3: 1),
                new TelemetryRecord(TelemetryKind.EffectRemoved, t, 2, WorldId, 1, 0, Subject: 20, V0: 4, V1: (long)StatusEndReason.Expired),
                new TelemetryRecord(TelemetryKind.EffectCleansed, t, 3, WorldId, 1, 42, Subject: 20, Target: 10, V0: 5, V1: 104),
                new TelemetryRecord(TelemetryKind.Healed, t, 4, WorldId, 1, 42, Subject: 10, Target: 20, V0: 102, V1: 0, V2: 24, V3: 130),
                new TelemetryRecord(TelemetryKind.BuildupTriggered, t, 5, WorldId, 1, 42, Subject: 20, Target: 10, V0: 1, V1: 4),
            ],
            records);
    }
}
