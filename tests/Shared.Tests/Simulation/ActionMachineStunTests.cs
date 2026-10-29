using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>
/// WP-M2-9 in the shared action machine (spec 3.7 "M2 status list"): the stun interrupt (no actions or movement, through
/// super armor: user answer 2026-10-28), its wire state, the Impact effects that never fire on the action's timeline,
/// and the movement speed of the moveSpeedPercent stat (Cold's slow, R-2.2-03).
/// </summary>
public class ActionMachineStunTests
{
    private static readonly ActionCatalog Catalog = ActionTestKit.Catalog();
    private static int Index(string id) => Catalog.Find(id)!.Index;

    private static ActionMachineState Started(string id)
    {
        var state = new ActionMachineState(Catalog);
        var input = id switch
        {
            "attack" => Input(InputActions.Attack),
            "dodge" => Input(InputActions.Dodge),
            "parry" => Input(InputActions.Parry),
            _ => Input(slot: (byte)(Array.IndexOf(new[] { "skill.cut", "skill.circle", "skill.charge", "skill.armored" }, id) + 1)),
        };
        Assert.Equal(Index(id), Step(state, input).StartedIndex);
        return state;
    }

    private static void RunTo(ActionMachineState state, int elapsed)
    {
        while (state.Elapsed < elapsed)
            Step(state, Input());
    }

