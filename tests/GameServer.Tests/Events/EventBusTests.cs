using GameServer.Events;
using GameServer.Tests.Support;

namespace GameServer.Tests.Events;

public class EventBusTests
{
    private const int WorldId = 7;

    /// <summary>Logs every delivery as text: event ID, type, and payload.</summary>
    private sealed class Recorder : IEventConsumer<Damaged>, IEventConsumer<Killed>, IEventConsumer<Died>
    {
        public List<string> Log { get; } = [];

        public List<EventEnvelope> Envelopes { get; } = [];

        public void Handle(in EventEnvelope envelope, in Damaged payload) => Add(envelope, payload.ToString());

        public void Handle(in EventEnvelope envelope, in Killed payload) => Add(envelope, payload.ToString());

        public void Handle(in EventEnvelope envelope, in Died payload) => Add(envelope, payload.ToString());

        private void Add(in EventEnvelope envelope, string payload)
        {
            Envelopes.Add(envelope);
            Log.Add($"{envelope.EventId}@{envelope.UtcMs}:{payload}");
        }

        public void SubscribeAll(EventBus bus)
        {
            bus.Subscribe<Damaged>(this);
            bus.Subscribe<Killed>(this);
            bus.Subscribe<Died>(this);
        }
    }

    private sealed class Lambda<T>(Action<EventEnvelope, T> handle) : IEventConsumer<T> where T : struct, IGameEvent
    {
        public void Handle(in EventEnvelope envelope, in T payload) => handle(envelope, payload);
    }

    private static EventBus NewBus(FakeClock? clock = null, int maxEventsPerDispatch = 10_000,
        Action<EventEnvelope, Exception>? onError = null) =>
        new(WorldId, clock ?? new FakeClock(), TestPipelineSettings.BusLimits(maxEventsPerDispatch), onError);

    [Fact]
    public void Nothing_is_delivered_before_phase_11()
    {
        var bus = NewBus();
        var recorder = new Recorder();
        recorder.SubscribeAll(bus);

        bus.Publish(new Damaged(1, 2, 3, 10, 90));

        Assert.Empty(recorder.Log);
        Assert.Equal(1, bus.PendingCount);
    }

    [Fact]
    public void Events_are_delivered_in_publish_order_across_types_with_ascending_ids()
    {
        var bus = NewBus();
        var recorder = new Recorder();
        recorder.SubscribeAll(bus);

        bus.Publish(new Damaged(1, 2, 3, 10, 90));
        bus.Publish(new Died(5, 6));
        bus.Publish(new Damaged(1, 2, 3, 90, 0));
        bus.Publish(new Killed(1, 2, 0, 44));

        Assert.Equal(4, bus.DispatchQueued());

        Assert.Equal([1L, 2, 3, 4], recorder.Envelopes.Select(e => e.EventId));
        Assert.StartsWith("1@", recorder.Log[0]);
        Assert.Contains("Died", recorder.Log[1]);
        Assert.Contains("Amount = 90", recorder.Log[2]);
        Assert.Contains("Killed", recorder.Log[3]);
        Assert.Equal(0, bus.PendingCount);
    }

    [Fact]
    public void Envelope_carries_world_time_actor_and_region()
    {
        var clock = new FakeClock(1_800_000_000_000);
        var bus = NewBus(clock);
        var recorder = new Recorder();
        recorder.SubscribeAll(bus);

        clock.Advance(50);
        var returned = bus.Publish(new Died(5, 6), actorCharacterId: 42, regionId: 3);
        bus.DispatchQueued();

        var expected = new EventEnvelope(1, 1_800_000_000_050, WorldId, 42, 3);
        Assert.Equal(expected, returned);
        Assert.Equal(expected, Assert.Single(recorder.Envelopes));
    }

