using Shared.Data.Tables;

namespace GameServer.World;

/// <summary>The config values one world reads, taken from the loaded tables (architecture rule 2: no magic numbers).</summary>
public sealed record WorldSettings(
    int TickRateHz,
    int SnapshotRateHz,
    int MoveSpeedCmPerSec,
    int InterestRadiusCm,
    int GridCellCm,
    string PlayerSpawnMarkerId,
    int SpeedTolerancePercent,
    int MaxQueuedInputFrames,
    int MaxSnapshotBytes,
    int InputBacklogTicks = 1,
    int MaxInputFramesPerTick = 1,
    int MoveKeyHysteresisDeg = 0)
{
    public static WorldSettings From(StartingValuesM1 values, ServerLoop loop, NetSecurity net) => new(
        values.TickRateHz,
        values.SnapshotRateHz,
        values.MoveSpeedCmPerSec,
        values.InterestRadiusCm,
        values.GridCellCm,
        loop.PlayerSpawnMarkerId,
        loop.SpeedTolerancePercent,
        loop.MaxQueuedInputFrames,
        // A snapshot is one unreliable message; the same cap that bounds what clients send bounds what we send.
        net.MaxUnreliableMessageBytes,
        loop.InputBacklogTicks,
        loop.MaxInputFramesPerTick,
        values.MoveKeyHysteresisDeg);

    /// <summary>Snapshots go out every this many ticks; the validator makes the rates divide evenly.</summary>
    public int SnapshotIntervalTicks => TickRateHz / SnapshotRateHz;

    /// <summary>R-2.2-01 (M3 decision 14): the move keys' sector hysteresis as a binary angle, for <c>MovementSim.Step</c>.</summary>
    public ushort KeyHysteresis => Shared.Simulation.MoveSteering.HysteresisFrom(MoveKeyHysteresisDeg);
}
