using GameServer.Events;

namespace GameServer.Telemetry;

/// <summary>
/// Bus consumer that records the M1 session, combat and status events (backlog H2, spec 8.1 "Telemetry" consumers) as
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
    IEventConsumer<Healed>
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
            v0: payload.VictimCharacterId, v1: payload.VictimDefId);

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
