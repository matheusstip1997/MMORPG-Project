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
/// <see cref="StrikeResolver"/>) and projectiles (phase 4, WP5). Callers run it in a fixed order (entity id), so the
/// results and the crit rolls are deterministic (R-3.3-01, architecture rule 6).
/// <para>
/// <see cref="CanHit"/>: never itself; both sides fight and are alive; monsters never hit monsters; a player hits another
/// player only when <see cref="PvpRules.CanDamage"/> allows it (never in M1, R-6.1-01). Shapes, rewind and visibility
/// are the caller's (areas need no visibility, spec 3.1).
/// </para>
/// <para>
/// <see cref="Resolve"/>, in the order of spec 3.1:
/// (1) dodge i-frames (R-3.3-02): the target's machine in its i-frame window at resolve time ignores the hit, so no
/// damage, poise or flinch (R-3.3-07). TODO(WP6): judge it on the defender's timeline with a pending hit (R-3.1-03).
/// (2) Parry: TODO(WP6), colors, charges and <c>ParryAttempted</c> (R-3.3-03, R-3.3-04); nothing is parried yet.
/// (3) Damage: <see cref="DamageCalc"/> with one crit roll from the world's seeded <see cref="IRng"/>, the effect's
/// <c>damagePercent</c> times the request's <see cref="HitRequest.DamagePercent"/> as the skill multiplier (R-3.4-04).
/// No element yet (TODO(D6)).
/// (4) HP: the damage is drained; at 0 the target dies (<see cref="Combatant.Die"/>, <see cref="ActionMachine.Kill"/>) and
/// death supersedes poise and flinch.
/// (5) Otherwise poise (<see cref="PoiseSystem"/>, R-3.3-01, R-3.1-08), then, if it did not break, flinch
/// (<see cref="FlinchSystem"/>, R-3.3-07).
/// (6) Events in this order: <c>HitLanded</c>, <c>Damaged</c> (when HP was lost), <c>Staggered</c> or <c>Flinched</c>,
/// <c>Killed</c> and <c>Died</c>.
/// Every resolved hit (dodged ones too) marks both players in combat (<see cref="Player.LastCombatAtMs"/>, R-2.1-07) and
/// leaves a <see cref="HitEntry"/> for the phase 10 hit message.
/// </para>
/// Allocation-free once its notice list has grown. World simulation thread only.
/// </summary>
internal sealed class HitResolver : IHitTargetResolver
{
    private readonly CombatRules _rules;
    private readonly EventBus? _events;
    private readonly IClock _clock;
    private readonly IRng _rng;
    private readonly Dictionary<string, int> _actionNetIds = new(StringComparer.Ordinal);
    private readonly List<HitEntry> _notices = new(64);

    /// <param name="actions">Turns an effect's <c>actionId</c> into the action's net id the events carry.</param>
    /// <param name="clock">UTC time for <see cref="Player.LastCombatAtMs"/> (the linger window compares it with UTC).</param>
    /// <param name="rng">The world's seeded generator; one draw per damage roll.</param>
    public HitResolver(CombatRules rules, ActionCatalog actions, EventBus? events, IClock clock, IRng rng)
    {
        _rules = rules;
        _events = events;
        _clock = clock;
        _rng = rng;
        for (int i = 0; i < actions.Count; i++)
            _actionNetIds[actions[i].Id] = actions[i].NetId;
    }

    /// <summary>Hits resolved since <see cref="ClearNotices"/>, in resolve order (phase 10 sends them).</summary>
    public IReadOnlyList<HitEntry> Notices => _notices;

    public void ClearNotices() => _notices.Clear();

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
        var attacker = request.Attacker;
        var target = request.Target;
        if (!CanHit(attacker, target))
            return HitOutcome.Ignored;
        var source = attacker.Combatant!;
        var victim = target.Combatant!;
        var effect = request.Effect;
        uint tick = request.Tick;
        MarkInCombat(attacker, target);

        // (1) Dodge i-frames (R-3.3-02).
        if (target.Actions.InIFrames)
        {
            Notice(attacker, target, 0, crit: false, HitKind.Dodged);
            return HitOutcome.Dodged;
        }

        // (2) Parry: TODO(WP6).

        // (3) Damage (R-3.4-04, R-3.4-06).
        long nowMs = _rules.MsAt(tick);
        source.Stats.ExpireModifiers(nowMs);
        victim.Stats.ExpireModifiers(nowMs);
        int skillPercent = (int)Math.Min(DamageCalc.MaxSkillMultiplierPercent,
            (long)effect.DamagePercent * Math.Max(0, request.DamagePercent) / 100);
        var kind = effect.DamageKind == EffectDamageKind.Magic ? DamageKind.Magic : DamageKind.Physical;
        // TODO(D6): the effect's element (R-3.4-04 step 5); TODO: its flat damage (step 1).
        var input = DamageCalc.Gather(_rules.Config, source.Stats, source.Rank, victim.Stats, victim.Rank, kind,
            skillPercent, skillFlat: 0, DamageCalc.NoElement);
        var damage = DamageCalc.Compute(input, _rules.Config, _rng);

        // (4) HP.
        int lost = damage.Damage > 0 ? victim.Meters.Drain(ResourceKind.Hp, damage.Damage, nowMs) : 0;
        int hpAfter = victim.Hp(nowMs);
        bool killed = damage.Damage > 0 && hpAfter <= 0;

        // (5) Poise, then flinch, unless the hit killed.
        bool staggered = false, flinched = false;
        int flinchMs = 0;
        if (!killed)
        {
            staggered = PoiseSystem.Hit(target, victim, effect.PoiseDamage, tick, _rules);
            if (!staggered)
                flinched = FlinchSystem.TryFlinch(target, victim, effect, request.Source, tick, _rules, out flinchMs);
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
            int actionId = _actionNetIds.TryGetValue(effect.ActionId, out int netId) ? netId : 0;
            _events.Publish(new HitLanded(attacker.Id, target.Id, actionId), actor);
            if (lost > 0)
                _events.Publish(new Damaged(attacker.Id, target.Id, actionId, lost, hpAfter, damage.IsCrit), actor);
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

    private void MarkInCombat(Entity attacker, Entity target)
    {
        long now = _clock.UtcNowMs;
        if (attacker is Player a)
            a.LastCombatAtMs = now;
        if (target is Player t)
            t.LastCombatAtMs = now;
    }

    private void Notice(Entity attacker, Entity target, int amount, bool crit, HitKind kind) =>
        _notices.Add(new HitEntry { AttackerId = attacker.Id, TargetId = target.Id, Amount = amount, Crit = crit, Kind = kind });

    private static long CharacterOf(Entity entity) => entity is Player player ? player.CharacterId : 0;
}
