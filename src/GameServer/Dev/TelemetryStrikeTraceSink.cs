using GameServer.Combat;
using GameServer.Telemetry;
using Shared.Simulation;
using Shared.Util;

namespace GameServer.Dev;

/// <summary>
/// The strike trace of the dev hitbox gizmos (spec 11.7) as telemetry: <see cref="TelemetryKind.StrikePlaced"/> and
/// <see cref="TelemetryKind.StrikeTested"/>, positions in raw Q24.8 units. World thread, phase 5: only enqueues, never
/// blocks or allocates (architecture rule 10); a full queue drops records.
/// </summary>
public sealed class TelemetryStrikeTraceSink(int worldId, TelemetryQueue queue, IClock clock) : IStrikeTraceSink
{
    public void StrikePlaced(uint tick, uint attackerId, int actionNetId, int effectIndex, FixVec2 origin, ushort facing) =>
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.StrikePlaced, clock.UtcNowMs, WorldId: worldId, Subject: attackerId,
            Target: ((long)actionNetId << 8) | (uint)(effectIndex & 0xFF), V0: tick, V1: origin.X, V2: origin.Y, V3: facing));

    public void TargetTested(uint tick, uint attackerId, uint targetId, FixVec2 position, int radiusRaw) =>
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.StrikeTested, clock.UtcNowMs, WorldId: worldId, Subject: attackerId,
            Target: targetId, V0: tick, V1: position.X, V2: position.Y, V3: radiusRaw));
}
