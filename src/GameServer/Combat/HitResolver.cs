using GameServer.Entities;
using GameServer.Events;
using GameServer.Law;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using Shared.Util;

namespace GameServer.Combat;

/// <summary>
/// The per-target hit pipeline of spec 3.1 (<c>HitResolver</c>, backlog D3, D4), shared by strikes (phase 5,
/// <see cref="StrikeResolver"/>) and projectiles (phase 4). Callers run it in a fixed order (entity id), so the results
/// and the crit rolls are deterministic (R-3.3-01, architecture rule 6).
/// <para>
/// <see cref="CanHit"/>: never itself; both sides fight and are alive; monsters never hit monsters; a player hits another
/// player only when <see cref="PvpRules.CanDamage"/> allows it (never in M1, R-6.1-01). Shapes, rewind and visibility
/// are the caller's (areas need no visibility, spec 3.1).
/// </para>
/// <para>
/// Pending hits (R-3.1-03): a monster's hit on a player is queued (<see cref="HitOutcome.Pending"/>) and lands in phase 5
/// once the defender's view lag has passed (<see cref="ResolveDue"/>, at most <c>lagCompensationCapMs</c>), judged on the
/// defender's timeline (<see cref="DefenceTimeline"/>): what it was doing when it saw the hit's tick. A pending hit still
/// lands when its attacker died meanwhile (it was swung first, and the defender saw it first); it is dropped when the
/// target died or left the world. Every other hit is judged at once on the server's clock (the live machine).
/// TODO(WP6-PvP): in PvP the defender rule applies to dodge and parry too, and the attacker is rewound by
/// <c>pvpAttackerRewindMs</c> (PvP is off in M1).
/// </para>
/// <para>
/// Landing a hit, in the order of spec 3.1:
/// (1) dodge i-frames (R-3.3-02): a dodge in its i-frame window ignores the hit, so no damage, poise or flinch (R-3.3-07).
/// (2) Parry (<see cref="ParrySystem"/>, R-3.3-03 to R-3.3-06): a parry, or a parry press refused for want of a charge,
/// that covers the hit. The attack's color is <see cref="ParryColors.Of(Entity, ActionDef)"/>; <c>ParryAttempted</c> is
/// published and the result goes to the defender's client as an <c>ActionResult</c> reason. A success voids the hit;
/// Reduced cuts damage and poise damage by <c>uncoloredDamageReductionPercent</c> and never flinches; a failure takes the
/// full hit.
/// (3) Damage: <see cref="DamageCalc"/> with one crit roll from the world's seeded <see cref="IRng"/>, the effect's
/// <c>damagePercent</c> times the request's <see cref="HitRequest.DamagePercent"/> as the skill multiplier, the effect's
/// element (step 5, R-3.4-07) and the damage dealt and taken stats (step 7, status modifiers) (R-3.4-04).
/// (4) HP: the damage is drained; at 0 the target dies (<see cref="Combatant.Die"/>, <see cref="ActionMachine.Kill"/>) and
/// death supersedes poise and flinch.
/// (5) Otherwise poise (<see cref="PoiseSystem"/>, R-3.3-01, R-3.1-08), then, if it did not break, flinch
/// (<see cref="FlinchSystem"/>, R-3.3-07; never for a ground area, R-3.3-09), then the effect's build-up changes
/// (<see cref="BuildUpSystem"/>, spec 3.8; a full meter triggers its status). A ground area's tick is never parried
/// (user decision 2026-10-24); dodge i-frames avoid it like any hit.
/// (6) Events in this order: <c>ParryAttempted</c>, <c>HitLanded</c>, <c>Damaged</c> (when HP was lost), <c>Staggered</c>
/// or <c>Flinched</c>, <c>Killed</c> and <c>Died</c>. Dodged and parried hits emit no <c>HitLanded</c> (TODO(open)).
/// Every landed hit (dodged and parried ones too) marks both players in combat (<see cref="Player.LastCombatAtMs"/>,
/// R-2.1-07) and leaves a <see cref="HitEntry"/> for the phase 10 hit message.
/// </para>
/// Allocation-free once its lists have grown. World simulation thread only.
/// </summary>
internal sealed class HitResolver : IHitTargetResolver
{
    private readonly CombatRules _rules;
    private readonly ActionCatalog _actions;
    private readonly EventBus? _events;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly Dictionary<string, ActionTiming> _actionsById = new(StringComparer.Ordinal);
    private readonly List<HitEntry> _notices = new(64);
    private readonly PendingHits _pending = new();
    private readonly Action<HitRequest> _land;
    private uint _landTick;