    [Fact]
    public void Two_runs_with_the_same_inputs_deliver_the_same_log()
    {
        static List<string> Run()
        {
            var clock = new FakeClock();
            var bus = NewBus(clock);
            var recorder = new Recorder();
            recorder.SubscribeAll(bus);
            // A consumer that reacts by publishing, as Combat consumers will.
            bus.Subscribe(new Lambda<Damaged>((envelope, damaged) =>
            {
                if (damaged.HpAfter == 0)
                    bus.Publish(new Killed(damaged.SourceId, damaged.TargetId, 0, 9), envelope.ActorCharacterId, envelope.RegionId);
            }));

            for (var tick = 0; tick < 50; tick++)
            {
                for (uint entity = 1; entity <= 5; entity++)
                    bus.Publish(new Damaged(entity, entity + 10, 1, tick, (tick + (int)entity) % 7 == 0 ? 0 : 100));
                bus.DispatchQueued();
                clock.Advance(50);
            }

            return recorder.Log;
        }

        var first = Run();
        var second = Run();

        Assert.True(first.Count > 250);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Consumers_of_one_type_run_in_subscription_order()
    {
        var bus = NewBus();
        var calls = new List<string>();
        bus.Subscribe(new Lambda<Died>((_, _) => calls.Add("first")));
        bus.Subscribe(new Lambda<Died>((_, _) => calls.Add("second")));
        bus.Subscribe(new Lambda<Died>((_, _) => calls.Add("third")));

        bus.Publish(new Died(1, 2));
        bus.DispatchQueued();

        Assert.Equal(["first", "second", "third"], calls);
    }

    [Fact]
    public void Events_published_during_dispatch_are_delivered_in_the_same_dispatch_after_queued_ones()
    {
        var bus = NewBus();
        var recorder = new Recorder();
        // Subscribed first, so it publishes Killed before the recorder sees the Damaged that caused it.
        bus.Subscribe(new Lambda<Damaged>((envelope, damaged) =>
        {
            if (damaged.HpAfter == 0)
                bus.Publish(new Killed(damaged.SourceId, damaged.TargetId, 0, 9), envelope.ActorCharacterId);
        }));
        recorder.SubscribeAll(bus);

        bus.Publish(new Damaged(1, 2, 3, 100, 0));
        bus.Publish(new Died(2, 1));

        Assert.Equal(3, bus.DispatchQueued());

        Assert.Equal([1L, 2, 3], recorder.Envelopes.Select(e => e.EventId));
        Assert.Contains("Damaged", recorder.Log[0]);
        Assert.Contains("Died", recorder.Log[1]);
        Assert.Contains("Killed", recorder.Log[2]);
        Assert.Equal(0, bus.PendingCount);
    }

    [Fact]
    public void Dispatch_stops_at_the_cap_and_carries_the_rest_to_the_next_tick()
    {
        var bus = NewBus(maxEventsPerDispatch: 3);
        var recorder = new Recorder();
        recorder.SubscribeAll(bus);
        for (uint i = 1; i <= 5; i++)
            bus.Publish(new Died(i, 0));

        Assert.Equal(3, bus.DispatchQueued());
        Assert.Equal(2, bus.PendingCount);
        Assert.Equal(2, bus.CarriedOverTotal);

        Assert.Equal(2, bus.DispatchQueued());
        Assert.Equal([1L, 2, 3, 4, 5], recorder.Envelopes.Select(e => e.EventId));
    }

    [Fact]
    public void A_consumer_that_always_republishes_cannot_stall_the_tick()
    {
        var bus = NewBus(maxEventsPerDispatch: 100);
        bus.Subscribe(new Lambda<Died>((_, died) => bus.Publish(died)));
        bus.Publish(new Died(1, 0));

        Assert.Equal(100, bus.DispatchQueued());
        Assert.Equal(1, bus.PendingCount);
    }

    [Fact]
    public void A_throwing_consumer_is_counted_and_does_not_stop_others()
    {
        var failures = new List<(EventEnvelope Envelope, Exception Exception)>();
        var bus = NewBus(onError: (envelope, exception) => failures.Add((envelope, exception)));
        var after = 0;
        bus.Subscribe(new Lambda<Died>((_, _) => throw new InvalidOperationException("boom")));
        bus.Subscribe(new Lambda<Died>((_, _) => after++));

        bus.Publish(new Died(1, 0));
        bus.Publish(new Died(2, 0));
        bus.DispatchQueued();

        Assert.Equal(2, after);
        Assert.Equal(2, bus.ConsumerFailures);
        Assert.Equal([1L, 2], failures.Select(f => f.Envelope.EventId));
        Assert.All(failures, f => Assert.Equal("boom", f.Exception.Message));
    }

    [Fact]
    public void Dispatch_is_not_reentrant()
    {
        Exception? failure = null;
        var bus = NewBus(onError: (_, exception) => failure = exception);
        bus.Subscribe(new Lambda<Died>((_, _) => bus.DispatchQueued()));

        bus.Publish(new Died(1, 0));
        bus.DispatchQueued();

        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void A_consumer_subscribed_during_dispatch_sees_only_later_events()
    {
        var bus = NewBus();
        var late = new Recorder();
        var subscribed = false;
        bus.Subscribe(new Lambda<Died>((_, _) =>
        {
            if (subscribed)
                return;
            subscribed = true;
            bus.Subscribe<Died>(late);
        }));

        bus.Publish(new Died(1, 0));
        bus.Publish(new Died(2, 0));
        bus.DispatchQueued();

        Assert.Equal([2L], late.Envelopes.Select(e => e.EventId));
    }

    /// <summary>An idempotent consumer of the kind other systems will write: counts kills once per event ID.</summary>
    private sealed class KillCounter : IEventConsumer<Killed>
    {
        private EventWatermark _watermark;

        public int Kills { get; private set; }

        public void Handle(in EventEnvelope envelope, in Killed payload)
        {
            if (_watermark.TryAccept(envelope))
                Kills++;
        }
    }

    [Fact]
    public void Idempotent_consumer_applies_a_redelivered_event_once()
    {
        var bus = NewBus();
        var counter = new KillCounter();
        bus.Subscribe(counter);
        var first = bus.Publish(new Killed(1, 2, 0, 9));
        var second = bus.Publish(new Killed(1, 3, 0, 9));
        bus.DispatchQueued();

        // Redelivery, for example a replay after a failed dispatch.
        counter.Handle(first, new Killed(1, 2, 0, 9));
        counter.Handle(second, new Killed(1, 3, 0, 9));

        Assert.Equal(2, counter.Kills);
    }

    [Fact]
    public void Watermark_accepts_ascending_ids_only()
    {
        var watermark = new EventWatermark();
        static EventEnvelope Id(long id) => new(id, 0, WorldId, 0, 0);

        Assert.True(watermark.TryAccept(Id(1)));
        Assert.True(watermark.TryAccept(Id(3)));
        Assert.False(watermark.TryAccept(Id(3)));
        Assert.False(watermark.TryAccept(Id(2)));
        Assert.Equal(3, watermark.LastEventId);
    }

    private sealed class NoOp : IEventConsumer<Damaged>, IEventConsumer<Died>, IEventConsumer<Moved>
    {
        public long Sum;

        public void Handle(in EventEnvelope envelope, in Damaged payload) => Sum += payload.Amount;

        public void Handle(in EventEnvelope envelope, in Died payload) => Sum += payload.EntityId;

        public void Handle(in EventEnvelope envelope, in Moved payload) => Sum += payload.XCm;
    }

    [Fact]
    public void Publish_and_dispatch_allocate_nothing_in_steady_state()
    {
        var bus = NewBus();
        var consumer = new NoOp();
        bus.Subscribe<Damaged>(consumer);
        bus.Subscribe<Died>(consumer);
        bus.Subscribe<Moved>(consumer);

        void Tick()
        {
            for (uint i = 0; i < 200; i++)
            {
                bus.Publish(new Damaged(i, i + 1, 1, 5, 10), actorCharacterId: i);
                bus.Publish(new Moved(i, 100, 200, 300));
                bus.Publish(new Died(i, 0));
            }

            bus.DispatchQueued();
        }

        // Warm up: grows the queues to their working size and JIT-compiles the paths.
        for (var i = 0; i < 20; i++)
            Tick();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 50; i++)
            Tick();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(consumer.Sum > 0);
    }
}
