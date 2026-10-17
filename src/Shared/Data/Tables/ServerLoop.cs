namespace Shared.Data.Tables
{
    /// <summary>
    /// Server world loop settings (spec 1.6, 2.2, 2.3; backlog C1). Values in data/tables/ServerLoop.json are
    /// placeholders to tune during Milestone 1.
    /// </summary>
    [Table("ServerLoop", 1)]
    [Singleton]
    public sealed class ServerLoop
    {
        /// <summary>
        /// Dev spawn point for every new player, in cm: X and Y on the ground plane (the
        /// MovementSim axes, +Y = screen-up) and the height (the vertical axis of snapshots). Replaced by spawn markers
        /// from UnityExporter (backlog C2) and the saved position from persistence (backlog H1).
        /// </summary>
        [Range(-8_000_000, 8_000_000)] public int DevSpawnXCm { get; set; }
        [Range(-8_000_000, 8_000_000)] public int DevSpawnYCm { get; set; }
        [Range(-100_000, 100_000)] public int DevSpawnHeightCm { get; set; }

        /// <summary>
        /// Input frames buffered per session (spec 1.6 InputQueue). The world applies one frame per tick; when the
        /// queue is full the oldest frame is dropped, so a backed-up client adds at most this many ticks of latency.
        /// </summary>
        [Range(1, 64)] public int MaxQueuedInputFrames { get; set; }

        /// <summary>How long the network thread waits between transport polls; adds up to this much input latency.</summary>
        [Range(1, 50)] public int NetPollIntervalMs { get; set; }
    }
}
