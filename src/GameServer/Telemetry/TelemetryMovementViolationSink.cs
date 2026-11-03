using GameServer.World;
using Shared.Util;

namespace GameServer.Telemetry;

/// <summary>
/// Records spec 2.2 speed-check violations as <see cref="TelemetryKind.SpeedViolation"/> and lock facing clamps as
/// <see cref="TelemetryKind.LockFacingClamp"/> (M3 decision 15; world thread, never blocks).
/// </summary>
public sealed class TelemetryMovementViolationSink(int worldId, TelemetryQueue queue, IClock clock) : IMovementViolationSink
{
    public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm)
    {
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.SpeedViolation, clock.UtcNowMs, WorldId: worldId,
            AccountId: accountId, Subject: entityId, V0: distanceCm, V1: allowedCm, V2: serverTick));
    }

    public void LockFacingClamp(uint serverTick, uint entityId, long accountId, ushort sentFacing, ushort allowedFacing, bool noTarget)
    {
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.LockFacingClamp, clock.UtcNowMs, WorldId: worldId,
            AccountId: accountId, Subject: entityId, V0: serverTick, V1: sentFacing, V2: allowedFacing, V3: noTarget ? 1 : 0));
    }
}
