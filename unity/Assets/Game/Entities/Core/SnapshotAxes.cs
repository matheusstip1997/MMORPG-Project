#nullable enable
using Shared.Protocol;
using Shared.Simulation;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// Snapshot axes versus simulation axes (spec 1.7, decision log 2026-10-17): snapshots are Y-up, so the ground-plane
    /// X of <see cref="MovementSim"/> travels as snapshot X, the ground-plane Y (W = +Y) as snapshot Z, and the height as
    /// snapshot Y. Unity is Y-up too, so <c>WorldUnits</c> only scales snapshot coordinates.
    /// </summary>
    public static class SnapshotAxes
    {
        /// <summary>The server state of an entity as <see cref="MovementSim"/> holds it (whole cm, as snapshots carry it).</summary>
        public static MovementState GroundState(in EntitySnapshot entity) =>
            new MovementState(FixVec2.FromCm(entity.XCm, entity.ZCm), entity.Facing);

        public static int HeightCm(in EntitySnapshot entity) => entity.YCm;
    }
}
