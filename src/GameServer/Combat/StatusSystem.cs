using GameServer.Entities;
using GameServer.Events;
using GameServer.World;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Actions;
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
/// lower every meter of their targets after their removal or heal (R-3.8-04). Heals never crit (TODO(open)). WP-M2-9 (spec 3.7
/// "M2 implementation"): a stun holds the target's action machine; a burst lands once in the first phase 6 after the status
/// is applied or refreshed; a RemoveStatus by kind removes the newest first, at most its <c>removeCount</c>; a status of an
/// <c>endsWithPhase</c> effect ends when the caster's action leaves that phase; periodic damage captures the Bystial bonus.
/// Allocation-free once its lists have grown. World simulation thread only.
/// </summary>
internal sealed class StatusSystem
{
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly SpatialGrid _grid;
    private CombatRules _rules;
    private readonly HitResolver _hits;
    private readonly EventBus? _events;
    private readonly StrikeQueue _queue = new();
    private readonly List<(Entity User, GameServer.Items.ConsumableEntry Entry)> _consumables = new(8);
    private readonly List<Entity> _candidates = new(32);
    private readonly List<Entity> _targets = new(32);
    private long _nextInstanceId;
    private long _nextAppliedSeq;

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

    /// <summary>
    /// Dev hot reload (spec 11.7), at a tick boundary: the reloaded tables from now on, and every active status moves to its
    /// reloaded row (same id; the reload keeps every status). Its remaining time, stacks and next periodic tick stay; its
    /// stat changes are taken out and put back from the new row, and its periodic amounts come from the new row.
    /// </summary>
    public void Retune(CombatRules rules, IReadOnlyList<Entity> entities, uint tick)
    {
        _rules = rules;
        for (int e = 0; e < entities.Count; e++)
        {
            if (entities[e].Combatant is not { } combatant || combatant.Statuses.Count == 0)
                continue;
            var statuses = combatant.Statuses;
            for (int i = 0; i < statuses.Count; i++)
            {
                ref var instance = ref statuses[i];
                if (!rules.Statuses.TryGet(instance.Definition.Id, out var definition))
                    continue; // not reached: the reload refuses a removed status
                combatant.Stats.RemoveSource(instance.Definition.SourceType, instance.InstanceId);
                instance.Definition = definition;
                AddModifiers(combatant.Stats, definition, instance.InstanceId, instance.Stacks);
            }
            combatant.Meters.Settle(rules.MsAt(tick));
        }
    }

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
        for (int i = 0; i < _consumables.Count; i++)
            ResolveConsumable(_consumables[i].User, _consumables[i].Entry, tick);
        _consumables.Clear();
    }

    /// <summary>
    /// A consumable used in phase 3 (spec 3.4 consumable use, R-3.7-05; WP-M3-3), resolved after the queued effects, in
    /// firing order: its flat heal and MP restore at once, then its status (source potion or food) on the user.
    /// </summary>
    internal void QueueConsumable(Entity user, GameServer.Items.ConsumableEntry entry) => _consumables.Add((user, entry));

    private void ResolveConsumable(Entity user, GameServer.Items.ConsumableEntry entry, uint tick)
    {
        if (user.Combatant is not { IsAlive: true } combatant)
            return; // died since it drank: the item is used, its effects land on no one
        var def = entry.Def;
        Heal(user.Id, CharacterOf(user), user, combatant, def.HealHp, entry.ActionNetId, statusId: 0, tick);
        if (def.RestoreMp > 0)
            combatant.Meters.Restore(ResourceKind.Mp, def.RestoreMp, _rules.MsAt(tick));
        if (entry.Status != null)
            Apply(user, user, entry.Status, tick);
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
            Ended(entity, definition, tick);
        }
        combatant.Meters.Settle(_rules.MsAt(tick));
    }

    /// <summary>
    /// Applies a status to a target (also the entry point of WP11's build-up triggers and, WP-M2-9, of on-hit statuses).
    /// Returns false when the target is dead or immune (R-3.7-02). A stun interrupts the target's action machine (applied
    /// again, it starts over); a burst lands in the next phase 6. <paramref name="boundAction"/> (an <c>endsWithPhase</c>
    /// effect) ties the status to the caster's own action and phase.
    /// </summary>
    public bool Apply(Entity source, Entity target, StatusDefinition definition, uint tick, int boundAction = -1,
        ActionState boundPhase = ActionState.Idle)
    {
        if (source.Combatant is not { } caster || target.Combatant is not { IsAlive: true } combatant)
            return false;
        if (definition.Def.IsBlockedBy(combatant.StatusImmuneTags))
            return false;
        var statuses = combatant.Statuses;
        int index = statuses.IndexOf(definition);
        bool refreshed = index >= 0;
        var captured = Capture(source, caster, target, definition.Def);
        bool burst = definition.Def.HasBurst();
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
            instance.AppliedSeq = ++_nextAppliedSeq;
            instance.BurstPending |= burst;
            instance.BoundActionIndex = boundAction;
            instance.BoundPhase = boundPhase;
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
                AppliedSeq = ++_nextAppliedSeq,
                Stacks = 1,
                AppliedTick = tick,
                EndTick = tick + (uint)definition.DurationTicks,
                NextTickAt = tick + (uint)definition.TickIntervalTicks,
                BurstPending = burst,
                BoundActionIndex = boundAction,
                BoundPhase = boundPhase,
                SourceEntityId = source.Id,
                SourceCharacterId = CharacterOf(source),
                Captured = captured,
            };
            AddModifiers(combatant.Stats, definition, instance.InstanceId, 1);
            statuses.Add(instance);
            Publish(target, source, definition, 1, instance.EndTick, refreshed: false);
        }
        // Spec 3.7 M2 status list: no actions or movement while stunned (through super armor, user answer 2026-10-28).
        if (definition.Control == StatusControl.Stun)
            ActionMachine.Interrupt(target.Actions, InterruptKind.Stun, definition.Def.DurationMs);
        combatant.Meters.Settle(_rules.MsAt(tick));
        return true;
    }

    /// <summary>
    /// Every way a status ends: a build-up meter it locked resets (spec 3.8), and a stun lets the action machine go (WP-M2-9;
    /// a dead machine stays dead).
    /// </summary>
    private void Ended(Entity entity, StatusDefinition definition, uint tick)
    {
        BuildUps?.StatusEnded(entity, definition, tick);
        if (definition.Control == StatusControl.Stun)
            ActionMachine.EndStun(entity.Actions);
    }

    private void Advance(Entity entity, Combatant combatant, uint tick)
    {
        var statuses = combatant.Statuses;
        bool changed = false;
        for (int i = 0; i < statuses.Count;)
        {
            ref var instance = ref statuses[i];
            var definition = instance.Definition;
            // WP-M2-9 endsWithPhase: gone once the caster's action left the phase (completed, cancelled or interrupted).
            if (instance.BoundActionIndex >= 0
                && (entity.Actions.ActionIndex != instance.BoundActionIndex || entity.Actions.State != instance.BoundPhase))
            {
                combatant.Stats.RemoveSource(definition.SourceType, instance.InstanceId);
                _events?.Publish(new EffectRemoved(entity.Id, definition.NetId, StatusEndReason.ActionEnded));
                statuses.RemoveAt(i);
                Ended(entity, definition, tick);
                changed = true;
                continue;
            }
            // WP-M2-9: a burst lands once, in the first phase 6 after it was applied or refreshed.
            if (instance.BurstPending)
            {
                instance.BurstPending = false;
                Burst(entity, instance, tick);
                if (!combatant.IsAlive)
                {
                    EndAll(entity, StatusEndReason.Died, tick); // the burst killed it
                    return;
                }
                instance = ref statuses[i];
            }
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
                Ended(entity, definition, tick);
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

    /// <summary>
    /// WP-M2-9 (spec 3.8 M2 numbers): the once-only damage of a status: a percent of the target's max HP (Blood loss, raw
    /// and capped on bosses, R-3.8-08) and/or a percent of the applier's captured power (Cold), credited to the applier.
    /// </summary>
    private void Burst(Entity entity, in StatusInstance instance, uint tick)
    {
        var def = instance.Definition.Def;
        if (def.BurstMaxHpPercent > 0)
        {
            _hits.PercentOfMaxHp(instance.SourceEntityId, instance.SourceCharacterId, entity, def.BurstMaxHpPercent, tick);
            if (entity.Combatant is not { IsAlive: true })
                return;
        }
        if (def.BurstDamagePercent > 0)
        {
            var input = instance.Captured;
            input.SkillMultiplierPercent = def.BurstDamagePercent;
            var kind = def.TickDamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
            _hits.DamageOverTime(instance.SourceEntityId, instance.SourceCharacterId, entity, input, kind,
                instance.Definition.TickElement, tick);
        }
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
                // The skill's level raises the heal percent too (WP-M2-5, user decision 2026-10-27).
                int amount = HealAmount(caster.Combatant.Stats.Get(_rules.Config.Ids.Matk),
                    (int)((long)effect.HealPercent * caster.Combatant.SkillPercentFor(effect.ActionId) / 100));
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
                int boundAction = -1;
                var boundPhase = ActionState.Idle;
                if (effect.EndsWithPhase)
                {
                    // WP-M2-9: held by the caster's own action; if that action already ended, nothing is applied.
                    if (caster.Actions.Action is not { } running || running.Id != effect.ActionId)
                        break;
                    boundAction = caster.Actions.ActionIndex;
                    boundPhase = (ActionState)(byte)effect.Phase;
                }
                for (int t = 0; t < _targets.Count; t++)
                    Apply(caster, _targets[t], definition, tick, boundAction, boundPhase);
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
                        Remove(caster, _targets[t], named, effect.StatusKind, effect.RemoveCount, actionId, tick);
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

    /// <summary>
    /// A named status always goes; by kind only the dispellable ones (R-3.7-03), the newest application first, at most
    /// <paramref name="count"/> (0: all; spec 3.7 M2 status list: Simple Cleanse 2, a full cleanse all).
    /// </summary>
    private void Remove(Entity source, Entity target, StatusDefinition? named, StatusEffectKind kind, int count, int actionId,
        uint tick)
    {
        if (target.Combatant is not { IsAlive: true } combatant)
            return;
        var statuses = combatant.Statuses;
        bool changed = false;
        for (int removed = 0; count == 0 || removed < count; removed++)
        {
            int newest = -1;
            for (int i = 0; i < statuses.Count; i++)
            {
                var candidate = statuses[i].Definition;
                bool match = named != null ? ReferenceEquals(candidate, named) : candidate.Kind == kind && candidate.Def.Dispellable;
                if (match && (newest < 0 || statuses[i].AppliedSeq > statuses[newest].AppliedSeq))
                    newest = i;
            }
            if (newest < 0)
                break;
            var definition = statuses[newest].Definition;
            combatant.Stats.RemoveSource(definition.SourceType, statuses[newest].InstanceId);
            _events?.Publish(new EffectCleansed(target.Id, source.Id, definition.NetId, actionId), CharacterOf(source));
            statuses.RemoveAt(newest);
            Ended(target, definition, tick); // R-3.8-04: a cleanse can end a build-up's effect
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

    /// <summary>
    /// The applier's side of a periodic tick or burst, read when the status is applied: damage dealt plus the Bystial book's
    /// bonus against the target (R-14.2-05; user answer 2026-10-28: periodic damage carries it, captured).
    /// </summary>
    private DamageInput Capture(Entity source, Combatant caster, Entity target, StatusEffectDef def)
    {
        var ids = _rules.Config.Ids;
        var stats = caster.Stats;
        if (def.TickHealPercent > 0)
            return new DamageInput { Power = stats.Get(ids.Matk) };
        if (!def.DealsPowerDamage())
            return default;
        bool magic = def.TickDamageKind == EffectDamageKind.Magic;
        long extra = stats.Get(ids.DamageDealtPercent);
        if (_hits.CardBonus != null)
            extra += _hits.CardBonus.DamagePercent(source, target);
        return new DamageInput
        {
            Power = stats.Get(magic ? ids.Matk : ids.Atk),
            SkillMultiplierPercent = def.TickDamagePercent,
            PenetrationPercent = stats.Get(magic ? ids.MagicPenPercent : ids.PhysPenPercent),
            PenetrationFlat = stats.Get(magic ? ids.MagicPenFlat : ids.PhysPenFlat),
            AttackerRank = caster.Rank,
            ExtraPercent = Clamp(extra),
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
