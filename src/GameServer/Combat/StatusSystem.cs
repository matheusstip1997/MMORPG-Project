using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Stats;

namespace GameServer.Combat;

/// <summary>
/// Tick phase 6 (spec 1.6 "status/gauge updates", spec 3.7 <c>StatusSystem</c>, backlog D6): status effects and the
/// effects phase 3 fired that are not hits (Heal, ApplyStatus, RemoveStatus; user decision 2026-10-23: no dodge, parry or
/// pending hit). Deterministic: entities in id order, their statuses in the order they were first applied, then the queued
/// effects in firing order (caster id).
/// <list type="number">
/// <item>A dead entity loses every status (<see cref="StatusEndReason.Died"/>).</item>
/// <item>Each status's periodic tick when it is due (damage through <see cref="HitResolver.DamageOverTime"/>, credited to
/// its applier, or a heal), then its expiry: a status applied in phase 6 of tick T with a duration of n ticks is active
/// through phase 5 of tick T + n and removed in its phase 6, after a tick due then.</item>
/// <item>The queued effects: targets from the effect's <c>targets</c> filter (R-3.1-07): the caster for <c>self</c>; for
/// <c>allies</c> (same faction, alive, not the caster: every other player in M1) and <c>enemies</c>
/// (<see cref="HitResolver.CanHit"/>) the entities whose body circle touches one of its shapes, placed where the caster stood
/// and faced when it fired, tested where the targets stand now (no rewind). A caster that died since fired nothing.</item>
/// </list>
/// Applying a status a target has refreshes it (duration from now, one more stack below the cap, newest applier owns it);
/// one instance per status per target (user decision 2026-10-23). Immunity tags (R-3.7-02) block it. Stat changes go
/// into the target's <see cref="StatBlock"/> (source type buff or debuff, the instance as source id) and the meters are
/// settled at once, so a regeneration change starts exactly then. Every status that ends tells the <see cref="BuildUps"/>
/// (a build-up meter its trigger locked resets, spec 3.8), and RemoveStatus and Heal effects with a <c>buildUpReduction</c>
/// lower every meter of their targets after their removal or heal (R-3.8-04). Heals never crit (TODO(open)). Allocation-free once its
/// lists have grown. World simulation thread only.
/// </summary>
internal sealed class StatusSystem
{
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly SpatialGrid _grid;
    private readonly CombatRules _rules;
    private readonly HitResolver _hits;
    private readonly EventBus? _events;
    private readonly StrikeQueue _queue = new();
    private readonly List<Entity> _candidates = new(32);
    private readonly List<Entity> _targets = new(32);
    private long _nextInstanceId;

    public StatusSystem(SpatialGrid grid, CombatRules rules, HitResolver hits, EventBus? events)
    {
        _grid = grid;
        _rules = rules;
        _hits = hits;
        _events = events;
    }

    /// <summary>Heal, ApplyStatus and RemoveStatus effects fired in phase 3, waiting for phase 6 (same shape as strikes).</summary>
    public StrikeQueue Queue => _queue;

    /// <summary>Effects naming a status the catalog does not have (a data bug the validator should have caught).</summary>
    public long UnknownStatuses { get; private set; }

    /// <summary>
    /// Build-ups (spec 3.8): told whenever a status ends (a meter locked by it resets, R-3.8-02), and given the
    /// <c>buildUpReduction</c> of RemoveStatus and Heal effects (R-3.8-04). Null without build-ups.
    /// </summary>
    public BuildUpSystem? BuildUps { get; set; }

    /// <summary>Phase 6: deaths, periodic ticks and expiry, then the queued effects (cleared afterwards).</summary>
    public void Run(uint tick, IReadOnlyList<Entity> entities)
    {
        for (int e = 0; e < entities.Count; e++)
        {
            var entity = entities[e];
            if (entity.Combatant is not { } combatant || combatant.Statuses.Count == 0)
                continue;
            if (!combatant.IsAlive)
            {
                EndAll(entity, StatusEndReason.Died, tick);
                continue;
            }
            Advance(entity, combatant, tick);
        }
        for (int i = 0; i < _queue.Count; i++)
            ResolveOne(_queue[i], tick);
        _queue.Clear();
    }

