using System;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Shared.Simulation.Actions
{
    /// <summary>The part of one tick's input the <see cref="ActionMachine"/> reads.</summary>
    public readonly struct ActionInput
    {
        /// <param name="hasInput">False when no input frame was applied this tick (the server never invents frames).</param>
        /// <param name="aim">Facing an action starting this tick locks: the input facing, or the current facing without input.</param>
        public ActionInput(bool hasInput, InputActions held, byte skillSlot, ushort aim, ushort sequence)
        {
            HasInput = hasInput;
            Held = held;
            SkillSlot = skillSlot;
            Aim = aim;
            Sequence = sequence;
        }

        public bool HasInput { get; }
        public InputActions Held { get; }
        public byte SkillSlot { get; }
        public ushort Aim { get; }
        /// <summary>Input frame sequence, echoed in refusals.</summary>
        public ushort Sequence { get; }
    }

    /// <summary>An effect that fired this tick (R-3.1-07): what D3, D4 and D6 resolve, and what visuals show.</summary>
    public readonly struct FiredEffect
    {
        public FiredEffect(ActionTiming action, int effectIndex)
        {
            Action = action;
            EffectIndex = effectIndex;
        }

        public ActionTiming Action { get; }
        public int EffectIndex { get; }
        public EffectTiming Effect => Action.Effects[EffectIndex];
        public EffectKind Kind => Effect.Kind;
    }

    /// <summary>What one <see cref="ActionMachine.Step"/> did.</summary>
    public struct ActionStepResult
    {
        /// <summary>Catalog index of the action that started this tick, or -1. The caller locks the entity's facing to it.</summary>
        public int StartedIndex;
        /// <summary>Catalog index of the action a start cut short (a cancel, R-3.1-01), or -1.</summary>
        public int CancelledIndex;
        /// <summary>Catalog index of the action that ran to its end this tick, or -1.</summary>
        public int EndedIndex;
        /// <summary>Why a press was refused, or <see cref="ActionReason.None"/>.</summary>
        public ActionReason Refused;
        /// <summary>The refused action (catalog index, -1 for an empty hotbar slot).</summary>
        public int RefusedIndex;
        public ushort RefusedSequence;
        public byte RefusedSlot;
        /// <summary>Dash movement this tick (Q24.8 cm), before collision. The caller moves the entity.</summary>
        public FixVec2 DashDelta;
        /// <summary>Effects written to the caller's buffer.</summary>
        public int FiredCount;
        /// <summary>Effects that fired but did not fit the buffer (sized too small: a bug).</summary>
        public int FiredDropped;

        public static ActionStepResult None => new ActionStepResult
        {
            StartedIndex = -1, CancelledIndex = -1, EndedIndex = -1, RefusedIndex = -1,
        };
    }

    /// <summary>
    /// The action fields of a snapshot entity (<see cref="EntitySnapshot.State"/>, <see cref="EntitySnapshot.ActionId"/>,
    /// <see cref="EntitySnapshot.StateAge"/>, <see cref="EntitySnapshot.StateDurationTicks"/>).
    /// </summary>
    public readonly struct ActionWireState : IEquatable<ActionWireState>
    {
        public ActionWireState(byte state, ushort actionId, byte age, byte durationTicks)
        {
            State = state;
            ActionId = actionId;
            Age = age;
            DurationTicks = durationTicks;
        }

        public static ActionWireState From(in EntitySnapshot entity) =>
            new ActionWireState(entity.State, entity.ActionId, entity.StateAge, entity.StateDurationTicks);

        public byte State { get; }
        public ushort ActionId { get; }
        public byte Age { get; }
        public byte DurationTicks { get; }

        public bool Equals(ActionWireState other) =>
            State == other.State && ActionId == other.ActionId && Age == other.Age && DurationTicks == other.DurationTicks;

        public override bool Equals(object? obj) => obj is ActionWireState other && Equals(other);
        public override int GetHashCode() => (State << 24) ^ (ActionId << 12) ^ (Age << 8) ^ DurationTicks;
        public override string ToString() => $"state {State} action {ActionId} age {Age} duration {DurationTicks}";
    }

    /// <summary>How an interrupt is meant (R-3.3-01, R-3.3-07).</summary>
    public enum InterruptKind : byte
    {
        Flinch = 1,
        PoiseBreak = 2,
    }

    /// <summary>
    /// The action state machine of spec 3.1 (backlog D2), shared by server phase 3 and client prediction so both run
    /// the same code on the same inputs (architecture rules 6, 7): integer ticks, a fixed order of operations, no
    /// allocation. One <see cref="Step"/> per entity per tick, in entity-id order.
    /// <para>
    /// Order inside a step: (1) cooldowns count down; (2) the running action or interrupt advances one tick and ends when
    /// its time is up; (3) a press buffered during the action fires when it has ended; (4) this tick's press: new presses
    /// are buttons held now and not in the previous input, one per tick by priority dodge, parry, hotbar skill, attack
    /// (the others are dropped). When idle it starts; during an action it cancels the action if the action's
    /// <c>cancelableBy</c> allows it from the current phase, is buffered when the action is within its buffer window of
    /// the end (the newest press wins), and is dropped otherwise; while flinched or staggered it is dropped. (5) The
    /// running action's effects due this tick fire, and a dash moves the entity.
    /// </para>
    /// <para>
    /// Starting an action locks the facing to the input's aim (R-3.1-01, R-2.2-02); while the machine is busy,
    /// <see cref="MovementSim"/> ignores move keys and facing. A refusal (cooldown, empty slot) is reported with a reason
    /// code for the client (spec 9.7).
    /// </para>
    /// TODO(D4): stamina, dodge and parry charges (refusals NoStamina, DodgeNoCharge, ParryNoCharge), i-frames and parry
    /// windows. TODO(D3): Strike and Projectile effects go to HitResolver and ProjectileSystem. TODO(D6): Heal, status and
    /// ground effects. TODO(D5): attack and cast speed scale the durations (R-3.1-02).
    /// </summary>
    public static class ActionMachine
    {
        /// <summary>Most effects one step can fire: only the running action fires, and a cancelled one fires nothing.</summary>
        public const int MaxFiredPerStep = ActionEffectDef.MaxEffectsPerAction;

        public static ActionStepResult Step(ActionMachineState state, in ActionInput input, Span<FiredEffect> fired)
        {
            var catalog = state.Catalog;
            var result = ActionStepResult.None;

            // (1) Cooldowns.
            var cooldowns = state.Cooldowns;
            for (int i = 0; i < cooldowns.Length; i++)
            {
                if (cooldowns[i] > 0)
                    cooldowns[i]--;
            }

            // (2) Time passes.
            int buffered = -1;
            ushort bufferedSequence = 0;
            byte bufferedSlot = 0;
            switch (state.State)
            {
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                {
                    var action = catalog[state.ActionIndex];
                    state.Elapsed++;
                    if (state.Elapsed >= action.TotalTicks)
                    {
                        result.EndedIndex = state.ActionIndex;
                        buffered = state.BufferedIndex;
                        bufferedSequence = state.BufferedSequence;
                        bufferedSlot = state.BufferedSlot;
                        state.ClearAction();
                        state.State = ActionState.Idle;
                    }
                    else
                    {
                        state.State = action.PhaseAt(state.Elapsed);
                    }
                    break;
                }
                case ActionState.Flinched:
                case ActionState.Staggered:
                    state.Elapsed++;
                    if (state.Elapsed >= state.InterruptTicks)
                    {
                        state.ClearAction();
                        state.InterruptTicks = 0;
                        state.State = ActionState.Idle;
                    }
                    break;
            }

            // (3) The buffered press fires when the action ends (spec 2.2).
            if (buffered >= 0)
                TryStart(state, buffered, bufferedSequence, bufferedSlot, input.Aim, ref result);

            // (4) This tick's press.
            if (input.HasInput)
            {
                InputActions pressed = input.Held & ~state.PreviousHeld;
                bool slotPressed = input.SkillSlot != 0 && input.SkillSlot != state.PreviousSlot;
                state.PreviousHeld = input.Held;
                state.PreviousSlot = input.SkillSlot;
                if (catalog.Count > 0)
                    HandlePress(state, input, pressed, slotPressed, ref result);
            }

            // (5) Effects of the running action, then its dash.
            if (state.ActionIndex >= 0)
                RunEffects(state, catalog[state.ActionIndex], ref result, fired);
            return result;
        }

        /// <summary>
        /// A hit interrupts the entity (called by D4 from hit resolution). A poise break staggers from any state but death.
        /// A flinch cancels an action in Startup or Channel, or catches an idle or flinched entity; it is ignored during
        /// Active and Recovery (the action finishes, so hits trade), during a dodge, in super armor phases (R-3.3-08), and
        /// while staggered (R-3.3-07). Flinch immunity and the duration scaling are the caller's (FlinchSystem, D4).
        /// Unfired effects of a cancelled action are dropped, and a buffered press is forgotten.
        /// </summary>
        /// <returns>True when the entity is now flinched or staggered.</returns>
        public static bool Interrupt(ActionMachineState state, InterruptKind kind, int durationMs)
        {
            int ticks = state.Catalog.TicksFromMs(durationMs);
            if (ticks <= 0 || state.State == ActionState.Dead)
                return false;
            if (kind == InterruptKind.Flinch)
            {
                switch (state.State)
                {
                    case ActionState.Staggered:
                    case ActionState.Active:
                    case ActionState.Recovery:
                        return false;
                    case ActionState.Startup:
                    case ActionState.Channel:
                    {
                        var action = state.Catalog[state.ActionIndex];
                        if (action.Kind == ActionKind.Dodge || action.HasSuperArmor(state.State))
                            return false;
                        break;
                    }
                }
            }
            state.ClearAction();
            state.State = kind == InterruptKind.Flinch ? ActionState.Flinched : ActionState.Staggered;
            state.InterruptTicks = ticks;
            return true;
        }

        /// <summary>The state as snapshots send it (spec 1.7): code, action wire id, age and interrupt length, each capped.</summary>
        public static ActionWireState ToWire(ActionMachineState state) => new ActionWireState(
            (byte)state.State,
            state.ActionIndex >= 0 ? (ushort)state.Catalog[state.ActionIndex].NetId : (ushort)0,
            (byte)Math.Min(state.Elapsed, byte.MaxValue),
            (byte)Math.Min(state.InterruptTicks, byte.MaxValue));

        /// <summary>
        /// Client reconciliation: takes the server's running action or interrupt from a snapshot (spec 2.2) and keeps the
        /// rest (buffered press, cooldowns, held buttons) as predicted, since snapshots do not carry it. Effects due up to
        /// the server's age count as fired; <paramref name="facing"/> is the snapshot's (locked) facing.
        /// An action the client predicted but the server is not running gets its predicted cooldown back.
        /// </summary>
        public static void Rebase(ActionMachineState state, in ActionWireState wire, ushort facing)
        {
            byte code = wire.State;
            ushort actionNetId = wire.ActionId;
            byte age = wire.Age;
            byte durationTicks = wire.DurationTicks;
            var catalog = state.Catalog;
            int predicted = state.ActionIndex;
            int predictedElapsed = state.Elapsed;
            int bufferedIndex = state.BufferedIndex;
            ushort bufferedSequence = state.BufferedSequence;
            byte bufferedSlot = state.BufferedSlot;

            state.ClearAction();
            state.InterruptTicks = 0;
            var serverState = (ActionState)code;
            switch (serverState)
            {
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                {
                    if (!catalog.TryGetIndex(actionNetId, out int index))
                    {
                        state.State = ActionState.Idle; // tables differ from the server's: nothing to predict with
                        break;
                    }
                    var action = catalog[index];
                    int elapsed = age == byte.MaxValue && predicted == index && predictedElapsed > age ? predictedElapsed : age;
                    elapsed = Math.Min(elapsed, action.TotalTicks - 1);
                    state.ActionIndex = index;
                    state.Elapsed = elapsed;
                    state.State = action.PhaseAt(elapsed);
                    state.Facing = facing;
                    for (int i = 0; i < action.Effects.Length; i++)
                    {
                        if (action.Effects[i].FireTick <= elapsed)
                            state.FiredEffects |= (byte)(1 << i);
                    }
                    state.BufferedIndex = bufferedIndex;
                    state.BufferedSequence = bufferedSequence;
                    state.BufferedSlot = bufferedSlot;
                    if (index != predicted)
                        state.Cooldowns[index] = Math.Max(state.Cooldowns[index], action.CooldownTicks - elapsed);
                    break;
                }
                case ActionState.Flinched:
                case ActionState.Staggered:
                    state.State = serverState;
                    state.Elapsed = age;
                    state.InterruptTicks = Math.Max((int)durationTicks, age + 1);
                    break;
                case ActionState.Dead:
                    state.State = ActionState.Dead;
                    break;
                default:
                    state.State = ActionState.Idle;
                    break;
            }
            if (predicted >= 0 && predicted != state.ActionIndex)
            {
                var gone = catalog[predicted];
                state.Cooldowns[predicted] = Math.Max(0, state.Cooldowns[predicted] - (gone.CooldownTicks - predictedElapsed));
            }
        }

        private static void HandlePress(ActionMachineState state, in ActionInput input, InputActions pressed, bool slotPressed,
            ref ActionStepResult result)
        {
            var catalog = state.Catalog;
            int index;
            byte slot = 0;
            if ((pressed & InputActions.Dodge) != 0 && catalog.DodgeIndex >= 0)
                index = catalog.DodgeIndex;
            else if ((pressed & InputActions.Parry) != 0 && catalog.ParryIndex >= 0)
                index = catalog.ParryIndex;
            else if (slotPressed)
            {
                slot = input.SkillSlot;
                index = catalog.SlotIndex(slot);
                if (index < 0)
                {
                    Refuse(ref result, -1, input.Sequence, slot, ActionReason.SkillNotLearned);
                    return;
                }
            }
            else if ((pressed & InputActions.Attack) != 0 && catalog.AttackIndex >= 0)
                index = catalog.AttackIndex;
            else
                return; // TODO(later): Interact (harvest, arrest) are actions too (R-3.1-01).

            switch (state.State)
            {
                case ActionState.Idle:
                    TryStart(state, index, input.Sequence, slot, input.Aim, ref result);
                    break;
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                {
                    var running = catalog[state.ActionIndex];
                    if (running.CanBeCancelledBy(index, state.State))
                    {
                        TryStart(state, index, input.Sequence, slot, input.Aim, ref result);
                    }
                    else if (running.TotalTicks - state.Elapsed <= running.BufferWindowTicks)
                    {
                        state.BufferedIndex = index;
                        state.BufferedSequence = input.Sequence;
                        state.BufferedSlot = slot;
                    }
                    break;
                }
                // Flinched, Staggered, Dead: dropped.
            }
        }

        private static void TryStart(ActionMachineState state, int index, ushort sequence, byte slot, ushort aim,
            ref ActionStepResult result)
        {
            var catalog = state.Catalog;
            if (state.Cooldowns[index] > 0)
            {
                Refuse(ref result, index, sequence, slot, ActionReason.OnCooldown);
                return;
            }
            if (state.ActionIndex >= 0)
            {
                // A cancel: the running action's unfired effects are dropped.
                result.CancelledIndex = state.ActionIndex;
            }
            var action = catalog[index];
            state.ClearAction();
            state.ActionIndex = index;
            state.Elapsed = 0;
            state.State = action.PhaseAt(0);
            state.Facing = aim;
            state.Cooldowns[index] = action.CooldownTicks;
            result.StartedIndex = index;
        }

        private static void Refuse(ref ActionStepResult result, int index, ushort sequence, byte slot, ActionReason reason)
        {
            result.Refused = reason;
            result.RefusedIndex = index;
            result.RefusedSequence = sequence;
            result.RefusedSlot = slot;
        }

        private static void RunEffects(ActionMachineState state, ActionTiming action, ref ActionStepResult result,
            Span<FiredEffect> fired)
        {
            int elapsed = state.Elapsed;
            var effects = action.Effects;
            for (int i = 0; i < effects.Length; i++)
            {
                var effect = effects[i];
                byte bit = (byte)(1 << i);
                if ((state.FiredEffects & bit) == 0 && effect.FireTick <= elapsed)
                {
                    state.FiredEffects |= bit;
                    if (result.FiredCount < fired.Length)
                        fired[result.FiredCount++] = new FiredEffect(action, i);
                    else
                        result.FiredDropped++;
                }
                if (effect.Kind == EffectKind.Dash && elapsed >= effect.FireTick && elapsed < effect.FireTick + effect.DashTicks)
                    result.DashDelta += DashStep(state.Facing, effect, elapsed - effect.FireTick);
            }
        }

        /// <summary>
        /// Movement of step <paramref name="step"/> of a dash: the difference of the cumulative offsets, so the steps add up
        /// to exactly the dash distance along the facing whatever the rounding.
        /// </summary>
        public static FixVec2 DashStep(ushort facing, in EffectTiming dash, int step)
        {
            long total = dash.DashDistanceRaw;
            int before = (int)(total * step / dash.DashTicks);
            int after = (int)(total * (step + 1) / dash.DashTicks);
            return FixedTrig.Along(facing, after) - FixedTrig.Along(facing, before);
        }
    }
}