    /// <param name="actions">Turns an effect's <c>actionId</c> into the action (net id, parry colors) and judges defences.</param>
    /// <param name="clock">UTC time for <see cref="Player.LastCombatAtMs"/> (the linger window compares it with UTC).</param>
    /// <param name="rng">The world's seeded generator; one draw per damage roll.</param>
    public HitResolver(CombatRules rules, ActionCatalog actions, EventBus? events, IClock clock, IRng rng)
    {
        _rules = rules;
        _actions = actions;
        _events = events;
        _clock = clock;
        _rng = rng;
        for (int i = 0; i < actions.Count; i++)
            _actionsById[actions[i].Id] = actions[i];
        _land = request => Land(request, _landTick, onTimeline: true);
    }

    /// <summary>Hits resolved since <see cref="ClearNotices"/>, in resolve order (phase 10 sends them).</summary>
    public IReadOnlyList<HitEntry> Notices => _notices;

    /// <summary>Monsters' hits on players still waiting for the defender's input.</summary>
    public PendingHits Pending => _pending;

    /// <summary>Build-ups (spec 3.8): the build-up changes of every landed hit go there; null without build-ups.</summary>
    public BuildUpSystem? BuildUps { get; set; }

    public void ClearNotices() => _notices.Clear();

    /// <summary>The net id of the action an effect belongs to (0 when the catalog does not have it).</summary>
    public int ActionNetIdOf(ActionEffectDef effect) => _actionsById.TryGetValue(effect.ActionId, out var action) ? action.NetId : 0;

    public bool CanHit(Entity attacker, Entity target)
    {
        if (attacker == target || attacker.Combatant is not { IsAlive: true } source || target.Combatant is not { IsAlive: true } victim)
            return false;
        if (source.Faction != victim.Faction)
            return true;
        if (attacker is Player player && target is Player other)
            return PvpRules.CanDamage(player, other, attacker.RegionIndex);
        return false; // monsters never hit monsters
    }

    public HitOutcome Resolve(in HitRequest request)
    {
        if (!CanHit(request.Attacker, request.Target))
            return HitOutcome.Ignored;
        if (request.Attacker is Monster && request.Target is Player)
        {
            _pending.Add(request); // R-3.1-03: judged on the defender's timeline once its input can have arrived
            return HitOutcome.Pending;
        }
        return Land(request, request.Tick, onTimeline: false);
    }

    /// <summary>Phase 5, after the strikes: lands the pending hits that are due at <paramref name="tick"/>, in order.</summary>
    public void ResolveDue(uint tick)
    {
        _landTick = tick;
        _pending.TakeDue(tick, _rules.LagCompensationCapTicks, _land);
    }

    /// <summary>The target left the world: its pending hits are dropped.</summary>
    public void TargetRemoved(Entity target) => _pending.Drop(target);

