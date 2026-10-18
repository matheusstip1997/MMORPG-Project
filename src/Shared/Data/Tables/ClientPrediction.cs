namespace Shared.Data.Tables
{
    /// <summary>
    /// Client prediction, reconciliation and interpolation settings (spec 2.2, backlog D1). Client-only: the server
    /// never reads them. Values in data/tables/ClientPrediction.json are placeholders to tune during Milestone 1.
    /// The interpolation delay itself is <c>StartingValuesM1.InterpolationDelayMs</c> (spec 12.7).
    /// </summary>
    [Table("ClientPrediction", 1)]
    [Singleton]
    public sealed class ClientPrediction : IValidatableRow
    {
        /// <summary>
        /// Spec 2.2: the client corrects its own entity only when the position it predicted for the acknowledged input
        /// differs from the server's by more than this. At least 2 cm: snapshots round each axis to whole cm, and a
        /// rebased prediction starts from such a rounded position, so up to 1 cm per axis (1.41 cm) is only rounding.
        /// </summary>
        [Range(2, 1_000)] public int CorrectionToleranceCm { get; set; }

        /// <summary>A correction larger than this snaps the visual at once instead of smoothing it (teleports, respawns).</summary>
        [Range(1, 100_000)] public int CorrectionSnapCm { get; set; }

        /// <summary>Time constant of the visual smoothing of a correction; 0 snaps every correction.</summary>
        [Range(0, 5_000)] public int CorrectionSmoothingMs { get; set; }

        /// <summary>
        /// Unacknowledged input frames kept for replay. When the server stops acknowledging (stall), the oldest are
        /// dropped and the next snapshot corrects the client.
        /// </summary>
        [Range(1, 1_024)] public int MaxPendingInputs { get; set; }

        /// <summary>
        /// Ticks the client runs in one rendered frame to catch up after a hitch; the rest of the time is dropped.
        /// Keep it at or below <c>ServerLoop.MaxQueuedInputFrames</c>, or the server drops the burst's oldest frames.
        /// </summary>
        [Range(1, 64)] public int MaxTicksPerFrame { get; set; }

        /// <summary>
        /// Time constant with which the remote-entity render clock drifts back to its target (server time minus the
        /// interpolation delay). An error larger than the interpolation delay snaps the clock instead.
        /// </summary>
        [Range(1, 10_000)] public int InterpolationClockCatchUpMs { get; set; }

        public void Validate(RowErrors errors)
        {
            if (CorrectionToleranceCm >= CorrectionSnapCm)
                errors.Add(nameof(CorrectionToleranceCm), "must be less than CorrectionSnapCm");
        }
    }
}
