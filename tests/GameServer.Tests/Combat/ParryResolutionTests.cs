using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;
using Shared.Simulation.Stats;

namespace GameServer.Tests.Combat;

/// <summary>
/// Parries in the hit pipeline (spec 3.3 ParrySystem, R-3.3-03 to R-3.3-06): a monster's basic attack on a parrying
/// player, every result with its damage, poise, flinch, <c>ParryAttempted</c>, hit notice and the reason code queued for
/// the defender's client. Repository tables: the parry's window is ticks 1-4 of 10; warrior heavy, assist medium, mage
/// light armor; an uncolored parry takes 50% off.
/// </summary>
public class ParryResolutionTests
{
    private const uint Start = 20;
    private const int NoCrit = 99;
    private const ushort ParryNetId = 3;

    private sealed class Scene
    {
        public Scene(string classId = "mage", ParryColor color = ParryColor.None)
        {
            F = new HitFixture(new FixedRng(NoCrit));
            Monster = F.Monster(1);
            Player = F.Player(2, classId: classId);
            F.Catalog.Find("attack.basic")!.Def.ParryClassPve = color;
            F.Catalog.Find("skill.arrowShot")!.Def.ParryClassPve = color;
        }

        public HitFixture F { get; }
        public Monster Monster { get; }
        public Player Player { get; }

        public int Hp => Player.Combatant!.Hp(F.NowMs(Start));

        /// <summary>The player parries on <see cref="Start"/> (no view lag) and the monster hits <paramref name="ticksLater"/> after.</summary>
        public HitOutcome ParryThenHit(int ticksLater = 1, string effectId = "attack.basic.strike",
            HitSource source = HitSource.Strike)
        {
            F.Step(Player, InputActions.Parry, Start, sequence: 7);
            for (int i = 1; i <= ticksLater; i++)
                F.Step(Player, InputActions.Parry, Start + (uint)i, sequence: (ushort)(7 + i));
            return F.Hit(Monster, Player, effectId, tick: Start + (uint)ticksLater, source: source);
        }

        public PendingActionResult Result => Assert.Single(Results());

        public IEnumerable<PendingActionResult> Results()
        {
            for (int i = 0; i < Player.ActionResults.Count; i++)
                yield return Player.ActionResults[i];
        }

        public int FullDamage(int parryReduction = 0)
        {
            var a = Monster.Combatant!;
            var t = Player.Combatant!;
            var input = DamageCalc.Gather(F.Rules.Config, a.Stats, a.Rank, t.Stats, t.Rank, DamageKind.Physical, 100, 0,
                DamageCalc.NoElement);
            input.ParryReductionPercent = parryReduction;
            return DamageCalc.ComputeWithRoll(input, F.Rules.Config, NoCrit).Damage;
        }
    }

    [Theory]
    [InlineData("mage", ParryColor.Green)]
    [InlineData("assist", ParryColor.Blue)]
    [InlineData("warrior", ParryColor.Blue)]
    [InlineData("warrior", ParryColor.Yellow)]
    public void A_successful_parry_voids_the_hit_and_tells_the_defender(string classId, ParryColor color)
    {
        var s = new Scene(classId, color);
        int hp = s.Hp;

        Assert.Equal(HitOutcome.Parried, s.ParryThenHit());

        Assert.Equal(hp, s.Hp);
        Assert.Equal(s.Player.Combatant!.Poise.Max, s.Player.Combatant.Poise.Current);
        Assert.Equal(ActionState.Active, s.Player.Actions.State);
        var events = s.F.Dispatch();
        Assert.Equal(new ParryAttempted(2, 1, 1, color, ParryResult.Success), Assert.Single(events));
        Assert.Equal(s.Player.CharacterId, Assert.Single(s.F.Log.Actors));
        Assert.Equal(HitKind.Parried, Assert.Single(s.F.Resolver.Notices).Kind);
        Assert.Equal(new PendingActionResult(7, ParryNetId, 0, ActionReason.ParrySuccess), s.Result);
        Assert.Equal(s.F.Clock.UtcNowMs, s.Player.LastCombatAtMs);
    }

    [Fact]
    public void An_uncolored_parry_halves_damage_and_poise_damage_and_never_flinches()
    {
        // R-3.3-04 with the user decision of WP6: damage (step 7 of R-3.4-04) and poise damage lose 50%, and no flinch.
        var s = new Scene("mage");
        int hp = s.Hp;
        int reduced = s.FullDamage(parryReduction: 50);
        Assert.True(reduced < s.FullDamage());

        Assert.Equal(HitOutcome.Damaged, s.ParryThenHit());

        Assert.Equal(hp - reduced, s.Hp);
        Assert.Equal(100 - 5, s.Player.Combatant!.Poise.Current); // poise damage 10, halved
        s.F.Dispatch();
        Assert.Equal([typeof(ParryAttempted), typeof(HitLanded), typeof(Damaged)], s.F.Log.Kinds);
        Assert.Equal(ParryResult.Reduced, ((ParryAttempted)s.F.Log.Events[0]).Result);
        Assert.Equal(ActionReason.ParryReduced, s.Result.Reason);
    }