    private HitOutcome Land(in HitRequest request, uint tick, bool onTimeline)
    {
        var attacker = request.Attacker;
        var target = request.Target;
        if (attacker.Combatant is not { } source || target.Combatant is not { IsAlive: true } victim)
            return HitOutcome.Ignored;
        var effect = request.Effect;
        _actionsById.TryGetValue(effect.ActionId, out var attack);
        int actionNetId = attack?.NetId ?? 0;
        MarkInCombat(attacker, target);

        // (1) Dodge and (2) parry, on the defender's timeline or at once.
        ParryResult parry = 0;
        ParryColor color = ParryColor.None;
        if (TryDefence(target, request.Tick, onTimeline, out var defence, out int elapsed, out bool noCharge, out ushort sequence))
        {
            if (defence.Kind == ActionKind.Dodge && defence.HasIFramesAt(elapsed))
            {
                Notice(attacker, target, 0, crit: false, HitKind.Dodged);
                return HitOutcome.Dodged;
            }
            bool inWindow = defence.PhaseAt(elapsed) == ActionState.Active;
            // A parry is not judged against the ground: a ground area's tick lands in full (user decision 2026-10-24).
            if (defence.Kind == ActionKind.Parry && request.Source != HitSource.GroundArea && (inWindow || !noCharge))
            {
                color = attack != null ? ParryColors.Of(attacker, attack.Def) : ParryColor.None;
                parry = ParrySystem.Judge(color, victim.ArmorClass, inWindow, noCharge);
                _events?.Publish(new ParryAttempted(target.Id, attacker.Id, actionNetId, color, parry), CharacterOf(target));
                if (target is Player defender)
                    defender.ActionResults.Add(new PendingActionResult(sequence, (ushort)defence.NetId, 0, ParrySystem.ReasonFor(parry)));
                if (parry == ParryResult.Success)
                {
                    Notice(attacker, target, 0, crit: false, HitKind.Parried);
                    return HitOutcome.Parried;
                }
            }
        }
        bool reduced = parry == ParryResult.Reduced;
        int reduction = reduced ? _rules.UncoloredParryReductionPercent : 0;

        // (3) Damage (R-3.4-04, R-3.4-06); a Reduced parry is step 7.
        long nowMs = _rules.MsAt(tick);
        source.Stats.ExpireModifiers(nowMs);
        victim.Stats.ExpireModifiers(nowMs);
        int skillPercent = (int)Math.Min(DamageCalc.MaxSkillMultiplierPercent,
            (long)effect.DamagePercent * Math.Max(0, request.DamagePercent) / 100);
        var kind = effect.DamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
        // Step 5: the effect's element; step 7: damage dealt and taken (Gather). TODO: its flat damage (step 1).
        var input = DamageCalc.Gather(_rules.Config, source.Stats, source.Rank, victim.Stats, victim.Rank, kind,
            skillPercent, skillFlat: 0, _rules.Config.ElementIndex(effect.Element));
        input.ParryReductionPercent = reduction;
        var damage = DamageCalc.Compute(input, _rules.Config, _rng);

        // (4) HP.
        int lost = damage.Damage > 0 ? victim.Meters.Drain(ResourceKind.Hp, damage.Damage, nowMs) : 0;
        int hpAfter = victim.Hp(nowMs);
        bool killed = damage.Damage > 0 && hpAfter <= 0;

        // (5) Poise, then flinch, unless the hit killed. A Reduced parry cuts the poise damage and never flinches.
        bool staggered = false, flinched = false;
        int flinchMs = 0;
        if (!killed)
        {
            int poiseDamage = reduced ? ParrySystem.Reduce(effect.PoiseDamage, reduction) : effect.PoiseDamage;
            staggered = PoiseSystem.Hit(target, victim, poiseDamage, tick, _rules);
            if (!staggered && !reduced)
                flinched = FlinchSystem.TryFlinch(target, victim, effect, request.Source, tick, _rules, out flinchMs);
            // Spec 3.8: build-up changes after the dodge and parry checks; a Reduced parry keeps 100 - its reduction (R-3.8-07).
            BuildUps?.ApplyHit(attacker, target, effect.Id, 100 - reduction, tick);
        }
        else
        {
            victim.Die(tick);
            ActionMachine.Kill(target.Actions);
        }

        // (6) Events.
        if (_events != null)
        {
            long actor = CharacterOf(attacker);
            _events.Publish(new HitLanded(attacker.Id, target.Id, actionNetId), actor);
            if (lost > 0)
                _events.Publish(new Damaged(attacker.Id, target.Id, actionNetId, lost, hpAfter, damage.IsCrit), actor);
            if (staggered)
                _events.Publish(new Staggered(target.Id, attacker.Id), actor);
            if (flinched)
                _events.Publish(new Flinched(target.Id, attacker.Id, flinchMs), actor);
            if (killed)
            {
                _events.Publish(new Killed(attacker.Id, target.Id, CharacterOf(target), target is Monster ? target.DefId : 0), actor);
                _events.Publish(new Died(target.Id, attacker.Id), CharacterOf(target));
            }
        }

        Notice(attacker, target, lost, damage.IsCrit, killed ? HitKind.Killed : HitKind.Damaged);
        return killed ? HitOutcome.Killed : HitOutcome.Damaged;
    }

