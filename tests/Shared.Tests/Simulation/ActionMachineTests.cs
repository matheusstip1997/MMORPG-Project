using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static Shared.Tests.Simulation.ActionTestKit;

namespace Shared.Tests.Simulation;

/// <summary>Backlog D2: the shared action machine (R-3.1-01, R-3.1-07, R-3.1-09, R-3.3-07, R-3.3-08, R-2.2-02).</summary>
public class ActionMachineTests
{
    private static readonly ActionCatalog Catalog = ActionTestKit.Catalog();
    private static int Index(string id) => Catalog.Find(id)!.Index;

    private static ActionMachineState Started(string id, out ActionStepResult result)
    {
        var state = new ActionMachineState(Catalog);
        var input = id switch
        {
            "attack" => Input(InputActions.Attack),
            "dodge" => Input(InputActions.Dodge),
            "parry" => Input(InputActions.Parry),
            _ => Input(slot: (byte)(Array.IndexOf(new[] { "skill.cut", "skill.circle", "skill.charge", "skill.armored" }, id) + 1)),
        };
        result = Step(state, input);
        Assert.Equal(Index(id), result.StartedIndex);
        return state;
    }

    private static ActionMachineState Started(string id) => Started(id, out _);

    /// <summary>Steps with nothing held until <paramref name="state"/> has run <paramref name="elapsed"/> ticks.</summary>
    private static void RunTo(ActionMachineState state, int elapsed)
    {
        while (state.Elapsed < elapsed)
            Step(state, Input());
        Assert.Equal(elapsed, state.Elapsed);
    }

    [Fact]
    public void Each_phase_lasts_its_ms_rounded_up_to_whole_ticks()
    {
        var attack = Catalog.Find("attack")!; // 300 / 100 / 300 ms at 20 Hz
        Assert.Equal((6, 6, 8, 14), (attack.StartupEnd, attack.ChannelEnd, attack.ActiveEnd, attack.TotalTicks));

        var odd = new ActionCatalog(TickRateHz, [Action("odd", 1, ActionKind.Skill, 60, 30, 1)], [], null)[0];
        Assert.Equal((2, 2, 3, 4), (odd.StartupEnd, odd.ChannelEnd, odd.ActiveEnd, odd.TotalTicks)); // every phase >= 1 tick

        var circle = Catalog.Find("skill.circle")!; // 200 / channel 1000 / 100 / 300
        Assert.Equal((4, 24, 26, 32), (circle.StartupEnd, circle.ChannelEnd, circle.ActiveEnd, circle.TotalTicks));
    }

    [Fact]
    public void An_action_runs_startup_active_recovery_then_idles()
    {
        var state = Started("attack");
        var phases = new List<ActionState> { state.State };
        int endedAt = -1;
        for (int step = 1; step <= 20 && endedAt < 0; step++)
        {
            var result = Step(state, Input(InputActions.Attack)); // still held: not a new press
            phases.Add(state.State);
            if (result.EndedIndex >= 0)
                endedAt = step;
        }
        Assert.Equal(14, endedAt);
        Assert.Equal(Enumerable.Repeat(ActionState.Startup, 6)
            .Concat(Enumerable.Repeat(ActionState.Active, 2))
            .Concat(Enumerable.Repeat(ActionState.Recovery, 6))
            .Append(ActionState.Idle), phases);
        Assert.Equal(-1, state.ActionIndex);
    }

