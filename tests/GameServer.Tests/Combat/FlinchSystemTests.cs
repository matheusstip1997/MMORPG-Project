using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace GameServer.Tests.Combat;

/// <summary>R-3.3-07 flinch (and R-3.3-09) on the repository's tables at 20 Hz.</summary>
public class FlinchSystemTests
{
    private const uint Tick = 100;
    private static readonly FiredEffect[] Fired = new FiredEffect[ActionMachine.MaxFiredPerStep];

    private static ActionEffectDef Strike(int power, int flinchMs = 300) => new()
    {
        Id = "test.strike", ActionId = "attack.warrior", Kind = EffectKind.Strike, Phase = ActionPhase.Active,
        PoiseDamage = 10, FlinchPower = power, FlinchMs = flinchMs, DamagePercent = 100, DamageKind = EffectDamageKind.Physical,
    };

    private static bool Flinch(HitFixture f, Entity target, ActionEffectDef effect, HitSource source = HitSource.Strike) =>
        FlinchSystem.TryFlinch(target, target.Combatant!, effect, source, Tick, f.Rules, out _);

    private static void Press(Entity entity, InputActions actions, int steps)
    {
        for (int i = 0; i < steps; i++)
            ActionMachine.Step(entity.Actions, new ActionInput(true, actions, 0, 0, (ushort)(i + 1)), UnlimitedActionResources.Instance, Fired);
    }

    [Fact]
    public void An_idle_target_flinches_for_the_scaled_time_then_is_immune_for_its_immunity_after_it()
    {
        var f = new HitFixture();
        var boss = f.Monster(1, monsterId: TestCombat.BossId); // resist 60, 50% duration, 3000 ms immunity

        Assert.True(FlinchSystem.TryFlinch(boss, boss.Combatant!, Strike(60, flinchMs: 400), HitSource.Strike, Tick, f.Rules, out int ms));

        Assert.Equal(200, ms);
        Assert.Equal(ActionState.Flinched, boss.Actions.State);
        Assert.Equal(4, boss.Actions.InterruptTicks);
        Assert.Equal(Tick + 4 + 60, boss.Combatant!.FlinchImmuneUntilTick);
    }

    [Fact]
    public void Power_below_the_resist_does_not_flinch()
    {
        var f = new HitFixture();
        var boss = f.Monster(1, monsterId: TestCombat.BossId);

        Assert.False(Flinch(f, boss, Strike(59)));
        Assert.Equal(ActionState.Idle, boss.Actions.State);
        Assert.Equal(0u, boss.Combatant!.FlinchImmuneUntilTick);
    }

    [Fact]
    public void A_flinch_immune_target_is_not_flinched_again_until_the_immunity_ends()
    {
        var f = new HitFixture();
        var monster = f.Monster(1);
        monster.Combatant!.FlinchImmuneUntilTick = Tick + 1;

        Assert.False(Flinch(f, monster, Strike(10)));
        monster.Combatant.FlinchImmuneUntilTick = Tick;
        Assert.True(Flinch(f, monster, Strike(10)));
    }

    [Fact]
    public void Ground_areas_never_flinch()
    {
        var f = new HitFixture();
        var monster = f.Monster(1);
        Assert.False(Flinch(f, monster, Strike(10), HitSource.GroundArea));
        Assert.Equal(ActionState.Idle, monster.Actions.State);
    }

    [Fact]
    public void A_startup_is_cancelled_but_active_recovery_and_dodges_are_not_and_start_no_immunity()
    {
        var f = new HitFixture();

        var winding = f.Player(1);
        Press(winding, InputActions.Attack, 1); // attack.warrior Startup (300 ms)
        Assert.Equal(ActionState.Startup, winding.Actions.State);
        Assert.True(Flinch(f, winding, Strike(20)));
        Assert.Equal(ActionState.Flinched, winding.Actions.State);
        Assert.Equal(-1, winding.Actions.ActionIndex);

        var swinging = f.Player(2);
        Press(swinging, InputActions.Attack, 7); // 6 ticks of Startup, then Active
        Assert.Equal(ActionState.Active, swinging.Actions.State);
        Assert.False(Flinch(f, swinging, Strike(20)));
        Assert.Equal(ActionState.Active, swinging.Actions.State);
        Assert.Equal(0u, swinging.Combatant!.FlinchImmuneUntilTick);

        var dodging = f.Player(3);
        Press(dodging, InputActions.Dodge, 1);
        Assert.False(Flinch(f, dodging, Strike(20)));
    }

    [Fact]
    public void A_staggered_target_is_not_flinched()
    {
        var f = new HitFixture();
        var monster = f.Monster(1);
        ActionMachine.Interrupt(monster.Actions, InterruptKind.PoiseBreak, 1_000);

        Assert.False(Flinch(f, monster, Strike(10)));
        Assert.Equal(ActionState.Staggered, monster.Actions.State);
    }
}
