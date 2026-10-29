using GameServer.Entities;
using GameServer.Events;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>
/// Build-ups (spec 3.8, backlog D6; M1: Burn only): the meters on every <see cref="Combatant"/> and what changes them.
/// <list type="bullet">
/// <item>Hits: the <see cref="HitResolver"/> calls <see cref="ApplyHit"/> after the dodge and parry checks and after poise
/// (R-3.8-07: a dodged hit and a successful parry change nothing, an uncolored parry keeps <paramref name="keepPercent"/> of
/// each gain; a killing hit changes nothing, the meters reset on death). Each <c>EffectBuildUpDef</c> change of the effect is
/// a gain (decay settled first, then resistance and the repeat penalty, R-3.8-03, R-3.8-05) or a reduction (R-3.8-04, not
/// scaled). A target immune to the build-up's status (R-3.7-02 tags) gains nothing.</item>
/// <item>Trigger (R-3.8-02): a gain that fills the meter locks it full and applies its status through the
/// <see cref="StatusSystem"/>, credited to the hitter (R-3.8-09), and publishes <see cref="BuildupTriggered"/>. The meter
/// stays full until the status ends for any reason (<see cref="StatusEnded"/>: expiry, cleanse, death, reset), then resets
/// to 0 and starts the repeat-penalty window.</item>
/// <item>Reductions by effects (<c>buildUpReduction</c> of RemoveStatus and Heal, phase 6): <see cref="Reduce"/>.</item>
/// </list>
/// Deterministic: callers run in the fixed hit and phase order, changes in build-up order. Allocation-free. World
/// simulation thread only.
/// </summary>
internal sealed class BuildUpSystem
{
    private BuildUpCatalog _catalog;
    private int _resistStat; // buildUpResistPercent (WP-M2-8), -1 without the StatDef row
    private readonly StatusSystem _statuses;
    private readonly EventBus? _events;

    public BuildUpSystem(CombatRules rules, StatusSystem statuses, EventBus? events)
    {
        _catalog = rules.BuildUps;
        _statuses = statuses;
        _resistStat = rules.Config.Ids.BuildUpResistPercent;
        _events = events;
    }

    /// <summary>Dev hot reload (spec 11.7): the reloaded build-ups (same rows in the same order, so the meters stay valid).</summary>
    public void Retune(CombatRules rules)
    {
        _catalog = rules.BuildUps;
        _resistStat = rules.Config.Ids.BuildUpResistPercent;
    }

    /// <summary>Triggers whose status could not be applied (the target died meanwhile); the meter reset at once.</summary>
    public long TriggersRefused { get; private set; }

    /// <summary>
    /// A landed hit of <paramref name="effectId"/> on a living <paramref name="target"/> (spec 3.8 "HitResolver applies
    /// build-up changes after the dodge and parry checks"). <paramref name="keepPercent"/> is 100, or 100 minus an
    /// uncolored parry's reduction (R-3.8-07).
    /// </summary>
    public void ApplyHit(Entity attacker, Entity target, string effectId, int keepPercent, uint tick)
    {
        if (target.Combatant is not { IsAlive: true } victim || !_catalog.TryGetChanges(effectId, out var changes))
            return;
        // The Bystial book's build-up resistance (spec 14.2, WP-M2-8): whole percents less of every gain, capped by its table.
        if (_resistStat >= 0)
            keepPercent = keepPercent * (100 - Math.Clamp(victim.Stats.Get(_resistStat), 0, 100)) / 100;
        var meters = victim.BuildUps;
        for (int i = 0; i < changes.Length; i++)
        {
            var change = changes[i];
            var rule = _catalog[change.Index];
            ref var meter = ref meters[change.Index];
            if (change.Amount < 0)
            {
                meter.Reduce(rule, -change.Amount, tick);
                continue;
            }
            var status = _catalog.StatusOf(change.Index);
            if (status.Def.IsBlockedBy(victim.StatusImmuneTags))
                continue; // R-3.7-02: a meter whose effect cannot land does not fill
            if (meter.Gain(rule, change.Amount, victim.Stats.Get(rule.ResistStat), tick, keepPercent))
                Trigger(attacker, target, change.Index, tick);
        }
    }

    /// <summary>R-3.8-04: every unlocked meter of <paramref name="target"/> loses <paramref name="points"/> (Simple Cleanse).</summary>
    public void Reduce(Entity target, int points, uint tick)
    {
        if (points <= 0 || target.Combatant is not { IsAlive: true } combatant)
            return;
        var meters = combatant.BuildUps;
        for (int i = 0; i < meters.Length; i++)
            meters[i].Reduce(_catalog[i], points, tick);
    }

    /// <summary>
    /// The <see cref="StatusSystem"/> removed <paramref name="definition"/> from <paramref name="entity"/> (expired, cleansed,
    /// died, reset): a meter locked by it resets to 0 (R-3.8-02) and its repeat-penalty window starts (R-3.8-05).
    /// </summary>
    public void StatusEnded(Entity entity, StatusDefinition definition, uint tick)
    {
        if (entity.Combatant is not { } combatant)
            return;
        var meters = combatant.BuildUps;
        for (int i = 0; i < meters.Length; i++)
        {
            if (ReferenceEquals(_catalog.StatusOf(i), definition))
                meters[i].Unlock(_catalog[i], tick);
        }
    }

    /// <summary>The meter of <paramref name="buildUpId"/> on <paramref name="target"/> in whole points (tests, tools); 0 when unknown.</summary>
    public int PointsOf(Entity target, string buildUpId, uint tick)
    {
        int index = _catalog.IndexOf(buildUpId);
        if (index < 0 || target.Combatant is not { } combatant || index >= combatant.BuildUps.Length)
            return 0;
        return combatant.BuildUps[index].PointsAt(_catalog[index], tick);
    }

    private void Trigger(Entity source, Entity target, int index, uint tick)
    {
        var status = _catalog.StatusOf(index);
        _events?.Publish(new BuildupTriggered(target.Id, source.Id, _catalog[index].NetId, status.NetId),
            source is Player player ? player.CharacterId : 0);
        if (!_statuses.Apply(source, target, status, tick))
        {
            TriggersRefused++;
            ref var meter = ref target.Combatant!.BuildUps[index];
            meter.Unlock(_catalog[index], tick);
        }
    }
}
