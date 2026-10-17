using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Events;

/// <summary>
/// In-process publish/subscribe for one world (spec 1.9, architecture rule 3). Producers publish facts during tick
/// phases 1-10; <see cref="DispatchQueued"/> delivers them in phase 11, so consumers never interrupt combat resolution.
/// </summary>
/// <remarks>
/// <para>
/// Order: events are delivered in publish order, which is also event-ID order. Producers already run in phase order
/// and in entity-ID order inside a phase (spec 1.6), so the delivery order is deterministic and keeps causes before
/// effects (Damaged before Killed). Consumers of one event type run in subscription order. Events published by a
/// consumer during dispatch are delivered in the same dispatch, after everything queued before them, up to
/// <see cref="EventBusLimits.MaxEventsPerDispatch"/>; the rest wait for the next tick.
/// </para>
/// <para>
/// Threading: a bus belongs to its world's simulation thread and is not thread-safe. Other threads (network, persistence
/// completions) hand their facts to the world thread first.
/// </para>
/// <para>
/// Allocations: each event type keeps a queue of (envelope, payload) structs and the bus keeps one queue of type indexes
/// in publish order. Queues grow when needed and never shrink, so in steady state publishing and dispatching allocate
/// nothing; payloads are structs and are delivered by generic interface calls, without boxing.
/// </para>
/// </remarks>
public sealed class EventBus
{
    private static int s_nextTypeIndex = -1;

    private readonly IClock _clock;
    private readonly int _maxEventsPerDispatch;
    private readonly int _initialCapacityPerType;
    private readonly Action<EventEnvelope, Exception>? _onConsumerError;
    private readonly Queue<int> _order;
    private Channel?[] _channels = new Channel?[16];
    private long _lastEventId;
    private bool _dispatching;

    /// <param name="onConsumerError">
    /// Called on the world thread when a consumer throws. The failure is counted and dispatch continues with the next
    /// consumer, so one broken consumer cannot stop the others or the tick.
    /// </param>
    public EventBus(int worldId, IClock clock, EventBusLimits limits, Action<EventEnvelope, Exception>? onConsumerError = null)
    {
        WorldId = worldId;
        _clock = clock;
        _maxEventsPerDispatch = limits.MaxEventsPerDispatch;
        _initialCapacityPerType = limits.InitialCapacityPerType;
        _onConsumerError = onConsumerError;
        _order = new Queue<int>(limits.InitialCapacityPerType);
    }

    public int WorldId { get; }

    /// <summary>Events published and not yet delivered.</summary>
    public int PendingCount => _order.Count;

    /// <summary>ID of the last event published; 0 before the first.</summary>
    public long LastEventId => _lastEventId;

    /// <summary>Sum, over all dispatches, of the events still pending when a dispatch stopped at its cap.</summary>
    public long CarriedOverTotal { get; private set; }

    /// <summary>Consumer calls that threw.</summary>
    public long ConsumerFailures { get; private set; }

    /// <summary>Adds a consumer for one event type. Subscribe at startup; a consumer added during dispatch sees later events.</summary>
    public void Subscribe<T>(IEventConsumer<T> consumer) where T : struct, IGameEvent =>
        GetChannel<T>().Consumers.Add(consumer);

    /// <summary>Queues an event for phase 11 and returns its envelope. Call from the world thread only.</summary>
    /// <param name="actorCharacterId">The character that caused the event; 0 for monsters and the world.</param>
    /// <param name="regionId">The actor's region; 0 when there is none.</param>
    public EventEnvelope Publish<T>(in T payload, long actorCharacterId = 0, int regionId = 0) where T : struct, IGameEvent
    {
        var channel = GetChannel<T>();
        var envelope = new EventEnvelope(++_lastEventId, _clock.UtcNowMs, WorldId, actorCharacterId, regionId);
        channel.Pending.Enqueue(new Queued<T>(envelope, payload));
        _order.Enqueue(channel.Index);
        return envelope;
    }

    /// <summary>
    /// Tick phase 11: delivers queued events in publish order, including events published while it runs, up to the
    /// dispatch cap. Returns the number of events delivered.
    /// </summary>
    public int DispatchQueued()
    {
        if (_dispatching)
            throw new InvalidOperationException("DispatchQueued is not re-entrant; a consumer should publish instead.");

        _dispatching = true;
        var delivered = 0;
        try
        {
            while (delivered < _maxEventsPerDispatch && _order.TryDequeue(out var index))
            {
                _channels[index]!.DeliverNext(this);
                delivered++;
            }
        }
        finally
        {
            _dispatching = false;
        }

        CarriedOverTotal += _order.Count;
        return delivered;
    }

    private void ReportFailure(in EventEnvelope envelope, Exception exception)
    {
        ConsumerFailures++;
        _onConsumerError?.Invoke(envelope, exception);
    }

    private Channel<T> GetChannel<T>() where T : struct, IGameEvent
    {
        var index = TypeIndex<T>.Value;
        if (index < _channels.Length && _channels[index] is Channel<T> channel)
            return channel;
        return CreateChannel<T>(index);
    }

    private Channel<T> CreateChannel<T>(int index) where T : struct, IGameEvent
    {
        if (index >= _channels.Length)
            Array.Resize(ref _channels, Math.Max(index + 1, _channels.Length * 2));
        var channel = new Channel<T>(index, _initialCapacityPerType);
        _channels[index] = channel;
        return channel;
    }

    /// <summary>A process-wide index per event type, so a bus finds a type's queue with one array read.</summary>
    private static class TypeIndex<T>
    {
        public static readonly int Value = Interlocked.Increment(ref s_nextTypeIndex);
    }

    private readonly record struct Queued<T>(EventEnvelope Envelope, T Payload);

    private abstract class Channel(int index)
    {
        public int Index { get; } = index;

        public abstract void DeliverNext(EventBus bus);
    }

    private sealed class Channel<T>(int index, int initialCapacity) : Channel(index) where T : struct, IGameEvent
    {
        public Queue<Queued<T>> Pending { get; } = new(initialCapacity);

        public List<IEventConsumer<T>> Consumers { get; } = [];

        public override void DeliverNext(EventBus bus)
        {
            var item = Pending.Dequeue();
            var consumers = Consumers;
            // Index loop over the count at the start: a consumer subscribed by a consumer of this event neither breaks
            // an enumerator nor receives this event, only later ones.
            var count = consumers.Count;
            for (var i = 0; i < count; i++)
            {
                try
                {
                    consumers[i].Handle(item.Envelope, item.Payload);
                }
                catch (Exception exception)
                {
                    bus.ReportFailure(item.Envelope, exception);
                }
            }
        }
    }
}