    /// <summary>
    /// Ends every status of an entity (death, a monster's leash reset: user decision 2026-10-23), removing their stat
    /// changes and publishing <see cref="EffectRemoved"/>.
    /// </summary>
    public void EndAll(Entity entity, StatusEndReason reason, uint tick)
    {
        if (entity.Combatant is not { } combatant)
            return;
        var statuses = combatant.Statuses;
        if (statuses.Count == 0)
            return;
        while (statuses.Count > 0)
        {
            ref var instance = ref statuses[statuses.Count - 1];
            var definition = instance.Definition;
            combatant.Stats.RemoveSource(definition.SourceType, instance.InstanceId);
            _events?.Publish(new EffectRemoved(entity.Id, definition.NetId, reason));
            statuses.RemoveAt(statuses.Count - 1);
            BuildUps?.StatusEnded(entity, definition, tick);
        }
        combatant.Meters.Settle(_rules.MsAt(tick));
    }

    /// <summary>
    /// Applies a status to a target (also the entry point of WP11's build-up triggers). Returns false when the target is
    /// dead or immune (R-3.7-02).
    /// </summary>
    public bool Apply(Entity source, Entity target, StatusDefinition definition, uint tick)
    {
        if (source.Combatant is not { } caster || target.Combatant is not { IsAlive: true } combatant)
            return false;
        if (definition.Def.IsBlockedBy(combatant.StatusImmuneTags))
            return false;
        var statuses = combatant.Statuses;
        int index = statuses.IndexOf(definition);
        bool refreshed = index >= 0;
        var captured = Capture(caster, definition.Def);
        if (refreshed)
        {
            ref var instance = ref statuses[index];
            int stacks = Math.Min(definition.Def.MaxStacks, instance.Stacks + 1);
            if (stacks != instance.Stacks)
            {
                combatant.Stats.RemoveSource(definition.SourceType, instance.InstanceId);
                AddModifiers(combatant.Stats, definition, instance.InstanceId, stacks);
                instance.Stacks = stacks;
            }
            instance.EndTick = tick + (uint)definition.DurationTicks;
            instance.SourceEntityId = source.Id;
            instance.SourceCharacterId = CharacterOf(source);
            instance.Captured = captured;
            Publish(target, source, definition, instance.Stacks, instance.EndTick, refreshed: true);
        }
        else
        {
            var instance = new StatusInstance
            {
                Definition = definition,
                InstanceId = ++_nextInstanceId,
                Stacks = 1,
                AppliedTick = tick,
                EndTick = tick + (uint)definition.DurationTicks,
                NextTickAt = tick + (uint)definition.TickIntervalTicks,
                SourceEntityId = source.Id,
                SourceCharacterId = CharacterOf(source),
                Captured = captured,
            };
            AddModifiers(combatant.Stats, definition, instance.InstanceId, 1);
            statuses.Add(instance);
            Publish(target, source, definition, 1, instance.EndTick, refreshed: false);
        }
        combatant.Meters.Settle(_rules.MsAt(tick));
        return true;
    }

    private void Advance(Entity entity, Combatant combatant, uint tick)
    {
        var statuses = combatant.Statuses;
        bool changed = false;
        for (int i = 0; i < statuses.Count;)
        {
            ref var instance = ref statuses[i];
            var definition = instance.Definition;
            if (definition.TickIntervalTicks > 0 && instance.NextTickAt <= tick && instance.NextTickAt <= instance.EndTick)
            {
                instance.NextTickAt += (uint)definition.TickIntervalTicks;
                PeriodicTick(entity, combatant, instance, tick);
                if (!combatant.IsAlive)
                {
                    EndAll(entity, StatusEndReason.Died, tick); // the tick killed it
                    return;
                }
                instance = ref statuses[i];
            }
            if (instance.EndTick <= tick)
            {
                combatant.Stats.RemoveSource(definition.SourceType, instance.InstanceId);
                _events?.Publish(new EffectRemoved(entity.Id, definition.NetId, StatusEndReason.Expired));
                statuses.RemoveAt(i);
                BuildUps?.StatusEnded(entity, definition, tick);
                changed = true;
                continue;
            }
            i++;
        }
        if (changed)
            combatant.Meters.Settle(_rules.MsAt(tick));
    }

