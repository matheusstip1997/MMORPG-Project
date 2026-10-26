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
    BuildupTriggered = 23,

    // Items and loot (backlog G, R-7.2-02 "drops").
    ItemCreated = 40,
    ItemDestroyed = 41,
    LootDropped = 42,
    LootPickedUp = 43,
    LootDropExpired = 44,

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

    /// <summary>A world's running totals of local chat (R-6.5-01): accepted, delivered, refused, rate limited. Never the text.</summary>
    ChatCounters = 207,

    /// <summary>A world's running totals of client requests on the reliable channel that never reached a system.</summary>
    RequestCounters = 208,

    /// <summary>A world's running totals of server messages the transport refused to send: unreliable ones over a peer's datagram limit (R-2.5-03).</summary>
    SendCounters = 209,

    /// <summary>
    /// One tick's duration and start lateness (backlog J per-tick timing log), every tick while
    /// <c>TelemetrySettings.TickTimingLog</c> is on: the source of the load test's tick p99 (spec 11.5, 12.4).
    /// </summary>
    TickTiming = 210,

    // Dev tools (spec 11.7, backlog J and A): written only by a DevTools build with MMORPG_DEV_TOOLS on.

    /// <summary>
    /// A config hot reload ended: accepted (swapped at the start of <c>tick</c>), rejected, or only tables the server does
    /// not read changed. The dev client applies an accepted reload when its files have the same <c>hash</c>.
    /// </summary>
    ConfigReload = 211,

    /// <summary>A strike placed in phase 3 and tested in phase 5: its exact origin and locked facing (hitbox gizmos).</summary>
    StrikePlaced = 212,

    /// <summary>A target a strike was tested against: the position actually tested (rewound when lag-compensated) and its radius.</summary>
    StrikeTested = 213,
}

/// <summary>The <c>result</c> of a <see cref="TelemetryKind.ConfigReload"/> record.</summary>
public enum ConfigReloadResult
{
    Accepted = 1,
    Rejected = 2,
    /// <summary>Only tables the GameServer does not read changed; nothing was swapped.</summary>
    NotServer = 3,
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
