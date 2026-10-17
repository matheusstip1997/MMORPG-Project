namespace Shared.Data.Tables
{
    /// <summary>Limits of the in-process event bus (spec 1.9). Values in data/tables/EventBusLimits.json are placeholders.</summary>
    [Table("EventBusLimits", 1)]
    [Singleton]
    public sealed class EventBusLimits
    {
        /// <summary>
        /// Events one phase-11 dispatch delivers at most, including events that consumers publish while it runs.
        /// The rest wait for the next tick, so a publish loop between consumers cannot stall the world.
        /// </summary>
        [Range(1, 1_000_000)] public int MaxEventsPerDispatch { get; set; }

        /// <summary>Queue slots reserved per event type when it is first used, so a normal tick never grows a queue.</summary>
        [Range(1, 65_536)] public int InitialCapacityPerType { get; set; }
    }
}