    private void PeriodicTick(Entity entity, Combatant combatant, in StatusInstance instance, uint tick)
    {
        var def = instance.Definition.Def;
        if (def.TickDamagePercent > 0)
        {
            var kind = def.TickDamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
            _hits.DamageOverTime(instance.SourceEntityId, instance.SourceCharacterId, entity, instance.Captured, kind,
                instance.Definition.TickElement, tick);
            return;
        }
        int amount = HealAmount(instance.Captured.Power, def.TickHealPercent);
        Heal(instance.SourceEntityId, instance.SourceCharacterId, entity, combatant, amount, actionId: 0, def.NetId, tick);
    }

    private void ResolveOne(in PendingStrike fired, uint tick)
    {
        var caster = fired.Attacker;
        if (caster.Combatant is not { IsAlive: true })
            return;
        var effect = fired.Effect;
        CollectTargets(fired);
        switch (effect.Kind)
        {
            case EffectKind.Heal:
            {
                int amount = HealAmount(caster.Combatant.Stats.Get(_rules.Config.Ids.Matk), effect.HealPercent);
                int actionId = _hits.ActionNetIdOf(effect);
                for (int t = 0; t < _targets.Count; t++)
                {
                    Heal(caster.Id, CharacterOf(caster), _targets[t], _targets[t].Combatant!, amount, actionId, statusId: 0, tick);
                    BuildUps?.Reduce(_targets[t], effect.BuildUpReduction, tick); // R-3.8-04
                }
                break;
            }
            case EffectKind.ApplyStatus:
            {
                if (!_rules.Statuses.TryGet(effect.StatusId, out var definition))
                {
                    UnknownStatuses++;
                    break;
                }
                for (int t = 0; t < _targets.Count; t++)
                    Apply(caster, _targets[t], definition, tick);
                break;
            }
            case EffectKind.RemoveStatus:
            {
                StatusDefinition? named = null;
                if (effect.StatusId.Length != 0 && !_rules.Statuses.TryGet(effect.StatusId, out named))
                {
                    UnknownStatuses++;
                    break;
                }
                int actionId = _hits.ActionNetIdOf(effect);
                bool removes = named != null || effect.StatusKind != StatusEffectKind.None;
                for (int t = 0; t < _targets.Count; t++)
                {
                    if (removes)
                        Remove(caster, _targets[t], named, effect.StatusKind, actionId, tick);
                    BuildUps?.Reduce(_targets[t], effect.BuildUpReduction, tick); // R-3.8-04 (Simple Cleanse)
                }
                break;
            }
        }
    }

    /// <summary>The effect's targets in entity-id order (R-3.1-07).</summary>
    private void CollectTargets(in PendingStrike fired)
    {
        _targets.Clear();
        var caster = fired.Attacker;
        var effect = fired.Effect;
        if (effect.HasTarget(EffectTarget.Self))
            _targets.Add(caster);
        bool allies = effect.HasTarget(EffectTarget.Allies), enemies = effect.HasTarget(EffectTarget.Enemies);
        if ((allies || enemies) && _rules.Shapes.TryGet(effect.Id, out var shapes))
        {
            int x = FixedPoint.ToCmRound(fired.Origin.X), y = FixedPoint.ToCmRound(fired.Origin.Y);
            int half = shapes.ReachCm + _rules.MaxBodyRadiusCm;
            _candidates.Clear();
            _grid.QueryCells(x - half, y - half, x + half, y + half, _candidates);
            for (int c = 0; c < _candidates.Count; c++)
            {
                var target = _candidates[c];
                if (target == caster || target.Combatant is not { IsAlive: true } other)
                    continue;
                bool wanted = allies && other.Faction == caster.Combatant!.Faction
                    || enemies && _hits.CanHit(caster, target);
                if (wanted && Overlaps(shapes, fired, target.Movement.Position, other.BodyRadiusRaw))
                    _targets.Add(target);
            }
        }
        _targets.Sort(ById);
    }

