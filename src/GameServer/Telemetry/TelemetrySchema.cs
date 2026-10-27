using System.Text.Json;

namespace GameServer.Telemetry;

/// <summary>
/// Telemetry schema v1 (spec 7.2). Names each kind and the payload fields it uses; the JSON lines format writes only
/// those fields, under these names. Changing a name or a field's meaning is a breaking change: bump <see cref="Version"/>.
/// Adding a kind or naming a field that was unused is not.
/// </summary>
public static class TelemetrySchema
{
    public const string Name = "mmorpg.telemetry";
    public const int Version = 1;

    /// <summary>Envelope keys, written for every record (zeros are omitted except kind and time).</summary>
    public const string KindKey = "k";
    public const string TimeKey = "t";
    public const string EventIdKey = "e";
    public const string WorldKey = "w";
    public const string RegionKey = "r";
    public const string CharacterKey = "c";
    public const string AccountKey = "a";

    public static readonly IReadOnlyList<KindLayout> Kinds =
    [
        new(TelemetryKind.SessionStarted, "SessionStarted", Subject: "entity", V0: "reattached"),
        new(TelemetryKind.SessionEnded, "SessionEnded", Subject: "entity", V0: "reason"),
        new(TelemetryKind.Moved, "Moved", Subject: "entity", V0: "x", V1: "y", V2: "facing"),
        new(TelemetryKind.ActionStarted, "ActionStarted", Subject: "entity", V0: "action", V1: "startTick", V2: "facing", V3: "color"),
        new(TelemetryKind.HitLanded, "HitLanded", Subject: "attacker", Target: "target", V0: "action"),
        new(TelemetryKind.Damaged, "Damaged", Subject: "source", Target: "target", V0: "action", V1: "amount", V2: "hpAfter", V3: "crit"),
        new(TelemetryKind.Killed, "Killed", Subject: "killer", Target: "victim", V0: "victimCharacter", V1: "victimDef", V2: "x", V3: "y"),
        new(TelemetryKind.Died, "Died", Subject: "entity", Target: "killer"),
        new(TelemetryKind.Staggered, "Staggered", Subject: "entity", Target: "source"),
        new(TelemetryKind.Flinched, "Flinched", Subject: "entity", Target: "source", V0: "durationMs"),
        new(TelemetryKind.ParryAttempted, "ParryAttempted", Subject: "defender", Target: "attacker", V0: "action", V1: "color", V2: "result"),
        new(TelemetryKind.Respawned, "Respawned", Subject: "entity"),
        new(TelemetryKind.EffectApplied, "EffectApplied", Subject: "target", Target: "source", V0: "status", V1: "stacks", V2: "endTick", V3: "refreshed"),
        new(TelemetryKind.EffectRemoved, "EffectRemoved", Subject: "target", V0: "status", V1: "reason"),
        new(TelemetryKind.EffectCleansed, "EffectCleansed", Subject: "target", Target: "source", V0: "status", V1: "action"),
        new(TelemetryKind.Healed, "Healed", Subject: "source", Target: "target", V0: "action", V1: "status", V2: "amount", V3: "hpAfter"),
        new(TelemetryKind.BuildupTriggered, "BuildupTriggered", Subject: "target", Target: "source", V0: "buildUp", V1: "status"),
        new(TelemetryKind.ItemCreated, "ItemCreated", Subject: "item", V0: "rarity", V1: "count", V2: "reason"),
        new(TelemetryKind.ItemDestroyed, "ItemDestroyed", Subject: "item", V0: "rarity", V1: "count", V2: "reason"),
        new(TelemetryKind.LootDropped, "LootDropped", Subject: "drop", Target: "killEvent", V0: "item", V1: "count", V2: "monsterDef", V3: "owner"),
        new(TelemetryKind.LootPickedUp, "LootPickedUp", Subject: "drop", V0: "item", V1: "count", V2: "countLeft"),
        new(TelemetryKind.LootDropExpired, "LootDropExpired", Subject: "drop", V0: "item", V1: "count"),
        new(TelemetryKind.EquipmentChanged, "EquipmentChanged", Subject: "character", V0: "slot"),
        new(TelemetryKind.Security, "Security", Subject: "peer", V0: "event", V1: "rejectReason", V2: "ipFamily", V3: "ipPrefix"),
        new(TelemetryKind.QueueDropped, "QueueDropped", V0: "dropped", V1: "droppedTotal"),
        new(TelemetryKind.TickStats, "TickStats", V0: "lastUs", V1: "maxUs", V2: "overruns", V3: "maxLateUs"),
        new(TelemetryKind.WorldCounters, "WorldCounters",
            V0: "malformedInputs", V1: "staleFrames", V2: "snapshotsTruncated", V3: "consumerFailures"),
        new(TelemetryKind.WorldCrashed, "WorldCrashed", V0: "tick"),
        new(TelemetryKind.SaveFailed, "SaveFailed", V0: "reason"),
        new(TelemetryKind.SpeedViolation, "SpeedViolation", Subject: "entity", V0: "distanceCm", V1: "allowedCm", V2: "tick"),
        new(TelemetryKind.WorldRng, "WorldRng", V0: "seed", V1: "stream"),
        new(TelemetryKind.ChatCounters, "ChatCounters", V0: "accepted", V1: "deliveries", V2: "refused", V3: "rateLimited"),
        new(TelemetryKind.RequestCounters, "RequestCounters", V0: "malformed", V1: "chatLinesDropped"),
        new(TelemetryKind.SendCounters, "SendCounters", Subject: "lastPeer", V0: "unreliableTooLarge", V1: "lastBytes", V2: "lastLimitBytes"),
        new(TelemetryKind.TickTiming, "TickTiming", V0: "tick", V1: "tickUs", V2: "lateUs"),
        new(TelemetryKind.ConfigReload, "ConfigReload", Subject: "tables", V0: "tick", V1: "result", V2: "hash", V3: "errors"),
        // Positions in raw Q24.8 units (256 per cm); actionEffect = action netId * 256 + effect index (255: not from an action).
        new(TelemetryKind.StrikePlaced, "StrikePlaced", Subject: "attacker", Target: "actionEffect", V0: "tick", V1: "x", V2: "y", V3: "facing"),
        new(TelemetryKind.StrikeTested, "StrikeTested", Subject: "attacker", Target: "target", V0: "tick", V1: "x", V2: "y", V3: "radius"),
    ];