    [Fact]
    public void A_held_button_starts_one_action_and_a_new_press_starts_the_next()
    {
        var state = Started("attack");
        for (int i = 0; i < 20; i++)
            Assert.Equal(-1, Step(state, Input(InputActions.Attack)).StartedIndex);
        Assert.False(state.IsBusy);
        Step(state, Input());
        Assert.Equal(Index("attack"), Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void Without_an_input_frame_time_passes_and_nothing_is_pressed()
    {
        var state = Started("attack");
        for (int i = 0; i < 14; i++)
            Step(state, NoInput());
        Assert.False(state.IsBusy);
        // The button was still held in the last frame: the next frame holding it is not a press.
        Assert.Equal(-1, Step(state, Input(InputActions.Attack)).StartedIndex);
    }

    [Fact]
    public void A_press_outside_the_buffer_window_is_dropped_and_inside_it_fires_when_the_action_ends()
    {
        var state = Started("attack"); // 14 ticks, buffer 150 ms = 3 ticks
        RunTo(state, 9);
        var result = Step(state, Input(InputActions.Parry)); // elapsed 10: 4 ticks left
        Assert.Equal((-1, ActionReason.None, -1), (result.StartedIndex, result.Refused, state.BufferedIndex));
        Step(state, Input()); // elapsed 11: 3 ticks left
        Step(state, Input(InputActions.Parry)); // elapsed 12: 2 left, buffered
        Assert.Equal(Index("parry"), state.BufferedIndex);
        Step(state, Input(InputActions.Parry));
        result = Step(state, Input(InputActions.Parry)); // elapsed 14: the attack ends, the parry starts
        Assert.Equal((Index("attack"), Index("parry")), (result.EndedIndex, result.StartedIndex));
        Assert.Equal((ActionState.Startup, 0), (state.State, state.Elapsed));
    }

    [Fact]
    public void The_buffer_window_starts_exactly_its_ticks_before_the_end()
    {
        var state = Started("attack");
        RunTo(state, 10);
        Step(state, Input(InputActions.Parry)); // elapsed 11 = 3 ticks left: inside
        Assert.Equal(Index("parry"), state.BufferedIndex);

        state = Started("attack");
        RunTo(state, 9);
        Step(state, Input(InputActions.Parry)); // elapsed 10 = 4 ticks left: outside
        Assert.Equal(-1, state.BufferedIndex);

        var noBuffer = Started("parry"); // buffer window 0
        RunTo(noBuffer, Catalog.Find("parry")!.TotalTicks - 2);
        Step(noBuffer, Input(InputActions.Attack));
        Assert.Equal(-1, noBuffer.BufferedIndex);
    }

    [Fact]
    public void The_newest_buffered_press_wins()
    {
        var state = Started("attack");
        RunTo(state, 10);
        Step(state, Input(InputActions.Parry));
        Step(state, Input(slot: 1));
        Assert.Equal(Index("skill.cut"), state.BufferedIndex);
        RunTo(state, 13);
        Assert.Equal(Index("skill.cut"), Step(state, Input(slot: 1)).StartedIndex);
    }

    [Fact]
    public void A_cancel_works_only_from_its_phase_and_drops_the_unfired_effects()
    {
        var fired = new List<string>();
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var state = new ActionMachineState(Catalog);
        ActionStepResult Run(ActionInput input)
        {
            var result = ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
            for (int i = 0; i < result.FiredCount; i++)
                fired.Add(buffer[i].Effect.Def.Id);
            return result;
        }

        Run(Input(InputActions.Attack));
        while (state.Elapsed < 6)
            Run(Input());
        Assert.Equal(ActionState.Active, state.State);
        Assert.Equal(-1, Run(Input(InputActions.Dodge)).StartedIndex); // Active: the dodge cannot cancel yet
        Run(Input());
        Assert.Equal(ActionState.Recovery, state.State); // elapsed 8
        var result = Run(Input(InputActions.Dodge));
        Assert.Equal((Index("attack"), Index("dodge")), (result.CancelledIndex, result.StartedIndex));
        RunToIdle(state);
        Assert.Equal(["attack.strike"], fired); // attack.late (Recovery + 100 ms) never fired
    }

    [Fact]
    public void Not_cancelable_actions_keep_running()
    {
        var dodge = Started("dodge"); // cancelableBy is empty
        Step(dodge, Input(InputActions.Attack));
        Assert.Equal(Index("dodge"), dodge.ActionIndex);
    }

    [Fact]
    public void A_skill_on_cooldown_is_refused_with_a_reason()
    {
        var state = Started("skill.cut"); // 15 ticks long, cooldown 3000 ms = 60 ticks
        Assert.Equal(15, RunToIdle(state));
        var result = Step(state, Input(slot: 1, sequence: 31));
        Assert.Equal((ActionReason.OnCooldown, Index("skill.cut"), (byte)1, (ushort)31),
            (result.Refused, result.RefusedIndex, result.RefusedSlot, result.RefusedSequence));
        Assert.False(state.IsBusy);
        Assert.Equal(60 - 16, state.CooldownTicks(Index("skill.cut")));
    }

    [Fact]
    public void The_cooldown_runs_from_the_start_of_the_action()
    {
        var state = Started("skill.cut");
        for (int i = 1; i < 60; i++)
            Assert.Equal(-1, Step(state, Input()).StartedIndex);
        Assert.Equal(Index("skill.cut"), Step(state, Input(slot: 1)).StartedIndex); // the 60th step after the start
    }

    [Fact]
    public void An_empty_hotbar_slot_is_refused_as_not_learned()
    {
        var state = new ActionMachineState(Catalog);
        var result = Step(state, Input(slot: 5, sequence: 77));
        Assert.Equal((ActionReason.SkillNotLearned, -1, (byte)5, (ushort)77),
            (result.Refused, result.RefusedIndex, result.RefusedSlot, result.RefusedSequence));
        Step(state, Input());
        Assert.Equal(ActionReason.SkillNotLearned, Step(state, Input(slot: 9)).Refused);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void One_press_per_tick_by_priority_dodge_parry_skill_attack()
    {
        var all = InputActions.Attack | InputActions.Dodge | InputActions.Parry;
        Assert.Equal(Index("dodge"), Step(new ActionMachineState(Catalog), Input(all, slot: 1)).StartedIndex);
        Assert.Equal(Index("parry"), Step(new ActionMachineState(Catalog), Input(InputActions.Attack | InputActions.Parry, slot: 1)).StartedIndex);
        Assert.Equal(Index("skill.cut"), Step(new ActionMachineState(Catalog), Input(InputActions.Attack, slot: 1)).StartedIndex);
    }

    [Fact]
    public void Starting_locks_the_aim_as_facing_and_later_aims_are_ignored()
    {
        var state = new ActionMachineState(Catalog);
        Step(state, Input(InputActions.Attack, aim: 12_288));
        Assert.Equal((ushort)12_288, state.Facing);
        Step(state, Input(aim: 40_000));
        Assert.Equal((ushort)12_288, state.Facing);
    }

    [Fact]
    public void Movement_stops_and_facing_stays_locked_while_the_action_runs()
    {
        // The server's order (phase 2 then phase 3) and the client's prediction step: R-2.2-02.
        var state = new ActionMachineState(Catalog);
        var movement = new MovementState(FixVec2.Zero, 0);
        var positions = new List<FixVec2>();
        var facings = new List<ushort>();
        for (int tick = 0; tick < 25; tick++)
        {
            ushort aim = (ushort)(tick * 1000);
            var held = tick == 2 ? InputActions.Attack : InputActions.None;
            movement = MovementSim.Step(movement, new MovementInput(Shared.Simulation.MoveKeys.W, aim), state.IsBusy, 400, TickRateHz);
            var result = Step(state, Input(held, aim: aim));
            if (result.StartedIndex >= 0)
                movement = new MovementState(movement.Position, state.Facing);
            positions.Add(movement.Position);
            facings.Add(movement.Facing);
        }
        // Started on tick 2 (after that tick's move); ticks 3..16 stand still (14 ticks), tick 16 is the end tick.
        for (int tick = 3; tick <= 16; tick++)
        {
            Assert.Equal(positions[2], positions[tick]);
            Assert.Equal((ushort)2000, facings[tick]);
        }
        Assert.NotEqual(positions[16], positions[17]);
        Assert.Equal((ushort)17_000, facings[17]);
    }

    [Fact]
    public void Effects_fire_on_their_phase_offset_once()
    {
        var fired = new List<(string Id, int Elapsed)>();
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var state = new ActionMachineState(Catalog);
        var result = ActionMachine.Step(state, Input(InputActions.Attack), UnlimitedActionResources.Instance, buffer);
        while (true)
        {
            for (int i = 0; i < result.FiredCount; i++)
                fired.Add((buffer[i].Effect.Def.Id, state.Elapsed));
            if (!state.IsBusy)
                break;
            result = ActionMachine.Step(state, Input(), UnlimitedActionResources.Instance, buffer);
        }
        Assert.Equal([("attack.strike", 6), ("attack.late", 10)], fired);
    }

    [Fact]
    public void An_offset_that_rounds_past_its_phase_fires_on_the_last_tick_of_the_phase()
    {
        var catalog = new ActionCatalog(TickRateHz,
            [Action("short", 1, ActionKind.Skill, 30, 100, 100)],
            [Effect("short.late", "short", EffectKind.Heal, ActionPhase.Startup, 20)], null);
        var effect = catalog[0].Effects[0];
        Assert.Equal(0, effect.FireTick); // ceil(20 ms) is 1 tick, but Startup is only tick 0
    }

    [Fact]
    public void A_channel_completes_before_its_active_effects_fire()
    {
        var fired = new List<(string Id, ActionState Phase, int Elapsed)>();
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var state = new ActionMachineState(Catalog);
        var result = ActionMachine.Step(state, Input(slot: 2), UnlimitedActionResources.Instance, buffer);
        var phases = new HashSet<ActionState>();
        while (state.IsBusy)
        {
            phases.Add(state.State);
            result = ActionMachine.Step(state, Input(), UnlimitedActionResources.Instance, buffer);
            for (int i = 0; i < result.FiredCount; i++)
                fired.Add((buffer[i].Effect.Def.Id, state.State, state.Elapsed));
        }
        Assert.Equal(new HashSet<ActionState> { ActionState.Startup, ActionState.Channel, ActionState.Active, ActionState.Recovery }, phases);
        Assert.Equal([("skill.circle.area", ActionState.Active, 24)], fired);
    }

    [Fact]
    public void A_flinch_cancels_startup_and_channel_and_drops_their_effects()
    {
        var channel = Started("skill.circle");
        RunTo(channel, 10);
        Assert.Equal(ActionState.Channel, channel.State);
        Assert.True(ActionMachine.Interrupt(channel, InterruptKind.Flinch, 300)); // R-3.1-09
        Assert.Equal((ActionState.Flinched, -1, 6), (channel.State, channel.ActionIndex, channel.InterruptTicks));
        int fired = 0;
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        while (channel.IsBusy)
            fired += ActionMachine.Step(channel, Input(), UnlimitedActionResources.Instance, buffer).FiredCount;
        Assert.Equal(0, fired);

        var startup = Started("attack");
        RunTo(startup, 3);
        Assert.True(ActionMachine.Interrupt(startup, InterruptKind.Flinch, 300));
        Assert.Equal(6, RunToIdle(startup)); // 300 ms = 6 ticks
    }

    [Fact]
    public void A_flinch_is_ignored_in_active_recovery_dodges_super_armor_and_staggers()
    {
        var active = Started("attack");
        RunTo(active, 6);
        Assert.False(ActionMachine.Interrupt(active, InterruptKind.Flinch, 300)); // hits trade (R-3.3-07)
        RunTo(active, 9);
        Assert.False(ActionMachine.Interrupt(active, InterruptKind.Flinch, 300));
        Assert.Equal(Index("attack"), active.ActionIndex);

        var dodge = Started("dodge");
        Assert.Equal(ActionState.Startup, dodge.State);
        Assert.False(ActionMachine.Interrupt(dodge, InterruptKind.Flinch, 300)); // never a dodge

        var armored = Started("skill.armored");
        Assert.False(ActionMachine.Interrupt(armored, InterruptKind.Flinch, 300)); // R-3.3-08
        Assert.True(ActionMachine.Interrupt(armored, InterruptKind.PoiseBreak, 1000)); // a poise break still staggers
        Assert.Equal(ActionState.Staggered, armored.State);
        Assert.False(ActionMachine.Interrupt(armored, InterruptKind.Flinch, 300)); // superseded by the stagger
    }

    [Fact]
    public void A_flinch_catches_idle_and_flinched_entities()
    {
        var idle = new ActionMachineState(Catalog);
        Assert.True(ActionMachine.Interrupt(idle, InterruptKind.Flinch, 100));
        Step(idle, Input());
        Assert.True(ActionMachine.Interrupt(idle, InterruptKind.Flinch, 200)); // immunity is FlinchSystem's (D4)
        Assert.Equal((ActionState.Flinched, 0, 4), (idle.State, idle.Elapsed, idle.InterruptTicks));
        Assert.False(ActionMachine.Interrupt(idle, InterruptKind.Flinch, 0));
    }

    [Fact]
    public void A_poise_break_cancels_any_action()
    {
        foreach (var id in new[] { "attack", "dodge", "skill.circle" })
        {
            var state = Started(id);
            RunTo(state, 5);
            Assert.True(ActionMachine.Interrupt(state, InterruptKind.PoiseBreak, 1500));
            Assert.Equal((ActionState.Staggered, -1, 30), (state.State, state.ActionIndex, state.InterruptTicks));
        }
    }

    [Fact]
    public void Presses_while_flinched_are_dropped_and_the_buffer_is_forgotten()
    {
        var state = Started("attack");
        RunTo(state, 2);
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 150));
        var result = Step(state, Input(InputActions.Dodge));
        Assert.Equal((-1, ActionReason.None, -1), (result.StartedIndex, result.Refused, state.BufferedIndex));
        Step(state, Input());
        Step(state, Input());
        Assert.False(state.IsBusy);
        Assert.Equal(Index("dodge"), Step(state, Input(InputActions.Dodge)).StartedIndex);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)16_384)]
    [InlineData((ushort)32_768)]
    [InlineData((ushort)7_040)]
    [InlineData((ushort)50_048)]
    public void A_dash_moves_exactly_its_distance_along_the_locked_facing(ushort facing)
    {
        var state = new ActionMachineState(Catalog);
        var total = FixVec2.Zero;
        var moving = new List<int>();
        var result = Step(state, Input(slot: 3, aim: facing));
        for (int guard = 0; guard < 100; guard++)
        {
            if (result.DashDelta != FixVec2.Zero)
                moving.Add(state.Elapsed);
            total += result.DashDelta;
            if (!state.IsBusy)
                break;
            result = Step(state, Input(aim: (ushort)(facing + 9_000))); // later aims do not steer the dash
        }
        Assert.Equal(FixedTrig.Along(facing, FixedPoint.FromCm(500)), total);
        Assert.Equal([2, 3, 4, 5, 6, 7], moving); // 100 ms into Startup, 300 ms = 6 ticks
    }

    [Fact]
    public void A_cancelled_dash_stops_moving()
    {
        var state = new ActionMachineState(Catalog);
        Step(state, Input(slot: 3));
        Step(state, Input());
        Step(state, Input()); // first dash step
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.Flinch, 300));
        for (int i = 0; i < 10; i++)
            Assert.Equal(FixVec2.Zero, Step(state, Input()).DashDelta);
    }

    [Fact]
    public void The_same_inputs_give_the_same_states()
    {
        var a = new ActionMachineState(Catalog);
        var b = new ActionMachineState(Catalog);
        var random = new Random(4242);
        var bufferA = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var bufferB = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var copy = new ActionMachineState(Catalog);
        for (int tick = 0; tick < 5_000; tick++)
        {
            var input = RandomInput(random);
            if (tick == 2_500)
                copy.CopyFrom(a);
            var ra = ActionMachine.Step(a, input, UnlimitedActionResources.Instance, bufferA);
            var rb = ActionMachine.Step(b, input, UnlimitedActionResources.Instance, bufferB);
            if (random.Next(40) == 0)
            {
                var kind = random.Next(2) == 0 ? InterruptKind.Flinch : InterruptKind.PoiseBreak;
                int ms = random.Next(0, 1000);
                Assert.Equal(ActionMachine.Interrupt(a, kind, ms), ActionMachine.Interrupt(b, kind, ms));
            }
            Assert.True(a.SameAs(b), $"tick {tick}: {a} vs {b}");
            Assert.Equal((ra.StartedIndex, ra.Refused, ra.DashDelta, ra.FiredCount), (rb.StartedIndex, rb.Refused, rb.DashDelta, rb.FiredCount));
        }
        Assert.False(copy.SameAs(a));
    }

    [Fact]
    public void A_rebase_from_the_wire_continues_like_the_server()
    {
        var random = new Random(77);
        var server = new ActionMachineState(Catalog);
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        int compared = 0;
        for (int tick = 0; tick < 3_000; tick++)
        {
            ActionMachine.Step(server, RandomInput(random), UnlimitedActionResources.Instance, buffer);
            if (random.Next(50) == 0)
                ActionMachine.Interrupt(server, InterruptKind.Flinch, 400);
            // A buffered press is not on the wire (the client keeps its own), so compare states without one.
            if (!server.IsBusy || server.Elapsed >= byte.MaxValue || server.BufferedIndex >= 0 || tick % 7 != 0)
                continue;

            // A client that predicted nothing takes the server's state and plays it out the same way.
            var client = new ActionMachineState(Catalog);
            client.CopyFrom(server);
            client.Reset();
            ActionMachine.Rebase(client, ActionMachine.ToWire(server), server.Facing);
            Assert.Equal(ActionMachine.ToWire(server), ActionMachine.ToWire(client));
            var serverCopy = new ActionMachineState(Catalog);
            serverCopy.CopyFrom(server);
            for (int i = 0; i < 40; i++)
            {
                var r1 = ActionMachine.Step(serverCopy, NoInput(), UnlimitedActionResources.Instance, buffer);
                var r2 = ActionMachine.Step(client, NoInput(), UnlimitedActionResources.Instance, buffer);
                Assert.Equal(ActionMachine.ToWire(serverCopy), ActionMachine.ToWire(client));
                Assert.Equal((r1.DashDelta, r1.FiredCount), (r2.DashDelta, r2.FiredCount));
            }
            compared++;
        }
        Assert.True(compared > 50);
    }

    [Fact]
    public void The_wire_caps_age_and_duration()
    {
        var state = Started("skill.circle");
        Assert.Equal(new ActionWireState((byte)ActionState.Startup, 102, 0, 0), ActionMachine.ToWire(state));
        Assert.True(ActionMachine.Interrupt(state, InterruptKind.PoiseBreak, 60_000));
        for (int i = 0; i < 300; i++)
            Step(state, Input());
        Assert.Equal(new ActionWireState((byte)ActionState.Staggered, 0, 255, 255), ActionMachine.ToWire(state));
    }

    [Fact]
    public void The_empty_catalog_does_nothing()
    {
        var state = new ActionMachineState(ActionCatalog.Empty);
        var result = Step(state, Input(InputActions.Attack | InputActions.Dodge, slot: 3));
        Assert.Equal((-1, ActionReason.None), (result.StartedIndex, result.Refused));
        Assert.False(state.IsBusy);
    }

    [Fact]
    public void Steps_do_not_allocate()
    {
        var state = new ActionMachineState(Catalog);
        var buffer = new FiredEffect[ActionMachine.MaxFiredPerStep];
        var random = new Random(1);
        var inputs = Enumerable.Range(0, 2_000).Select(_ => RandomInput(random)).ToArray();
        ActionMachine.Step(state, inputs[0], UnlimitedActionResources.Instance, buffer);
        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var input in inputs)
            ActionMachine.Step(state, input, UnlimitedActionResources.Instance, buffer);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    private static ActionInput RandomInput(Random random)
    {
        var held = (InputActions)(random.Next(16) & random.Next(16) & 7); // mostly nothing held
        byte slot = random.Next(5) == 0 ? (byte)random.Next(0, 7) : (byte)0;
        return new ActionInput(random.Next(10) != 0, held, slot, (ushort)(random.Next(1024) << 6), (ushort)random.Next(65536));
    }
}
