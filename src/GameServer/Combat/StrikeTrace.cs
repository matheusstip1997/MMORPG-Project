using Shared.Simulation;

namespace GameServer.Combat;

/// <summary>
/// Where <see cref="StrikeResolver"/> reports what it tests, for the dev hitbox gizmos (spec 11.7, backlog J): each strike
/// with its exact origin and locked facing, and each candidate target with the position it was tested at (rewound when
/// lag-compensated) and its body radius. Phase 5, world thread: an implementation only enqueues, never blocks or allocates.
/// The dev build's telemetry sink lives in <c>Dev/</c>; without one nothing is reported.
/// </summary>
public interface IStrikeTraceSink
{
    void StrikePlaced(uint tick, uint attackerId, int actionNetId, int effectIndex, FixVec2 origin, ushort facing);

    void TargetTested(uint tick, uint attackerId, uint targetId, FixVec2 position, int radiusRaw);
}
