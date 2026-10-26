using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;
using Shared.Util;

namespace GameServer.Tests.Combat;

/// <summary>
/// The per-target pipeline of spec 3.1 (backlog D3, D4) on the repository's tables: target filter, dodge i-frames,
/// damage with a seeded crit roll (R-3.4-04), death, poise (R-3.3-01), flinch (R-3.3-07), events and hit notices.
/// </summary>
public class HitResolverTests
{
    private const uint Tick = 10;
    private const int NoCrit = 99;
    private const int Crit = 0;

    private static int ExpectedDamage(HitFixture f, Entity attacker, Entity target, int skillPercent, int roll,
        DamageKind kind = DamageKind.Physical)
    {
        var a = attacker.Combatant!;
        var t = target.Combatant!;
        var input = DamageCalc.Gather(f.Rules.Config, a.Stats, a.Rank, t.Stats, t.Rank, kind, skillPercent, 0, DamageCalc.NoElement);
        return DamageCalc.ComputeWithRoll(input, f.Rules.Config, roll).Damage;
    }

    [Fact]
    public void A_player_hitting_a_monster_deals_the_damage_formula_and_flinches_it()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var monster = f.Monster(2, yCm: 100);
        int before = monster.Combatant!.Hp(f.NowMs(Tick));
        int expected = ExpectedDamage(f, player, monster, 100, NoCrit);
        Assert.True(expected > 0);

        Assert.Equal(HitOutcome.Damaged, f.Hit(player, monster));

        Assert.Equal(before - expected, monster.Combatant.Hp(f.NowMs(Tick)));
        // Basic attack: flinch power 10 >= the demon's resist 10; 300 ms x 100% = 6 ticks; immunity after it: 1500 ms.
        Assert.Equal(ActionState.Flinched, monster.Actions.State);
        Assert.Equal(6, monster.Actions.InterruptTicks);
        Assert.Equal(Tick + 6 + 30, monster.Combatant.FlinchImmuneUntilTick);
        Assert.Equal(60 - 10, monster.Combatant.Poise.Current);

        var events = f.Dispatch();
        Assert.Equal([typeof(HitLanded), typeof(Damaged), typeof(Flinched)], f.Log.Kinds);
        Assert.Equal(new HitLanded(1, 2, 1), events[0]);
        Assert.Equal(new Damaged(1, 2, 1, expected, before - expected, false), events[1]);
        Assert.Equal(new Flinched(2, 1, 300), events[2]);
        Assert.All(f.Log.Actors, actor => Assert.Equal(player.CharacterId, actor));