    [Theory]
    [InlineData("warrior", ParryColor.Red, ParryResult.FailedRed, ActionReason.ParryFailedRed)]
    [InlineData("mage", ParryColor.Yellow, ParryResult.FailedArmor, ActionReason.ParryFailedArmor)]
    [InlineData("assist", ParryColor.Yellow, ParryResult.FailedArmor, ActionReason.ParryFailedArmor)]
    [InlineData("mage", ParryColor.Blue, ParryResult.FailedArmor, ActionReason.ParryFailedArmor)]
    public void A_failed_parry_takes_the_full_hit(string classId, ParryColor color, ParryResult result, ActionReason reason)
    {
        var s = new Scene(classId, color);
        int hp = s.Hp;

        Assert.Equal(HitOutcome.Damaged, s.ParryThenHit());

        Assert.Equal(hp - s.FullDamage(), s.Hp);
        Assert.Equal(100 - 10, s.Player.Combatant!.Poise.Current);
        s.F.Dispatch();
        Assert.Equal(new ParryAttempted(2, 1, 1, color, result), s.F.Log.Events[0]);
        Assert.Equal([typeof(ParryAttempted), typeof(HitLanded), typeof(Damaged)], s.F.Log.Kinds);
        Assert.Equal(reason, s.Result.Reason);
    }

    [Theory]
    [InlineData(0, ActionState.Startup)] // pressed too late: the hit lands on the parry's first tick
    [InlineData(5, ActionState.Recovery)] // pressed too early: the window closed a tick before
    public void A_hit_outside_the_window_fails_on_timing(int ticksLater, ActionState phase)
    {
        var s = new Scene("warrior", ParryColor.Green);
        int hp = s.Hp;

        Assert.Equal(HitOutcome.Damaged, s.ParryThenHit(ticksLater));

        Assert.Equal(phase, s.F.Catalog[s.F.Catalog.ParryIndex].PhaseAt(ticksLater));
        Assert.Equal(hp - s.FullDamage(), s.Hp);
        s.F.Dispatch();
        Assert.Equal(ParryResult.FailedTiming, ((ParryAttempted)s.F.Log.Events[0]).Result);
        Assert.Equal(ActionReason.ParryFailedTiming, s.Result.Reason);
    }

    [Fact]
    public void A_parry_pressed_with_no_charge_left_reports_no_charge_and_takes_the_full_hit()
    {
        // Spend the 3 charges (R-3.3-03: each start spends one, the pool recharges one per 5 s = 100 ticks).
        var s = new Scene("warrior", ParryColor.Green);
        uint tick = Start;
        for (int parry = 0; parry < 3; parry++)
        {
            s.F.Step(s.Player, InputActions.Parry, tick++);
            for (int i = 1; i < 10; i++)
                s.F.Step(s.Player, InputActions.None, tick++);
        }
        Assert.Equal(0, s.Player.Actions.Charges(ChargePool.Parry));
        var refused = s.F.Step(s.Player, InputActions.Parry, tick, sequence: 40);
        Assert.Equal(ActionReason.ParryNoCharge, refused.Refused);
        s.F.Step(s.Player, InputActions.Parry, tick + 1, sequence: 41);
        int hp = s.Player.Combatant!.Hp(s.F.NowMs(tick + 1));

        Assert.Equal(HitOutcome.Damaged, s.F.Hit(s.Monster, s.Player, tick: tick + 1));

        Assert.Equal(hp - s.FullDamage(), s.Player.Combatant.Hp(s.F.NowMs(tick + 1)));
        s.F.Dispatch();
        Assert.Equal(new ParryAttempted(2, 1, 1, ParryColor.Green, ParryResult.NoCharge), s.F.Log.Events[0]);
        // The result echoes the refused press's input sequence.
        Assert.Equal(new PendingActionResult(40, ParryNetId, 0, ActionReason.ParryNoCharge), s.Result);
    }

    [Fact]
    public void A_refused_parry_press_outside_its_would_be_window_is_no_parry_at_all()
    {
        var s = new Scene("warrior", ParryColor.Green);
        s.Player.Defence.RefusedNoCharge(s.F.Catalog[s.F.Catalog.ParryIndex], Start, sequence: 3);

        Assert.Equal(HitOutcome.Damaged, s.F.Hit(s.Monster, s.Player, tick: Start + 6)); // its recovery

        s.F.Dispatch();
        Assert.DoesNotContain(typeof(ParryAttempted), s.F.Log.Kinds);
        Assert.Empty(s.Results());
    }

    [Fact]
    public void A_monster_projectile_goes_through_the_same_parry_path()
    {
        var s = new Scene("mage", ParryColor.Red);
        int hp = s.Hp;

        Assert.Equal(HitOutcome.Damaged, s.ParryThenHit(effectId: "skill.arrowShot.projectile", source: HitSource.Projectile));

        Assert.Equal(hp - s.FullDamage(), s.Hp);
        s.F.Dispatch();
        Assert.Equal(new ParryAttempted(2, 1, 107, ParryColor.Red, ParryResult.FailedRed), s.F.Log.Events[0]);
    }

    [Fact]
    public void A_player_attack_uses_the_pvp_color_and_a_monster_attack_the_pve_color()
    {
        var f = new HitFixture();
        var action = f.Catalog.Find("attack.basic")!;
        action.Def.ParryClassPve = ParryColor.Yellow;
        action.Def.ParryClassPvp = ParryColor.Blue;

        Assert.Equal(ParryColor.Yellow, ParryColors.Of(f.Monster(1), action));
        Assert.Equal(ParryColor.Blue, ParryColors.Of(f.Player(2), action));
    }
}
