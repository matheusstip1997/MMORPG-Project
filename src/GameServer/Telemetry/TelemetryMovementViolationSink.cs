using GameServer.World;
using Shared.Util;

namespace GameServer.Telemetry;

/// <summary>Records spec 2.2 speed-check violations as <see cref="TelemetryKind.SpeedViolation"/> (world thread, never blocks).</summary>
public sealed class TelemetryMovementViolationSink(int worldId, TelemetryQueue queue, IClock clock) : IMovementViolationSink
{
    public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm)
    {
        queue.TryEnqueue(new TelemetryRecord(TelemetryKind.SpeedViolation, clock.UtcNowMs, WorldId: worldId,
            AccountId: accountId, Subject: entityId, V0: distanceCm, V1: allowedCm, V2: serverTick));
    }
}