        var notice = Assert.Single(f.Resolver.Notices);
        Assert.Equal(new HitEntry { AttackerId = 1, TargetId = 2, Amount = expected, Crit = false, Kind = HitKind.Damaged }, notice);
    }

    [Fact]
    public void A_crit_roll_multiplies_the_damage_and_is_reported()
    {
        var f = new HitFixture(new FixedRng(Crit));
        var player = f.Player(1);
        var monster = f.Monster(2);
        int expected = ExpectedDamage(f, player, monster, 100, Crit);
        Assert.True(expected > ExpectedDamage(f, player, monster, 100, NoCrit));

        f.Hit(player, monster);

        var damaged = Assert.Single(f.Dispatch().OfType<Damaged>());
        Assert.True(damaged.Crit);
        Assert.Equal(expected, damaged.Amount);
        Assert.True(Assert.Single(f.Resolver.Notices).Crit);
    }

    [Fact]
    public void Each_damage_roll_draws_once_from_the_seeded_generator_so_a_seed_replays_the_fight()
    {
        static List<int> Fight(ulong seed)
        {
            var f = new HitFixture(new Pcg32Rng(seed));
            var player = f.Player(1);
            var amounts = new List<int>();
            for (uint i = 0; i < 40; i++)
            {
                var monster = f.Monster(100 + i);
                f.Hit(player, monster, tick: 10 + i);
                amounts.Add(f.Resolver.Notices[^1].Amount);
            }
            return amounts;
        }

        Assert.Equal(Fight(7), Fight(7));
        var rng = new FixedRng(NoCrit);
        var counted = new HitFixture(rng);
        counted.Hit(counted.Player(1), counted.Monster(2));
        Assert.Equal(1, rng.Draws);
    }

    [Fact]
    public void The_skill_multiplier_is_the_effect_percent_times_the_request_percent()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var a = f.Monster(2);
        var b = f.Monster(3);

        f.Hit(player, a, "skill.crossCut.strike"); // 150%
        f.Hit(player, b, "skill.crossCut.strike", damagePercent: 50); // 150% x 50% = 75%, as a projectile falloff would

        Assert.Equal(ExpectedDamage(f, player, a, 150, NoCrit), f.Resolver.Notices[0].Amount);
        Assert.Equal(ExpectedDamage(f, player, b, 75, NoCrit), f.Resolver.Notices[1].Amount);
    }

    [Fact]
    public void A_magic_effect_uses_magic_attack_against_magic_defense()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var mage = f.Player(1, classId: "mage");
        var monster = f.Monster(2);
        var effect = f.Effect("attack.warrior.strike");
        var magic = new ActionEffectDef
        {
            Id = effect.Id, ActionId = effect.ActionId, Kind = effect.Kind, Phase = effect.Phase, PoiseDamage = effect.PoiseDamage,
            FlinchPower = effect.FlinchPower, FlinchMs = effect.FlinchMs, DamagePercent = 100, DamageKind = EffectDamageKind.Magic,
        };

        f.Resolver.Resolve(new HitRequest(mage, monster, magic, HitSource.Strike, Tick, 100));

        int expected = ExpectedDamage(f, mage, monster, 100, NoCrit, DamageKind.Magic);
        Assert.NotEqual(ExpectedDamage(f, mage, monster, 100, NoCrit), expected);
        Assert.Equal(expected, f.Resolver.Notices[0].Amount);
    }

    [Fact]
    public void Dodge_i_frames_ignore_the_hit_damage_poise_and_flinch()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var monster = f.Monster(1);
        var player = f.Player(2);
        // The dodge starts (Startup, 1 tick), then its i-frame window covers Active (spec 12.7: 250 ms). With no view lag
        // the defender's timeline is the server's.
        f.Step(player, InputActions.Dodge, Tick - 1);
        f.Step(player, InputActions.Dodge, Tick);
        Assert.True(player.Actions.InIFrames);
        int hp = player.Combatant!.Hp(f.NowMs(Tick));

        Assert.Equal(HitOutcome.Dodged, f.Hit(monster, player));

        Assert.Equal(hp, player.Combatant.Hp(f.NowMs(Tick)));
        Assert.Equal(player.Combatant.Poise.Max, player.Combatant.Poise.Current);
        Assert.NotEqual(ActionState.Flinched, player.Actions.State);
        Assert.Empty(f.Dispatch());
        Assert.Equal(HitKind.Dodged, Assert.Single(f.Resolver.Notices).Kind);
        Assert.Equal(f.Clock.UtcNowMs, player.LastCombatAtMs);
    }

    [Fact]
    public void The_target_filter_refuses_itself_players_in_M1_dead_targets_and_monster_on_monster()
    {
        var f = new HitFixture();
        var player = f.Player(1);
        var other = f.Player(2);
        var monster = f.Monster(3);
        var monster2 = f.Monster(4);

        Assert.True(f.Resolver.CanHit(player, monster));
        Assert.True(f.Resolver.CanHit(monster, player));
        Assert.False(f.Resolver.CanHit(player, player));
        Assert.False(f.Resolver.CanHit(player, other)); // spec 6.1 PvpRules: no first class change in M1
        Assert.False(f.Resolver.CanHit(monster, monster2));

        monster2.Combatant!.Die(1);
        Assert.False(f.Resolver.CanHit(player, monster2));
        Assert.False(f.Resolver.CanHit(monster2, player)); // a dead attacker's strike lands nowhere

        int hp = other.Combatant!.Hp(0);
        Assert.Equal(HitOutcome.Ignored, f.Hit(player, other));
        Assert.Equal(hp, other.Combatant.Hp(0));
        Assert.Empty(f.Resolver.Notices);
        Assert.Empty(f.Dispatch());
        Assert.Equal(0, player.LastCombatAtMs);
    }

    [Fact]
    public void A_lethal_hit_kills_without_poise_or_flinch_and_emits_Killed_then_Died()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var monster = f.Monster(2);
        monster.Combatant!.Meters.Set(ResourceKind.Hp, 3, f.NowMs(Tick));

        Assert.Equal(HitOutcome.Killed, f.Hit(player, monster));

        Assert.False(monster.Combatant.IsAlive);
        Assert.Equal(Tick, monster.Combatant.DiedAtTick);
        Assert.Equal(ActionState.Dead, monster.Actions.State);
        Assert.Equal(0, monster.Combatant.Hp(f.NowMs(Tick + 100))); // no regeneration on a corpse
        Assert.Equal(monster.Combatant.Poise.Max, monster.Combatant.Poise.Current);

        var events = f.Dispatch();
        Assert.Equal([typeof(HitLanded), typeof(Damaged), typeof(Killed), typeof(Died)], f.Log.Kinds);
        Assert.Equal(new Damaged(1, 2, 1, 3, 0, false), events[1]);
        Assert.Equal(new Killed(1, 2, 0, monster.DefId, monster.XCm, monster.YCm), events[2]);
        Assert.Equal(new Died(2, 1), events[3]);
        Assert.Equal([player.CharacterId, player.CharacterId, player.CharacterId, 0L], f.Log.Actors);
        Assert.Equal(new HitEntry { AttackerId = 1, TargetId = 2, Amount = 3, Kind = HitKind.Killed }, Assert.Single(f.Resolver.Notices));

        Assert.Equal(HitOutcome.Ignored, f.Hit(player, monster, tick: Tick + 1));
    }

    [Fact]
    public void A_poise_break_staggers_resets_the_bar_and_supersedes_the_flinch()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var monster = f.Monster(2);
        monster.Combatant!.Poise.Current = 10; // the basic attack deals 10 poise damage
        monster.Combatant.Poise.LastHitTick = Tick;

        f.Hit(player, monster);

        Assert.Equal(ActionState.Staggered, monster.Actions.State);
        Assert.Equal(20, monster.Actions.InterruptTicks); // staggerMs 1000
        Assert.Equal(monster.Combatant.Poise.Max, monster.Combatant.Poise.Current);
        Assert.Equal(Tick + 30, monster.Combatant.Poise.ImmuneUntilTick); // staggerImmunityMs 1500
        Assert.Equal(0u, monster.Combatant.FlinchImmuneUntilTick);
        f.Dispatch();
        Assert.Equal([typeof(HitLanded), typeof(Damaged), typeof(Staggered)], f.Log.Kinds);
        Assert.Equal(new Staggered(2, 1), f.Log.Events[2]);
    }

    [Fact]
    public void Ground_areas_deal_poise_damage_but_never_flinch()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var monster = f.Monster(2);

        f.Hit(player, monster, source: HitSource.GroundArea);

        Assert.Equal(ActionState.Idle, monster.Actions.State);
        Assert.Equal(50, monster.Combatant!.Poise.Current);
        f.Dispatch();
        Assert.DoesNotContain(typeof(Flinched), f.Log.Kinds);
    }

    [Fact]
    public void Hits_mark_both_players_in_combat_with_UTC_time()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        var monster = f.Monster(2);

        f.Hit(player, monster);
        Assert.Equal(f.Clock.UtcNowMs, player.LastCombatAtMs);

        f.Clock.Advance(5_000);
        f.Hit(monster, player, tick: Tick + 1);
        Assert.Equal(f.Clock.UtcNowMs, player.LastCombatAtMs);
    }

    [Fact]
    public void Notices_are_kept_in_resolve_order_until_cleared()
    {
        var f = new HitFixture(new FixedRng(NoCrit));
        var player = f.Player(1);
        f.Hit(player, f.Monster(3));
        f.Hit(player, f.Monster(2));

        Assert.Equal([3u, 2u], f.Resolver.Notices.Select(n => n.TargetId));
        f.Resolver.ClearNotices();
        Assert.Empty(f.Resolver.Notices);
    }
}
