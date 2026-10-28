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

/// <summary>
/// Why a save was issued; carried through for telemetry and tests. Stored in telemetry (<c>SaveFailed</c>): append only,
/// never renumber.
/// </summary>
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
    /// <summary>
    /// A class 1 operation (R-7.1-01: outpost sale, vendor purchase) waits for this save, which carries it together with
    /// any pending class 2 changes in one transaction (the ordering barrier by inclusion, R-7.1-02).
    /// </summary>
    Class1,
    /// <summary>A rank-up at the outpost (R-3.6-04, WP-M2-3): saved at once and confirmed only once committed (rule 5).</summary>
    RankUp,
    /// <summary>A skill-tree operation (learn, upgrade, evolve, respec, class advance; WP-M2-5): saved at once, confirmed once committed.</summary>
    SkillTree,
}

/// <summary>
/// One character save handed from the world thread to a persistence worker, with the inventory and progression changes it
/// carries (null when there are none). A retry resends the identical job, batch included.
/// </summary>
public readonly record struct SaveJob(long CharacterId, CharacterState State, long ExpectedVersion, Guid SaveId, SaveReason Reason,
    InventoryBatch? Inventory = null, ProgressionBatch? Progression = null, SkillBatch? Skills = null, BystialBatch? Bystial = null);

public enum SaveOutcome : byte
{
    Saved,
    VersionConflict,
    NotFound,
    Deleted,
    /// <summary>The database could not be reached or the call threw; whether it committed is unknown.</summary>
    Failed,
    /// <summary>An inventory operation of the batch was applied before (<see cref="SaveStatus.DuplicateOperation"/>).</summary>
    DuplicateOperation,
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
        SaveStatus.DuplicateOperation => SaveOutcome.DuplicateOperation,
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
