namespace GameServer.World;

/// <summary>
/// Receives spec 2.2 speed-check violations from tick phase 2 and lock facing clamps from phase 1 (M3 decision 15). Called on the simulation thread: implementations only
/// enqueue, never block and never allocate (architecture rule 10).
/// </summary>
public interface IMovementViolationSink
{
    /// <param name="distanceCm">How far the entity moved this tick, whole cm.</param>
    /// <param name="allowedCm">The most it may move per tick (maxSpeed plus tolerance), whole cm.</param>
    void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm);

    /// <summary>
    /// R-2.2-06, M3 decision 15: a locked frame's facing the server clamped toward the locked target, or (with
    /// <paramref name="noTarget"/>) used as sent because it knew no living locked target for it.
    /// </summary>
    /// <param name="sentFacing">The facing the frame carried (binary angle).</param>
    /// <param name="allowedFacing">The facing applied: the edge of the tolerance window, or the sent one with <paramref name="noTarget"/>.</param>
    void LockFacingClamp(uint serverTick, uint entityId, long accountId, ushort sentFacing, ushort allowedFacing, bool noTarget);
}

public sealed class NoMovementViolationSink : IMovementViolationSink
{
    public static readonly NoMovementViolationSink Instance = new();

    public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm)
    {
    }

    public void LockFacingClamp(uint serverTick, uint entityId, long accountId, ushort sentFacing, ushort allowedFacing, bool noTarget)
    {
    }
}