    [Theory]
    [InlineData("attack", 0)] // Startup
    [InlineData("attack", 6)] // Active: a flinch would be refused here
    [InlineData("attack", 9)] // Recovery
    [InlineData("dodge", 0)]
    [InlineData("skill.armored", 1)] // super armor in Startup (R-3.3-08) does not resist a stun
    [InlineData("skill.circle", 10)] // Channel
    public void A_stun_cancels_any_action_and_drops_its_effects(string id, int at)
    {
        var state = Started(id);
        RunTo(state, at);

        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Stun, 500));

        Assert.Equal((ActionState.Stunned, -1, 10), (state.State, state.ActionIndex, state.InterruptTicks));
        Assert.True(state.IsBusy); // R-2.2-02: movement ignores the keys while busy
        int fired = 0;
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        while (state.IsBusy)
            fired += ActionMachine.Step(state, Input(), UnlimitedActionResources.Instance, buffer).FiredCount;
        Assert.Equal(0, fired);
    }

    [Fact]
    public void A_stun_lasts_its_ticks_and_every_press_meanwhile_is_dropped()
    {
        var state = new ActionMachineState(Catalog);
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Stun, 500)); // 10 ticks at 20 Hz
        for (int i = 0; i < 9; i++)
        {
            var result = Step(state, Input(i % 2 == 0 ? InputActions.Attack : InputActions.Dodge, slot: (byte)(i % 3)));
            Assert.Equal((-1, ActionReason.None), (result.StartedIndex, result.Refused));
            Assert.Equal(ActionState.Stunned, state.State);
        }
        Step(state, Input());
        Assert.Equal(ActionState.Idle, state.State);
        Assert.Equal(Index("attack"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void A_flinch_is_refused_while_stunned_and_a_poise_break_or_a_new_stun_replaces_it()
    {
        var state = new ActionMachineState(Catalog);
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Stun, 500));
        Assert.False(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        Assert.Equal(ActionState.Stunned, state.State);

        Step(state, Input());
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Stun, 500)); // applied again: it starts over
        Assert.Equal((ActionState.Stunned, 0, 10), (state.State, state.Elapsed, state.InterruptTicks));

        Assert.True(ActionMachine.Interrupt(state, InterruptKind.PoiseBreak, 1500));
        Assert.Equal((ActionState.Staggered, 30), (state.State, state.InterruptTicks));
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Stun, 500)); // a stun replaces a stagger too
        Assert.Equal(ActionState.Stunned, state.State);
    }

    [Fact]
    public void A_dead_machine_takes_no_stun_and_end_stun_leaves_anything_but_a_stun_alone()
    {
        var dead = Started("attack");
        ActionMachine.Kill(dead);
        Assert.False(ActionMachine.Interrupt(dead, InterruptKind.Stun, 500));
        Assert.False(ActionMachine.EndStun(dead));
        Assert.Equal(ActionState.Dead, dead.State);

        var flinched = new ActionMachineState(Catalog);
        ActionMachine.Interrupt(flinched, InterruptKind.Flinch, 300);
        Assert.False(ActionMachine.EndStun(flinched));
        Assert.Equal(ActionState.Flinched, flinched.State);

        var stunned = new ActionMachineState(Catalog);
        ActionMachine.Interrupt(stunned, InterruptKind.Stun, 500);
        Step(stunned, Input());
        Assert.True(ActionMachine.EndStun(stunned)); // the status ended early (removed, or a leash reset)
        Assert.Equal((ActionState.Idle, 0, 0), (stunned.State, stunned.Elapsed, stunned.InterruptTicks));
        Assert.Equal(Index("attack"), Step(stunned, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void A_rebase_onto_a_server_stun_continues_like_the_server_and_keeps_the_cancelled_cooldown()
    {
        // The server started skill.cut, then a stun cancelled it; the client predicted the skill.
        var server = Started("skill.cut");
        Step(server, Input());
        ActionMachine.Interrupt(server, InterruptKind.Stun, 500);
        Step(server, Input());
        var client = Started("skill.cut");
        RunTo(client, 2);

        ActionMachine.Rebase(client, ActionMachine.ToWire(server), server.Facing);

        Assert.Equal(ActionMachine.ToWire(server), ActionMachine.ToWire(client));
        Assert.True(client.CooldownTicks(Index("skill.cut")) > 0); // the server started it: the cooldown stays
        for (int i = 0; i < 12; i++)
        {
            Step(server, Input());
            Step(client, Input());
            Assert.Equal(ActionMachine.ToWire(server), ActionMachine.ToWire(client));
        }
        Assert.Equal(ActionState.Idle, client.State);
    }

    [Fact]
    public void An_impact_effect_never_fires_on_the_actions_timeline()
    {
        var actions = new[] { Action("throw", 1, ActionKind.Skill, 100, 100, 100) };
        var projectile = Effect("throw.projectile", "throw", EffectKind.Projectile, ActionPhase.Active, 0);
        projectile.ImpactEffectId = "throw.explosion";
        var impact = Effect("throw.explosion", "throw", EffectKind.Impact, ActionPhase.Active, 0);
        var bindings = new ActionBindings { NormalAttack = "throw", SkillSlots = [] };
        var catalog = new ActionCatalog(TickRateHz, actions, [projectile, impact], bindings);
        var state = new ActionMachineState(catalog, catalog.Loadout(null));
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var fired = new List<string>();
        var result = ActionMachine.Step(state, Input(InputActions.Attack), UnlimitedActionResources.Instance, buffer);
        while (true)
        {
            for (int i = 0; i < result.FiredCount; i++)
                fired.Add(buffer[i].Effect.Def.Id);
            if (!state.IsBusy)
                break;
            result = ActionMachine.Step(state, Input(), UnlimitedActionResources.Instance, buffer);
        }
        Assert.Equal(["throw.projectile"], fired);
        Assert.Equal(2, catalog[0].Effects.Length); // it stays in the action, so the server finds it by id
    }

    [Fact]
    public void The_stunned_state_travels_like_an_interrupt()
    {
        Assert.Equal(Snapshot.InterruptStateBits, Snapshot.ExtraStateBits((byte)ActionState.Stunned));
        var header = new SnapshotHeader { OriginXCm = 1000, OriginYCm = 2000, OriginZCm = 0 };
        var stunned = new EntitySnapshot
        {
            EntityId = 7, XCm = 1000, YCm = 2000, State = (byte)ActionState.Stunned, StateAge = 3, StateDurationTicks = 10,
        };
        var buffer = new byte[64];
        var writer = new SnapshotWriter(buffer, header);
        Assert.True(writer.TryAddEntity(stunned));
        int length = writer.Finish();
        Assert.True(SnapshotReader.TryBegin(buffer.AsSpan(0, length), out var reader));
        Assert.True(reader.TryReadEntity(out var read));
        Assert.Equal(stunned, read);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SnapshotWriter(new byte[64], header).TryAddEntity(stunned with { StateDurationTicks = 0 }));
    }

    [Theory]
    [InlineData(400, 0, 400)]
    [InlineData(400, -30, 280)] // Cold's slow
    [InlineData(400, -90, 40)] // the stat's floor
    [InlineData(333, -30, 233)] // rounded down
    [InlineData(400, 100, 800)]
    [InlineData(400, -150, 0)] // never below 0
    public void SpeedFor_scales_the_walk_speed_by_the_percent(int baseCm, int percent, int expected) =>
        Assert.Equal(expected, MovementSim.SpeedFor(baseCm, percent));

    [Fact]
    public void Stun_steps_do_not_allocate()
    {
        var state = new ActionMachineState(Catalog);
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var input = Input(InputActions.Attack);
        ActionMachine.Interrupt(state, InterruptKind.Stun, 500);
        ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            ActionMachine.Interrupt(state, InterruptKind.Stun, 500);
            ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
            ActionMachine.EndStun(state);
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