    private static readonly Dictionary<TelemetryKind, KindLayout> ByKind = Kinds.ToDictionary(k => k.Kind);
    private static readonly Dictionary<string, KindLayout> ByName = Kinds.ToDictionary(k => k.Name, StringComparer.Ordinal);

    public static bool TryGet(TelemetryKind kind, out KindLayout layout) => ByKind.TryGetValue(kind, out layout!);

    public static bool TryGet(string name, out KindLayout layout) => ByName.TryGetValue(name, out layout!);
}

/// <summary>
/// The file names of one kind's payload fields; null means the field is unused and not written.
/// Encoded names are kept so the writer allocates nothing per record.
/// </summary>
public sealed record KindLayout(
    TelemetryKind Kind,
    string Name,
    string? Subject = null,
    string? Target = null,
    string? V0 = null,
    string? V1 = null,
    string? V2 = null,
    string? V3 = null)
{
    internal JsonEncodedText EncodedName { get; } = JsonEncodedText.Encode(Name);

    /// <summary>The six payload fields in record order: Subject, Target, V0-V3.</summary>
    internal (string? Name, JsonEncodedText Encoded)[] Fields { get; } =
        new[] { Subject, Target, V0, V1, V2, V3 }
            .Select(name => (name, name == null ? default : JsonEncodedText.Encode(name)))
            .ToArray();
}
