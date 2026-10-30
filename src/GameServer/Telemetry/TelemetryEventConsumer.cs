using GameServer.Events;

namespace GameServer.Telemetry;

/// <summary>
/// Bus consumer that records the session, combat, status, item and economy events (backlog H2, spec 8.1 "Telemetry" consumers) as
/// <see cref="TelemetryRecord"/>s. Runs in phase 11 and only enqueues, so it never blocks the tick. Idempotent: a
/// redelivered event ID is skipped. One instance per bus, because event IDs are per world.
/// </summary>
public sealed class TelemetryEventConsumer :
    IEventConsumer<SessionStarted>,
    IEventConsumer<SessionEnded>,
    IEventConsumer<Moved>,
    IEventConsumer<ActionStarted>,
    IEventConsumer<HitLanded>,
    IEventConsumer<Damaged>,
    IEventConsumer<Killed>,
    IEventConsumer<Died>,
    IEventConsumer<Staggered>,
    IEventConsumer<Flinched>,
    IEventConsumer<ParryAttempted>,
    IEventConsumer<Respawned>,
    IEventConsumer<EffectApplied>,
    IEventConsumer<EffectRemoved>,
    IEventConsumer<EffectCleansed>,
    IEventConsumer<Healed>,
    IEventConsumer<BuildupTriggered>,
    IEventConsumer<ItemCreated>,
    IEventConsumer<ItemDestroyed>,
    IEventConsumer<LootDropped>,
    IEventConsumer<LootPickedUp>,
    IEventConsumer<LootDropExpired>,
    IEventConsumer<EquipmentChanged>,
    IEventConsumer<OutpostVisited>,
    IEventConsumer<ItemSold>,
    IEventConsumer<GoldChanged>,
    IEventConsumer<GoldDestroyed>,
    IEventConsumer<DemandUpdated>,
    IEventConsumer<NodeDemandUpdated>,
    IEventConsumer<PriceChanged>,
    IEventConsumer<ExpGained>,
    IEventConsumer<LevelUp>,
    IEventConsumer<RankChanged>,
    IEventConsumer<AttributesChanged>,
    IEventConsumer<SkillLearned>,
    IEventConsumer<SkillEvolved>,
    IEventConsumer<Respecced>,
    IEventConsumer<ClassAdvanced>,
    IEventConsumer<CardEarned>,
    IEventConsumer<CardEvolved>,
    IEventConsumer<BookChanged>
{
    private readonly TelemetryQueue _queue;
    private EventWatermark _watermark;

    private TelemetryEventConsumer(TelemetryQueue queue)
    {
        _queue = queue;
    }

    /// <summary>Creates a consumer for <paramref name="bus"/> and subscribes it to every recorded event type.</summary>
    public static TelemetryEventConsumer Attach(EventBus bus, TelemetryQueue queue)
    {
        var consumer = new TelemetryEventConsumer(queue);
        bus.Subscribe<SessionStarted>(consumer);
        bus.Subscribe<SessionEnded>(consumer);
        bus.Subscribe<Moved>(consumer);
        bus.Subscribe<ActionStarted>(consumer);
        bus.Subscribe<HitLanded>(consumer);
        bus.Subscribe<Damaged>(consumer);
        bus.Subscribe<Killed>(consumer);
        bus.Subscribe<Died>(consumer);
        bus.Subscribe<Staggered>(consumer);
        bus.Subscribe<Flinched>(consumer);
        bus.Subscribe<ParryAttempted>(consumer);
        bus.Subscribe<Respawned>(consumer);
        bus.Subscribe<EffectApplied>(consumer);
        bus.Subscribe<EffectRemoved>(consumer);
        bus.Subscribe<EffectCleansed>(consumer);
        bus.Subscribe<Healed>(consumer);
        bus.Subscribe<BuildupTriggered>(consumer);
        bus.Subscribe<ItemCreated>(consumer);
        bus.Subscribe<ItemDestroyed>(consumer);
        bus.Subscribe<LootDropped>(consumer);
        bus.Subscribe<LootPickedUp>(consumer);
        bus.Subscribe<LootDropExpired>(consumer);
        bus.Subscribe<EquipmentChanged>(consumer);
        bus.Subscribe<OutpostVisited>(consumer);
        bus.Subscribe<ItemSold>(consumer);
        bus.Subscribe<GoldChanged>(consumer);
        bus.Subscribe<GoldDestroyed>(consumer);
        bus.Subscribe<DemandUpdated>(consumer);
        bus.Subscribe<NodeDemandUpdated>(consumer);
        bus.Subscribe<PriceChanged>(consumer);
        bus.Subscribe<ExpGained>(consumer);
        bus.Subscribe<LevelUp>(consumer);
        bus.Subscribe<RankChanged>(consumer);
        bus.Subscribe<AttributesChanged>(consumer);
        bus.Subscribe<SkillLearned>(consumer);
        bus.Subscribe<SkillEvolved>(consumer);
        bus.Subscribe<Respecced>(consumer);
        bus.Subscribe<ClassAdvanced>(consumer);
        bus.Subscribe<CardEarned>(consumer);
        bus.Subscribe<CardEvolved>(consumer);
        bus.Subscribe<BookChanged>(consumer);
        return consumer;
    }

    public void Handle(in EventEnvelope envelope, in SessionStarted payload) =>
        Record(TelemetryKind.SessionStarted, envelope, payload.AccountId, payload.EntityId, v0: payload.Reattached ? 1 : 0);

    public void Handle(in EventEnvelope envelope, in SessionEnded payload) =>
        Record(TelemetryKind.SessionEnded, envelope, payload.AccountId, payload.EntityId, v0: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in Moved payload) =>
        Record(TelemetryKind.Moved, envelope, subject: payload.EntityId, v0: payload.XCm, v1: payload.YCm, v2: payload.Facing);

    public void Handle(in EventEnvelope envelope, in ActionStarted payload) =>
        Record(TelemetryKind.ActionStarted, envelope, subject: payload.EntityId,
            v0: payload.ActionId, v1: payload.StartTick, v2: payload.Facing, v3: (long)payload.Color);

    public void Handle(in EventEnvelope envelope, in HitLanded payload) =>
        Record(TelemetryKind.HitLanded, envelope, subject: payload.AttackerId, target: payload.TargetId, v0: payload.ActionId);

    public void Handle(in EventEnvelope envelope, in Damaged payload) =>
        Record(TelemetryKind.Damaged, envelope, subject: payload.SourceId, target: payload.TargetId,
            v0: payload.ActionId, v1: payload.Amount, v2: payload.HpAfter, v3: payload.Crit ? 1 : 0);

    public void Handle(in EventEnvelope envelope, in Killed payload) =>
        Record(TelemetryKind.Killed, envelope, subject: payload.KillerId, target: payload.VictimId,
            v0: payload.VictimCharacterId, v1: payload.VictimDefId, v2: payload.XCm, v3: payload.YCm);

    public void Handle(in EventEnvelope envelope, in Died payload) =>
        Record(TelemetryKind.Died, envelope, subject: payload.EntityId, target: payload.KillerId);

    public void Handle(in EventEnvelope envelope, in Staggered payload) =>
        Record(TelemetryKind.Staggered, envelope, subject: payload.EntityId, target: payload.SourceId);

    public void Handle(in EventEnvelope envelope, in Flinched payload) =>
        Record(TelemetryKind.Flinched, envelope, subject: payload.EntityId, target: payload.SourceId, v0: payload.DurationMs);

    public void Handle(in EventEnvelope envelope, in ParryAttempted payload) =>
        Record(TelemetryKind.ParryAttempted, envelope, subject: payload.DefenderId, target: payload.AttackerId,
            v0: payload.ActionId, v1: (long)payload.Color, v2: (long)payload.Result);

    public void Handle(in EventEnvelope envelope, in Respawned payload) =>
        Record(TelemetryKind.Respawned, envelope, subject: payload.EntityId);

    public void Handle(in EventEnvelope envelope, in EffectApplied payload) =>
        Record(TelemetryKind.EffectApplied, envelope, subject: payload.TargetId, target: payload.SourceId,
            v0: payload.StatusId, v1: payload.Stacks, v2: payload.EndTick, v3: payload.Refreshed ? 1 : 0);

    public void Handle(in EventEnvelope envelope, in EffectRemoved payload) =>
        Record(TelemetryKind.EffectRemoved, envelope, subject: payload.TargetId, v0: payload.StatusId, v1: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in EffectCleansed payload) =>
        Record(TelemetryKind.EffectCleansed, envelope, subject: payload.TargetId, target: payload.SourceId,
            v0: payload.StatusId, v1: payload.ActionId);

    public void Handle(in EventEnvelope envelope, in Healed payload) =>
        Record(TelemetryKind.Healed, envelope, subject: payload.SourceId, target: payload.TargetId,
            v0: payload.ActionId, v1: payload.StatusId, v2: payload.Amount, v3: payload.HpAfter);

    public void Handle(in EventEnvelope envelope, in BuildupTriggered payload) =>
        Record(TelemetryKind.BuildupTriggered, envelope, subject: payload.TargetId, target: payload.SourceId,
            v0: payload.BuildUpId, v1: payload.StatusId);

    public void Handle(in EventEnvelope envelope, in ItemCreated payload) =>
        Record(TelemetryKind.ItemCreated, envelope, subject: payload.ItemNetId, v0: payload.Rarity, v1: payload.Count,
            v2: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in ItemDestroyed payload) =>
        Record(TelemetryKind.ItemDestroyed, envelope, subject: payload.ItemNetId, v0: payload.Rarity, v1: payload.Count,
            v2: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in LootDropped payload) =>
        Record(TelemetryKind.LootDropped, envelope, subject: payload.DropId, target: payload.KillEventId,
            v0: payload.ItemNetId, v1: payload.Count, v2: payload.MonsterDefId, v3: payload.OwnerCharacterId);

    public void Handle(in EventEnvelope envelope, in LootPickedUp payload) =>
        Record(TelemetryKind.LootPickedUp, envelope, subject: payload.DropId, v0: payload.ItemNetId, v1: payload.Count,
            v2: payload.CountLeft);

    public void Handle(in EventEnvelope envelope, in LootDropExpired payload) =>
        Record(TelemetryKind.LootDropExpired, envelope, subject: payload.DropId, v0: payload.ItemNetId, v1: payload.Count);

    public void Handle(in EventEnvelope envelope, in EquipmentChanged payload) =>
        Record(TelemetryKind.EquipmentChanged, envelope, subject: payload.CharacterId, v0: payload.Slot);

    public void Handle(in EventEnvelope envelope, in OutpostVisited payload) =>
        Record(TelemetryKind.OutpostVisited, envelope, subject: payload.NpcEntityId, v0: payload.NpcDefId);

    public void Handle(in EventEnvelope envelope, in ItemSold payload) =>
        Record(TelemetryKind.ItemSold, envelope, subject: payload.ItemNetId, v0: payload.Rarity, v1: payload.Count,
            v2: payload.Gold, v3: payload.NpcDefId);

    public void Handle(in EventEnvelope envelope, in GoldChanged payload) =>
        Record(TelemetryKind.GoldChanged, envelope, v0: payload.Delta, v1: payload.Balance, v2: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in GoldDestroyed payload) =>
        Record(TelemetryKind.GoldDestroyed, envelope, v0: payload.Amount, v1: (long)payload.Reason);

    public void Handle(in EventEnvelope envelope, in DemandUpdated payload) =>
        Record(TelemetryKind.DemandUpdated, envelope, subject: payload.MonsterNetId, v0: payload.DecayedMilli,
            v1: payload.RatioPermille, v2: payload.SpawnDelayPercent, v3: payload.RewardPercent);

    public void Handle(in EventEnvelope envelope, in NodeDemandUpdated payload) =>
        Record(TelemetryKind.NodeDemandUpdated, envelope, subject: payload.NodeNetId, v0: payload.DecayedMilli,
            v1: payload.RatioPermille, v2: payload.SpawnDelayPercent);

    public void Handle(in EventEnvelope envelope, in PriceChanged payload) =>
        Record(TelemetryKind.PriceChanged, envelope, subject: payload.ItemNetId, v0: payload.DecayedMilli, v1: payload.PricePercent);
    public void Handle(in EventEnvelope envelope, in ExpGained payload) =>
        Record(TelemetryKind.ExpGained, envelope, subject: payload.EntityId, v0: payload.Amount, v1: payload.VictimDefId,
            v2: payload.RankPercent, v3: payload.BandPpm);

    public void Handle(in EventEnvelope envelope, in LevelUp payload) =>
        Record(TelemetryKind.LevelUp, envelope, subject: payload.EntityId, v0: payload.FromLevel, v1: payload.ToLevel);

    public void Handle(in EventEnvelope envelope, in RankChanged payload) =>
        Record(TelemetryKind.RankChanged, envelope, subject: payload.EntityId, v0: (long)payload.From, v1: (long)payload.To);

    public void Handle(in EventEnvelope envelope, in AttributesChanged payload) =>
        Record(TelemetryKind.AttributesChanged, envelope, subject: payload.EntityId, v0: payload.Attribute, v1: payload.Points,
            v2: payload.Unspent);

    public void Handle(in EventEnvelope envelope, in SkillLearned payload) =>
        Record(TelemetryKind.SkillLearned, envelope, subject: payload.EntityId, v0: SkillIdHash.Of(payload.NodeId), v1: payload.Level,
            v2: payload.PointsSpent);

    public void Handle(in EventEnvelope envelope, in SkillEvolved payload) =>
        Record(TelemetryKind.SkillEvolved, envelope, subject: payload.EntityId, v0: SkillIdHash.Of(payload.FromNodeId),
            v1: payload.FromLevel, v2: SkillIdHash.Of(payload.NodeId), v3: payload.PointsSpent);

    public void Handle(in EventEnvelope envelope, in Respecced payload) =>
        Record(TelemetryKind.Respecced, envelope, subject: payload.EntityId, v0: payload.NodesCleared, v1: payload.PointsRefunded);

    public void Handle(in EventEnvelope envelope, in ClassAdvanced payload) =>
        Record(TelemetryKind.ClassAdvanced, envelope, subject: payload.EntityId, v0: SkillIdHash.Of(payload.FromClassId),
            v1: SkillIdHash.Of(payload.ToClassId), v2: payload.Tier);
    public void Handle(in EventEnvelope envelope, in CardEarned payload) =>
        Record(TelemetryKind.CardEarned, envelope, subject: payload.EntityId, v0: payload.MonsterDefId, v1: (long)payload.Tier, v2: payload.Kills);

    public void Handle(in EventEnvelope envelope, in CardEvolved payload) =>
        Record(TelemetryKind.CardEvolved, envelope, subject: payload.EntityId, v0: payload.MonsterDefId, v1: (long)payload.From,
            v2: (long)payload.To, v3: payload.Kills);

    public void Handle(in EventEnvelope envelope, in BookChanged payload) =>
        Record(TelemetryKind.BookChanged, envelope, subject: payload.EntityId, v0: payload.Version, v1: payload.Placed);

    private void Record(
        TelemetryKind kind,
        in EventEnvelope envelope,
        long accountId = 0,
        long subject = 0,
        long target = 0,
        long v0 = 0,
        long v1 = 0,
        long v2 = 0,
        long v3 = 0)
    {
        if (!_watermark.TryAccept(envelope))
            return;

        // A full queue drops the record and counts it (spec 7.2); there is no retry.
        _queue.TryEnqueue(new TelemetryRecord(
            kind, envelope.UtcMs, envelope.EventId, envelope.WorldId, envelope.RegionId, envelope.ActorCharacterId,
            accountId, subject, target, v0, v1, v2, v3));
    }
}
