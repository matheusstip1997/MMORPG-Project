namespace GameServer.World;

/// <summary>The tick phases, in the fixed order of spec 1.6. Inside a phase, entities go in id order.</summary>
public enum TickPhase : byte
{
    ApplyInputs = 1,
    Movement = 2,
    ActionStateMachines = 3,
    ProjectileMovement = 4,
    HitResolution = 5,
    StatusAndGauges = 6,
    AiAndSpawns = 7,
    DeathAndRespawn = 8,
    VisibilityAndInterest = 9,
    SnapshotsAndSends = 10,
    EventDispatch = 11,
    TelemetryFlush = 12,
}

/// <summary>
/// A step another module plugs into a tick phase without the world knowing its internals (spec 1.4: interfaces at
/// the seams). Runs on the simulation thread and must not block it.
/// </summary>
public interface ITickHook
{
    void Run(uint serverTick);
}

public sealed class NoTickHook : ITickHook
{
    public static readonly NoTickHook Instance = new();

    public void Run(uint serverTick)
    {
    }
}
