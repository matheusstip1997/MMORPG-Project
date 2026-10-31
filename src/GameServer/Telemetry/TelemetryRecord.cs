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
    /// <summary>A committed equip or unequip changed an equipment slot (WP-M2-1).</summary>
    EquipmentChanged = 45,

    // Outposts and the economy (spec 4.2, 7.2 R-4.2-07; WP-M2-4): bus events.
    OutpostVisited = 50,
    ItemSold = 51,
    GoldChanged = 52,
    GoldDestroyed = 53,
    DemandUpdated = 54,
    PriceChanged = 55,
    /// <summary>A resource node type's harvest counter after a demand step (R-5.2-03, WP-M3-4).</summary>
    NodeDemandUpdated = 56,

    // Exp, level and rank (spec 3.6, 7.2; WP-M2-3).
    ExpGained = 60,
    LevelUp = 61,
    RankChanged = 62,
    // Free attribute points (spec 3.4 R-3.4-08, 7.2; WP-M2-13): v0 the attribute (-1 a respec reset), v1 the points, v2 unspent.
    AttributesChanged = 63,

    // Skill tree (spec 3.5, 7.2; WP-M2-5). Node and class ids are carried as SkillIdHash (FNV-1a 32 bit of the id).
    SkillLearned = 85,
    SkillEvolved = 86,
    Respecced = 87,
    ClassAdvanced = 88,
    // The Bystial book (spec 14.2, 7.2; WP-M2-8): v0 the monster net id, then tiers and the kill count; BookChanged v0 the
    // version, v1 the placed cards.
    CardEarned = 70,
    CardEvolved = 71,
    BookChanged = 72,
    // Jobs (spec 5.1, 7.2; WP-M3-1): the job as SkillIdHash of its JobDef id. JobXpGained v1 the activity (JobActivity), v2
    // the amount, v3 the repeats it was diminished by; JobLevelUp v1 and v2 the levels, v3 the points gained.
    JobChosen = 90,
    JobLevelUp = 91,
    JobXpGained = 92,
    // Harvesting (spec 5.2, 7.3; WP-M3-5): v0 the NodeDef net id, v1 the item net id, v2 the count, v3 the yield seed (rule 6;
    // an anti-bot input); the region is the node's.
    Harvested = 93,
    // Job skill trees (spec 5.1, 7.2; WP-M3-7; 94 is WP-M3-6's in merge order): the job as SkillIdHash of its JobDef id.
    // JobNodeLearned v1 the node (SkillIdHash), v2 the level reached, v3 the points spent; JobTreeRespec v1 the levels cleared,
    // v2 the points refunded.
    JobNodeLearned = 95,
    JobTreeRespec = 96,

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

    /// <summary>
    /// A world's economy snapshot (R-4.2-07), every demand control step: running totals of gold created and destroyed since
    /// the world started, the gold online characters hold now, and the active players.
    /// </summary>
    EconomyTotals = 214,

    /// <summary>The same snapshot by reason (subject: the <c>InventoryReason</c>): gold created, gold destroyed, items destroyed.</summary>
    EconomyByReason = 215,
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
