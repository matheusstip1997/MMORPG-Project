namespace GameServer.Persistence;

/// <summary>Which kinds of unsaved change a character has (R-7.1-01).</summary>
[Flags]
public enum DirtyFlags : byte
{
    None = 0,
    /// <summary>Class 2: single-player gains (loot, exp, craft results, ...), saved in batches.</summary>
    Class2 = 1,
    /// <summary>Volatile state (position, HP, MP, stamina, cooldowns, effects).</summary>
    Volatile = 2,
}

/// <summary>Why a save was issued; carried through for telemetry and tests.</summary>
public enum SaveReason : byte
{
    Class2Batch,
    VolatilePeriodic,
    Logout,
    ZoneChange,
    Death,
    LevelUp,
    /// <summary>Ordering barrier before a class 1 operation (R-7.1-02).</summary>
    Barrier,
}

/// <summary>One character save handed from the world thread to a persistence worker.</summary>
public readonly record struct SaveJob(long CharacterId, CharacterState State, long ExpectedVersion, Guid SaveId, SaveReason Reason);

public enum SaveOutcome : byte
{
    Saved,
    VersionConflict,
    NotFound,
    Deleted,
    /// <summary>The database could not be reached or the call threw; whether it committed is unknown.</summary>
    Failed,
}

/// <summary>The result of a <see cref="SaveJob"/>, returned to the world thread through the completion queue (spec 1.6).</summary>
public readonly record struct SaveCompletion(long CharacterId, Guid SaveId, SaveOutcome Outcome, long Version, Exception? Error = null)
{
    public static SaveCompletion From(in SaveJob job, SaveResult result) => new(job.CharacterId, job.SaveId, result.Status switch
    {
        SaveStatus.Saved => SaveOutcome.Saved,
        SaveStatus.VersionConflict => SaveOutcome.VersionConflict,
        SaveStatus.NotFound => SaveOutcome.NotFound,
        SaveStatus.Deleted => SaveOutcome.Deleted,
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    }, result.Version);
}

/// <summary>Where the scheduler sends save jobs. Must never block the calling (tick) thread.</summary>
public interface ISaveJobSink
{
    void Enqueue(in SaveJob job);
}

/// <summary>Copies a character's saved state out of the world. Called on the world thread.</summary>
public interface ICharacterStateSource
{
    CharacterState Capture(long characterId);
}
