using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using Shared.Data.Tables;

namespace GameServer.Progression;

/// <summary>
/// Per-character achievement counters (R-3.6-05; spec 3.6 <c>AchievementService</c>, WP-M2-3), idempotent per event. From
/// <see cref="Killed"/> (phase 11, its own <see cref="EventWatermark"/>): the killer's monster kills, the kill count of the
/// monster's type (R-14.2-01: the counters the Bystial book reuses, by <c>MonsterDef.id</c>) and boss kills. Monster
/// types killed is the number of types with a kill. Outposts visited from <see cref="OutpostVisited"/> (WP-M2-4, its own
/// watermark), distinct by the outpost's npc marker id (<see cref="RecordOutpostVisit"/>). Runs claimed come through <see cref="Record"/>
/// (WP-M2-6: the claim records it in the call that pays the exp, keyed by run id, so both ride one save). Resources harvested and
/// items crafted from <see cref="Harvested"/> and <see cref="Crafted"/> (WP-M3-1, R-3.6-05; one per operation key). TODO(open): <c>AchievementDone</c> (spec 8.1) once discrete achievements exist; v0 has counters only.
/// </summary>
public sealed class AchievementService : IEventConsumer<Killed>, IEventConsumer<OutpostVisited>, IEventConsumer<Harvested>,
    IEventConsumer<Crafted>
{
    private readonly ProgressionStore _store;
    private readonly Func<CombatRules?> _combat;
    private EventWatermark _watermark;
    private EventWatermark _visitWatermark;
    private EventWatermark _harvestWatermark;
    private EventWatermark _craftWatermark;

    public AchievementService(ProgressionStore store, Func<CombatRules?> combat)
    {
        _store = store;
        _combat = combat;
    }

    /// <summary>The NPC of an entity id, null for anything else; set by the world with the economy tables.</summary>
    public Func<uint, Npc?>? FindNpc { get; set; }

    /// <summary>
    /// The operation keys of activity events seen lately (WP-M3-1, set by the world with the job tables), so a repeated
    /// <c>Harvested</c> or <c>Crafted</c> counts once; without it only the bus watermark applies.
    /// </summary>
    public RecentOpKeys? ActivityOps { get; set; }

    public void Subscribe(EventBus events)
    {
        events.Subscribe<Killed>(this);
        events.Subscribe<OutpostVisited>(this);
        events.Subscribe<Harvested>(this);
        events.Subscribe<Crafted>(this);
    }

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill counts once
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.VictimCharacterId != 0 || !_store.IsAttached(characterId))
            return; // monster kills only
        if (_combat() is not { } combat || !combat.TryGetMonster(payload.VictimDefId, out var monster))
            return;
        _store.RecordKill(characterId, monster.Id, monster.Boss);
    }

    public void Handle(in EventEnvelope envelope, in OutpostVisited payload)
    {
        if (!_visitWatermark.TryAccept(envelope))
            return;
        if (FindNpc?.Invoke(payload.NpcEntityId) is { } npc)
            RecordOutpostVisit(payload.CharacterId, npc);
    }

    /// <summary>
    /// The character is at outpost <paramref name="npc"/>: "outposts visited" counts it once, keyed by its npc marker id (stable
    /// across runs; two outposts may share one <c>NpcDef</c>). True when the counter changed.
    /// </summary>
    public bool RecordOutpostVisit(long characterId, Npc npc) =>
        npc.Def.Kind == NpcKind.Outpost && Record(characterId, AchievementCategory.OutpostsVisited, npc.MarkerId);

    /// <summary>
    /// An achievement event of a character (runs claimed, outposts visited by outpost id). Callers must be idempotent
    /// themselves (an event consumer with its watermark); a distinct counter also ignores a key it has. True when a counter changed.
    /// </summary>
    public bool Record(long characterId, AchievementCategory category, string key) =>
        _store.IsAttached(characterId) && _store.Record(characterId, category, key);

    /// <summary>R-3.6-05 (WP-M3-1): every harvest counts once per operation key.</summary>
    public void Handle(in EventEnvelope envelope, in Harvested payload)
    {
        if (_harvestWatermark.TryAccept(envelope) && FirstTime(payload.CharacterId, payload.OpKey))
            Record(payload.CharacterId, AchievementCategory.ResourcesHarvested, payload.OpKey);
    }

    /// <summary>R-3.6-05 (WP-M3-1): every craft counts once per operation key, for the crafter.</summary>
    public void Handle(in EventEnvelope envelope, in Crafted payload)
    {
        if (_craftWatermark.TryAccept(envelope) && FirstTime(payload.CharacterId, payload.OpKey))
            Record(payload.CharacterId, AchievementCategory.ItemsCrafted, payload.OpKey);
    }

    /// <summary>The character left the world: its remembered operation keys go.</summary>
    public void Forget(long characterId) => ActivityOps?.Forget(characterId);

    private bool FirstTime(long characterId, string opKey) =>
        _store.IsAttached(characterId) && (ActivityOps?.TryAccept(characterId, opKey) ?? true);
}
