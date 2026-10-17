using System.Net;
using GameServer.Events;
using GameServer.Net.Security;
using GameServer.Telemetry;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Telemetry;

public class TelemetryBridgeTests
{
    private const int WorldId = 3;

    private static List<TelemetryRecord> Drain(TelemetryQueue queue)
    {
        var buffer = new TelemetryRecord[queue.Capacity];
        return [.. buffer.Take(queue.DrainTo(buffer))];
    }

    [Fact]
    public void Every_M1_event_becomes_one_record_in_bus_order()
    {
        var clock = new FakeClock();
        var bus = new EventBus(WorldId, clock, TestPipelineSettings.BusLimits());
        var queue = new TelemetryQueue(64);
        TelemetryEventConsumer.Attach(bus, queue);

        bus.Publish(new SessionStarted(500, 10, Reattached: true), actorCharacterId: 42, regionId: 1);
        bus.Publish(new SessionEnded(500, 10, SessionEndReason.Disconnected), 42, 1);
        bus.Publish(new Moved(10, -150, 320, 16_384), 42, 1);
        bus.Publish(new ActionStarted(10, 7, 1_234, 8_192, ParryColor.Yellow), 42, 1);
        bus.Publish(new HitLanded(10, 20, 7), 42, 1);
        bus.Publish(new Damaged(10, 20, 7, 120, 880), 42, 1);
        bus.Publish(new Killed(10, 20, 0, 900), 42, 1);
        bus.Publish(new Died(30, 20), 77, 1);
        bus.Publish(new Staggered(20, 10), 0, 1);
        bus.Publish(new ParryAttempted(30, 20, 8, ParryColor.Red, ParryResult.FailedRed), 77, 1);
        bus.DispatchQueued();

        var records = Drain(queue);
        Assert.Equal(
            [
                TelemetryKind.SessionStarted, TelemetryKind.SessionEnded, TelemetryKind.Moved, TelemetryKind.ActionStarted,
                TelemetryKind.HitLanded, TelemetryKind.Damaged, TelemetryKind.Killed, TelemetryKind.Died,
                TelemetryKind.Staggered, TelemetryKind.ParryAttempted,
            ],
            records.Select(r => r.Kind));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), records.Select(r => r.EventId));
        Assert.All(records, r => Assert.Equal(WorldId, r.WorldId));

        var t = clock.UtcNowMs;
        Assert.Equal(new TelemetryRecord(TelemetryKind.SessionStarted, t, 1, WorldId, 1, 42, AccountId: 500, Subject: 10, V0: 1), records[0]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.SessionEnded, t, 2, WorldId, 1, 42, AccountId: 500, Subject: 10,
            V0: (long)SessionEndReason.Disconnected), records[1]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Moved, t, 3, WorldId, 1, 42, Subject: 10, V0: -150, V1: 320, V2: 16_384), records[2]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.ActionStarted, t, 4, WorldId, 1, 42, Subject: 10, V0: 7, V1: 1_234, V2: 8_192,
            V3: (long)ParryColor.Yellow), records[3]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.HitLanded, t, 5, WorldId, 1, 42, Subject: 10, Target: 20, V0: 7), records[4]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Damaged, t, 6, WorldId, 1, 42, Subject: 10, Target: 20, V0: 7, V1: 120, V2: 880), records[5]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Killed, t, 7, WorldId, 1, 42, Subject: 10, Target: 20, V0: 0, V1: 900), records[6]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Died, t, 8, WorldId, 1, 77, Subject: 30, Target: 20), records[7]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Staggered, t, 9, WorldId, 1, 0, Subject: 20, Target: 10), records[8]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.ParryAttempted, t, 10, WorldId, 1, 77, Subject: 30, Target: 20, V0: 8,
            V1: (long)ParryColor.Red, V2: (long)ParryResult.FailedRed), records[9]);

        // Every mapped record survives the file format: only named fields are set.
        foreach (var record in records)
            Assert.Equal(record, RoundTrip(record));
    }

    [Fact]
    public void A_redelivered_event_is_recorded_once()
    {
        var bus = new EventBus(WorldId, new FakeClock(), TestPipelineSettings.BusLimits());
        var queue = new TelemetryQueue(64);
        var consumer = TelemetryEventConsumer.Attach(bus, queue);
        var envelope = bus.Publish(new Damaged(1, 2, 3, 4, 5));
        bus.DispatchQueued();

        consumer.Handle(envelope, new Damaged(1, 2, 3, 4, 5));

        Assert.Single(Drain(queue));
    }

    [Fact]
    public void Bus_publish_dispatch_and_telemetry_enqueue_allocate_nothing_in_steady_state()
    {
        var bus = new EventBus(WorldId, new FakeClock(), TestPipelineSettings.BusLimits());
        var queue = new TelemetryQueue(1_024);
        TelemetryEventConsumer.Attach(bus, queue);
        var buffer = new TelemetryRecord[1_024];

        void Tick()
        {
            for (uint i = 0; i < 100; i++)
            {
                bus.Publish(new Damaged(i, i + 1, 1, 5, 10), actorCharacterId: i);
                bus.Publish(new ActionStarted(i, 2, 3, 4, ParryColor.None));
            }

            bus.DispatchQueued();
            queue.DrainTo(buffer);
        }

        for (var i = 0; i < 20; i++)
            Tick();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
            Tick();

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Security_events_become_records()
    {
        var queue = new TelemetryQueue(64);
        ISecurityEventSink sink = new TelemetrySecurityEventSink(queue);

        sink.Report(new SecurityEvent(SecurityEventKind.ConnectRejected, 1_000, IPAddress.Parse("203.0.113.9"),
            RejectReason: ConnectRejectReason.InvalidTicket));
        sink.Report(new SecurityEvent(SecurityEventKind.InputTagFailed, 2_000, IPAddress.Parse("2001:db8:1:2:aaaa::1"), PeerId: 4,
            AccountId: 500));

        var records = Drain(queue);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Security, 1_000, Subject: -1, V0: (long)SecurityEventKind.ConnectRejected,
            V1: (long)ConnectRejectReason.InvalidTicket, V2: 4, V3: 0xCB00_7109), records[0]);
        Assert.Equal(new TelemetryRecord(TelemetryKind.Security, 2_000, AccountId: 500, Subject: 4,
            V0: (long)SecurityEventKind.InputTagFailed, V2: 6, V3: 0x2001_0db8_0001_0002), records[1]);
        Assert.Equal(records[0], RoundTrip(records[0]));
    }

    [Theory]
    [InlineData(null, 0, 0L)]
    [InlineData("10.1.2.3", 4, 0x0A01_0203L)]
    [InlineData("::ffff:10.1.2.3", 4, 0x0A01_0203L)]
    [InlineData("ffff:ffff:ffff:ffff::1", 6, -1L)]
    public void Addresses_are_kept_as_the_rate_limit_unit(string? address, long family, long prefix)
    {
        var fields = TelemetrySecurityEventSink.AddressFields(address == null ? null : IPAddress.Parse(address));

        Assert.Equal((family, prefix), fields);
    }

    private static TelemetryRecord RoundTrip(in TelemetryRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            TelemetryJsonFormat.WriteRecord(writer, record);
        return TelemetryJsonFormat.ParseRecord(System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }
}
