using GameServer.Combat;
using GameServer.Events;

namespace GameServer.Progression;

/// <summary>
/// Exp from kills (R-3.6-01, R-3.6-03; spec 3.6 <c>ExpService</c>, WP-M2-3). Consumes <see cref="Killed"/> in phase 11,
/// idempotent per event ID (<see cref="EventWatermark"/>): the killer's character (the envelope's actor) gets the monster's
/// base exp x the rank modifier (monster rank against the character's) x the band modifier (the character's level against
/// its rank's band), rounded down, with no floor. Level-ups, several in one kill allowed, give skill points (R-3.5-04) and
/// publish <see cref="LevelUp"/>; every gain publishes <see cref="ExpGained"/> for telemetry.
/// <list type="bullet">
/// <item>Players give no exp (R-3.6-01: kill monsters for exp); a kill with no character as actor gives nothing.</item>
/// <item>TODO(M4): per party member in range, each with their own rank and level (R-3.6-03); M2 has no parties, so the killer.</item>
/// </list>
/// The monster's level, base exp and rank come from the world's current <see cref="CombatRules"/>, so a hot reload of
/// <c>MonsterDef</c> reaches the next kill.
/// </summary>
public sealed class ExpService : IEventConsumer<Killed>
{
    private readonly ProgressionStore _store;
    private readonly Func<CombatRules?> _combat;
    private readonly EventBus? _events;
    private EventWatermark _watermark;

    public ExpService(ProgressionStore store, Func<CombatRules?> combat, EventBus? events)
    {
        _store = store;
        _combat = combat;
        _events = events;
    }

    public void Subscribe(EventBus events) => events.Subscribe<Killed>(this);

    public void Handle(in EventEnvelope envelope, in Killed payload)
    {
        if (!_watermark.TryAccept(envelope))
            return; // a re-dispatched kill pays nothing twice
        long characterId = envelope.ActorCharacterId;
        if (characterId == 0 || payload.VictimCharacterId != 0 || !_store.IsAttached(characterId))
            return;
        if (_combat() is not { } combat || !combat.TryGetMonster(payload.VictimDefId, out var monster))
            return;

        var curves = _store.Rules.Curves;
        var before = _store.Snapshot(characterId);
        int rankPercent = curves.RankPercent(before.Rank, monster.Rank);
        long bandPpm = curves.BandPpm(before.Level, before.Rank);
        long gain = Shared.Simulation.Progression.ExpMath.Gain(curves.BaseExpOf(monster), rankPercent, bandPpm);
        Apply(characterId, payload.KillerId, gain, payload.VictimDefId, rankPercent, bandPpm, envelope.RegionId);
    }

    /// <summary>
    /// Exp from another source, already computed (WP-M2-6: run claims pay through here). No modifiers are applied.
    /// Returns the levels gained.
    /// </summary>
    public int Grant(long characterId, uint entityId, long amount, int regionId = 0)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount));
        return _store.IsAttached(characterId) ? Apply(characterId, entityId, amount, 0, 100, Shared.Simulation.Progression.ExpMath.PpmOne, regionId) : 0;
    }

    private int Apply(long characterId, uint entityId, long gain, int victimDefId, int rankPercent, long bandPpm, int regionId)
    {
        int fromLevel = _store.AddExp(characterId, gain, out int levelsGained);
        if (_events == null)
            return levelsGained;
        _events.Publish(new ExpGained(characterId, entityId, gain, victimDefId, rankPercent, bandPpm), characterId, regionId);
        if (levelsGained > 0)
            _events.Publish(new LevelUp(characterId, entityId, fromLevel, fromLevel + levelsGained), characterId, regionId);
        return levelsGained;
    }
}
