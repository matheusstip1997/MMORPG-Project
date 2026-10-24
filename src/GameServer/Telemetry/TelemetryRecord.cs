namespace GameServer.Telemetry;

/// <summary>
/// Record kinds of telemetry schema v1 (spec 7.2). Stored in files: append only, never renumber or rename;
/// a breaking change bumps <see cref="TelemetrySchema.Version"/>.
/// </summary>
public enum TelemetryKind : ushort
{
    SessionStarted = 1,
    SessionEnded = 2,
    Moved = 3,

    ActionStarted = 10,
    HitLanded = 11,
    Damaged = 12,
    Killed = 13,
    Died = 14,
    Staggered = 15,
    ParryAttempted = 16,
    Flinched = 17,
    Respawned = 18,
    EffectApplied = 19,
    EffectRemoved = 20,
    EffectCleansed = 21,
    Healed = 22,

    /// <summary>A transport security event (R-2.5-06).</summary>
    Security = 100,

    /// <summary>Written by the writer itself when the queue dropped records since its last report.</summary>
    QueueDropped = 200,

    // Server health (not bus events): written by the host and its threads, never by gameplay.

    /// <summary>A world's tick timing over the last stats interval (phase 12, architecture rule 12).</summary>
    TickStats = 201,

    /// <summary>A world's running totals of refused or degraded traffic and bus failures.</summary>
    WorldCounters = 202,

    /// <summary>A world's simulation thread stopped on an exception.</summary>
    WorldCrashed = 203,

    /// <summary>A character save threw; whether it committed is unknown and the scheduler retries it.</summary>
    SaveFailed = 204,

    /// <summary>An entity moved further in one tick than maxSpeed plus the tolerance allows (spec 2.2 speed check).</summary>
    SpeedViolation = 205,

    /// <summary>The seed and stream of a world's simulation generator (architecture rule 6: logged seeds).</summary>
    WorldRng = 206,
}

/// <summary>
/// One telemetry record, schema v1 (spec 7.2: one compact versioned schema). A fixed-size struct so producers never
/// allocate: the envelope fields, two entity references, and four generic values whose meaning depends on
/// <see cref="Kind"/> (see <see cref="TelemetrySchema"/> for the names written to files). Unused fields are 0.
/// </summary>
/// <param name="EventId">Bus event ID (unique per world run), or 0 for records that do not come from the bus.</param>
/// <param name="CharacterId">The acting character (the bus envelope's actor), or 0.</param>
public readonly record struct TelemetryRecord(
    TelemetryKind Kind,
    long UtcMs,
    long EventId = 0,
    int WorldId = 0,
    int RegionId = 0,
    long CharacterId = 0,
    long AccountId = 0,
    long Subject = 0,
    long Target = 0,
    long V0 = 0,
    long V1 = 0,
    long V2 = 0,
    long V3 = 0);