    private void Remove(Entity source, Entity target, StatusDefinition? named, StatusEffectKind kind, int actionId, uint tick)
    {
        if (target.Combatant is not { IsAlive: true } combatant)
            return;
        var statuses = combatant.Statuses;
        bool changed = false;
        for (int i = 0; i < statuses.Count;)
        {
            var definition = statuses[i].Definition;
            // A named status always goes; by kind only the dispellable ones (R-3.7-03). TODO(open): priority and limits.
            bool match = named != null ? ReferenceEquals(definition, named) : definition.Kind == kind && definition.Def.Dispellable;
            if (!match)
            {
                i++;
                continue;
            }
            combatant.Stats.RemoveSource(definition.SourceType, statuses[i].InstanceId);
            _events?.Publish(new EffectCleansed(target.Id, source.Id, definition.NetId, actionId), CharacterOf(source));
            statuses.RemoveAt(i);
            BuildUps?.StatusEnded(target, definition, tick); // R-3.8-04: a cleanse can end a build-up's effect
            changed = true;
        }
        if (changed)
            combatant.Meters.Settle(_rules.MsAt(tick));
    }

    /// <summary>
    /// Restores HP (never above the max, never on the dead), publishes <see cref="Healed"/> and leaves a heal notice when HP
    /// was gained. Also the per-tick heal of ground areas (<see cref="GroundAreaSystem"/>).
    /// </summary>
    internal void Heal(uint sourceId, long sourceCharacterId, Entity target, Combatant combatant, int amount, int actionId,
        int statusId, uint tick)
    {
        if (!combatant.IsAlive || amount <= 0)
            return;
        long nowMs = _rules.MsAt(tick);
        int gained = combatant.Meters.Restore(ResourceKind.Hp, amount, nowMs);
        if (gained <= 0)
            return;
        _events?.Publish(new Healed(sourceId, target.Id, actionId, statusId, gained, combatant.Hp(nowMs)), sourceCharacterId);
        _hits.NoticeHeal(sourceId, target.Id, gained);
    }

    /// <summary>Percent of a power stat, rounded down (no crit, TODO(open)).</summary>
    internal static int HealAmount(int power, int percent) =>
        (int)Math.Min(int.MaxValue, (long)Math.Max(0, power) * Math.Max(0, percent) / 100);

    /// <summary>The applier's side of a periodic tick, read when the status is applied.</summary>
    private DamageInput Capture(Combatant caster, StatusEffectDef def)
    {
        var ids = _rules.Config.Ids;
        var stats = caster.Stats;
        if (def.TickHealPercent > 0)
            return new DamageInput { Power = stats.Get(ids.Matk) };
        if (def.TickDamagePercent <= 0)
            return default;
        bool magic = def.TickDamageKind == EffectDamageKind.Magic;
        return new DamageInput
        {
            Power = stats.Get(magic ? ids.Matk : ids.Atk),
            SkillMultiplierPercent = def.TickDamagePercent,
            PenetrationPercent = stats.Get(magic ? ids.MagicPenPercent : ids.PhysPenPercent),
            PenetrationFlat = stats.Get(magic ? ids.MagicPenFlat : ids.PhysPenFlat),
            AttackerRank = caster.Rank,
            ExtraPercent = stats.Get(ids.DamageDealtPercent),
            NoCrit = true,
        };
    }

    private static void AddModifiers(StatBlock stats, StatusDefinition definition, long instanceId, int stacks)
    {
        var modifiers = definition.Modifiers;
        for (int m = 0; m < modifiers.Length; m++)
        {
            var modifier = modifiers[m];
            stats.Add(new StatModifier(modifier.StatId, Clamp((long)modifier.Flat * stacks), Clamp((long)modifier.Percent * stacks),
                definition.SourceType, instanceId));
        }
    }

    private void Publish(Entity target, Entity source, StatusDefinition definition, int stacks, uint endTick, bool refreshed) =>
        _events?.Publish(new EffectApplied(target.Id, source.Id, definition.NetId, stacks, endTick, refreshed), CharacterOf(source));

    private static bool Overlaps(EffectShapes shapes, in PendingStrike fired, FixVec2 position, int radiusRaw)
    {
        var list = shapes.Shapes;
        for (int s = 0; s < list.Count; s++)
        {
            if (list[s].Overlaps(fired.Origin, fired.Facing, position, radiusRaw))
                return true;
        }
        return false;
    }

    private static int Clamp(long value) => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value));

    private static long CharacterOf(Entity entity) => entity is Player player ? player.CharacterId : 0;
}
