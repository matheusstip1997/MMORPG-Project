namespace GameServer.Events;

/// <summary>
/// What every bus event carries besides its payload (spec 1.9): event ID, UTC time, world, actor character ID, region.
/// The bus fills it on publish.
/// </summary>
/// <param name="EventId">
/// Counts up from 1 per world bus, in publish order, so delivery order is ID order. Unique within one world run;
/// telemetry adds the server run ID and world ID for global uniqueness.
/// </param>
/// <param name="UtcMs">Publish time from <c>IClock</c>, ms since the Unix epoch (architecture rule 6).</param>
/// <param name="WorldId">The world (main map or instance) whose tick published the event.</param>
/// <param name="ActorCharacterId">The character that caused the event; 0 when the actor is not a character (a monster).</param>
/// <param name="RegionId">The actor's region; 0 when there is none.</param>
public readonly record struct EventEnvelope(long EventId, long UtcMs, int WorldId, long ActorCharacterId, int RegionId);

/// <summary>Marks a bus event payload: a small immutable struct (spec 1.9). The envelope travels next to it.</summary>
public interface IGameEvent
{
}

/// <summary>
/// Receives one event type in phase 11, on the world thread. Consumers must be idempotent (architecture rule 3):
/// the same event ID may be delivered again, see <see cref="EventWatermark"/>. They must not block or do I/O.
/// </summary>
public interface IEventConsumer<T> where T : struct, IGameEvent
{
    void Handle(in EventEnvelope envelope, in T payload);
}