    /// <summary>
    /// The dodge or parry that covers a hit of <paramref name="hitTick"/>: on a player's timeline for a pending hit, else
    /// the action the target's machine runs now.
    /// </summary>
    private bool TryDefence(Entity target, uint hitTick, bool onTimeline, out ActionTiming defence, out int elapsed,
        out bool noCharge, out ushort sequence)
    {
        if (onTimeline && target is Player player && target.Actions.Catalog == _actions)
        {
            if (player.Defence.TryJudge(hitTick, _actions, out var start, out elapsed))
            {
                defence = _actions[start.ActionIndex];
                noCharge = start.NoCharge;
                sequence = start.Sequence;
                return true;
            }
        }
        else if (target.Actions.Action is { } running)
        {
            defence = running;
            elapsed = target.Actions.Elapsed;
            noCharge = false;
            sequence = target is Player p ? p.LastInputSequence : (ushort)0;
            return running.Kind == ActionKind.Dodge || running.Kind == ActionKind.Parry;
        }
        defence = null!;
        elapsed = 0;
        noCharge = false;
        sequence = 0;
        return false;
    }

    /// <summary>
    /// Phase 6: one periodic tick of a status's damage (spec 3.7, user decision 2026-10-23). Not a hit: no dodge, parry,
    /// poise or flinch, never a crit (<paramref name="input"/> has the applier's side captured when the status was applied;
    /// the target's defense, resistance and damage taken are read now). Death and its events are credited to the applier
    /// (R-3.8-09), which may have died or left since. The target is marked in combat.
    /// </summary>
    public HitOutcome DamageOverTime(uint sourceId, long sourceCharacterId, Entity target, DamageInput input, DamageKind kind,
        int element, uint tick)
    {
        if (target.Combatant is not { IsAlive: true } victim)
            return HitOutcome.Ignored;
        long nowMs = _rules.MsAt(tick);
        var config = _rules.Config;
        var ids = config.Ids;
        input.Defense = victim.Stats.Get(kind == DamageKind.Magic ? ids.Mdef : ids.Def);
        input.DefenderRank = victim.Rank;
        input.Element = element;
        input.ResistPercent = element == DamageCalc.NoElement ? 0 : victim.Stats.Get(config.ElementResistStat(element));
        input.ExtraPercent = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)input.ExtraPercent + victim.Stats.Get(ids.DamageTakenPercent)));
        input.NoCrit = true;
        var damage = DamageCalc.Compute(input, config, _rng);
        int lost = damage.Damage > 0 ? victim.Meters.Drain(ResourceKind.Hp, damage.Damage, nowMs) : 0;
        int hpAfter = victim.Hp(nowMs);
        bool killed = damage.Damage > 0 && hpAfter <= 0;
        if (killed)
        {
            victim.Die(tick);
            ActionMachine.Kill(target.Actions);
        }
        if (target is Player defender)
            defender.LastCombatAtMs = _clock.UtcNowMs;
        if (_events != null)
        {
            if (lost > 0)
                _events.Publish(new Damaged(sourceId, target.Id, 0, lost, hpAfter, false), sourceCharacterId);
            if (killed)
            {
                _events.Publish(new Killed(sourceId, target.Id, CharacterOf(target), target is Monster ? target.DefId : 0), sourceCharacterId);
                _events.Publish(new Died(target.Id, sourceId), CharacterOf(target));
            }
        }
        Notice(sourceId, target.Id, lost, crit: false, killed ? HitKind.Killed : HitKind.Damaged);
        return killed ? HitOutcome.Killed : HitOutcome.Damaged;
    }

    /// <summary>A heal for the phase 10 hit message (heal numbers, spec 9): the HP gained.</summary>
    public void NoticeHeal(uint sourceId, uint targetId, int amount) => Notice(sourceId, targetId, amount, crit: false, HitKind.Healed);

    private void MarkInCombat(Entity attacker, Entity target)
    {
        long now = _clock.UtcNowMs;
        if (attacker is Player a)
            a.LastCombatAtMs = now;
        if (target is Player t)
            t.LastCombatAtMs = now;
    }

    private void Notice(Entity attacker, Entity target, int amount, bool crit, HitKind kind) =>
        Notice(attacker.Id, target.Id, amount, crit, kind);

    private void Notice(uint attackerId, uint targetId, int amount, bool crit, HitKind kind) =>
        _notices.Add(new HitEntry { AttackerId = attackerId, TargetId = targetId, Amount = amount, Crit = crit, Kind = kind });

    private static long CharacterOf(Entity entity) => entity is Player player ? player.CharacterId : 0;
}
