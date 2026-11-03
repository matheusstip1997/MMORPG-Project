namespace GameServer.World;

/// <summary>
/// Receives spec 2.2 speed-check violations from tick phase 2. Called on the simulation thread: implementations only
/// enqueue, never block and never allocate (architecture rule 10).
/// </summary>
public interface IMovementViolationSink
{
    /// <param name="distanceCm">How far the entity moved this tick, whole cm.</param>
    /// <param name="allowedCm">The most it may move per tick (maxSpeed plus tolerance), whole cm.</param>
    void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm);
}

public sealed class NoMovementViolationSink : IMovementViolationSink
{
    public static readonly NoMovementViolationSink Instance = new();

    public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm)
    {
    }
}
