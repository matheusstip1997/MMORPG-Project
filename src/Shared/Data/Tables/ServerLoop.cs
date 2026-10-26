namespace Shared.Data.Tables
{
    /// <summary>
    /// Server world loop settings (spec 1.6, 2.2, 2.3; backlog C1, C3). Values in data/tables/ServerLoop.json are
    /// placeholders to tune during Milestone 1.
    /// </summary>
    [Table("ServerLoop", 2)]
    [Singleton]
    public sealed class ServerLoop : IValidatableRow
    {
        /// <summary>
        /// Where new players appear: the id of a <c>playerSpawn</c> marker exported by UnityExporter into the world's
        /// markers.json (backlog C2). The world loader and DataValidator --world check that it exists.
        /// TODO(H1): a returning character spawns at its saved position instead.
        /// </summary>
        public string PlayerSpawnMarkerId { get; set; } = string.Empty;

        /// <summary>
        /// Input frames buffered per session (spec 1.6 InputQueue). The world applies one frame per tick; when the
        /// queue is full the oldest frame is dropped, so a backed-up client adds at most this many ticks of latency.
        /// </summary>
        [Range(1, 64)] public int MaxQueuedInputFrames { get; set; }

        /// <summary>How long the network thread waits between transport polls; adds up to this much input latency.</summary>
        [Range(1, 50)] public int NetPollIntervalMs { get; set; }

        /// <summary>
        /// Spec 2.2 speed check: a tick may move up to maxSpeed per tick plus this many percent before it counts as a
        /// violation (telemetry <c>SpeedViolation</c>).
        /// </summary>
        [Range(0, 100)] public int SpeedTolerancePercent { get; set; }

        /// <summary>
        /// Reliable requests (chat lines today) one peer may have waiting for the world at once (R-2.5-03 reliable
        /// backlog). The network thread drops and counts any request past it, so a flooding client cannot grow the
        /// world's inbox.
        /// </summary>
        [Range(1, 256)] public int MaxPendingRequestsPerPeer { get; set; }

        public void Validate(RowErrors errors)
        {
            if (string.IsNullOrWhiteSpace(PlayerSpawnMarkerId))
                errors.Add(nameof(PlayerSpawnMarkerId), "must name a playerSpawn marker of the world");
        }
    }
}
