namespace GameServer.Events;

/// <summary>
/// Idempotency helper for consumers (architecture rule 3): remembers the highest event ID handled and refuses
/// anything at or below it. Works because one bus delivers in ascending ID order, including events published during
/// dispatch. Keep one watermark per bus (event IDs are per world), shared by all event types the consumer handles.
/// </summary>
public struct EventWatermark
{
    public long LastEventId { get; private set; }

    /// <summary>True the first time an event ID above the watermark is seen; false for a redelivery.</summary>
    public bool TryAccept(in EventEnvelope envelope)
    {
        if (envelope.EventId <= LastEventId)
            return false;
        LastEventId = envelope.EventId;
        return true;
    }
}
