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
        /// <param name="keys">Movement keys of this tick's input (none without input): a MoveInput dash goes along them.</param>
        public ActionInput(bool hasInput, InputActions held, byte skillSlot, ushort aim, ushort sequence, MoveKeys keys = MoveKeys.None)
        {
            HasInput = hasInput;
            Held = held;
            SkillSlot = skillSlot;
            Aim = aim;
            Sequence = sequence;
            Keys = keys;
            _requestedPlusOne = 0;
        }

        private ActionInput(ushort aim, int requestedAction)
            : this(false, InputActions.None, 0, aim, 0)
        {
            _requestedPlusOne = requestedAction < 0 ? 0 : requestedAction + 1;
        }

        /// <summary>
        /// Input of an entity without buttons (monster AI, backlog F): <paramref name="requestedAction"/> is the catalog index
        /// of the action to start this tick (-1 for none), aimed at <paramref name="aim"/>. It starts only from Idle, through
        /// the same cooldown, charge and stamina checks as a press; it never cancels or buffers.
        /// </summary>
        public static ActionInput Request(int requestedAction, ushort aim) => new ActionInput(aim, requestedAction);

        // Stored plus one, so default(ActionInput) requests nothing.
        private readonly int _requestedPlusOne;

        public bool HasInput { get; }
        public InputActions Held { get; }
        public byte SkillSlot { get; }
        public ushort Aim { get; }
        /// <summary>Movement keys held this tick (R-2.2-01); an action starting now keeps them for its MoveInput dashes.</summary>
        public MoveKeys Keys { get; }
        /// <summary>Input frame sequence, echoed in refusals.</summary>
        public ushort Sequence { get; }

        /// <summary>Catalog index of the action asked for with <see cref="Request"/>, or -1.</summary>
        public int RequestedAction => _requestedPlusOne - 1;
    }

    /// <summary>
    /// What an action start spends that the <see cref="ActionMachine"/> does not own: stamina and MP live in the entity's
    /// resource meters (spec 3.4), not in the machine state. Called only when an action that costs stamina or MP is about
    /// to start, after its cooldown and charge checks passed; the same call path runs on the server and in prediction.
    /// </summary>
    public interface IActionResources
    {
        /// <summary>
        /// Checks the stamina first (<see cref="ActionReason.NoStamina"/>, R-3.1-02), then the MP
        /// (<see cref="ActionReason.NoMp"/>, R-3.1-04), and spends both only when both are there: a refusal spends nothing.
        /// Costs are 0 or positive. Returns <see cref="ActionReason.None"/> when spent.
        /// </summary>
        ActionReason TrySpend(int staminaCost, int mpCost);
    }

    /// <summary>Every spend succeeds and nothing is tracked (tests, monsters, and client prediction until it has meters).</summary>
    public sealed class UnlimitedActionResources : IActionResources
    {
        public static readonly UnlimitedActionResources Instance = new UnlimitedActionResources();

        private UnlimitedActionResources()
        {
        }

        public ActionReason TrySpend(int staminaCost, int mpCost) => ActionReason.None;
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
    /// Order inside a step: (1) the machine's <see cref="ActionMachineState.Tick"/> advances and cooldowns count down;
    /// (2) the running action or interrupt advances one tick and ends when its time is up; (3) a press buffered during
    /// the action fires when it has ended; (4) this tick's press: new presses are buttons held now and not in the
    /// previous input, one per tick by priority dodge, parry, hotbar skill, attack (the others are dropped). When idle
    /// it starts; during an action it cancels the action if the action's <c>cancelableBy</c> allows it from the current
    /// phase, is buffered when the action is within its buffer window of the end (the newest press wins), and is dropped
    /// otherwise; while flinched or staggered it is dropped. (5) The running action's effects due this tick fire, and a
    /// dash moves the entity.
    /// </para>
    /// <para>
    /// Starting an action locks the facing to the input's aim (R-3.1-01, R-2.2-02); while the machine is busy,
    /// <see cref="MovementSim"/> ignores move keys and facing. A start is checked and paid in this order (backlog D4):
    /// the cooldown (refused <c>OnCooldown</c>), a charge of the action's pool (<c>DodgeNoCharge</c>, <c>ParryNoCharge</c>;
    /// R-3.3-02, R-3.3-03), then its stamina (<c>NoStamina</c>; R-3.1-02) and its MP (<c>NoMp</c>; R-3.1-04) through
    /// <see cref="IActionResources"/>; only when all pass are the stamina, the MP and the charge spent, and the action starts. A parry's charge is
    /// spent on that start, whatever the parry later does (R-3.3-03). A refusal is reported with a reason code for the
    /// client (spec 9.7) and changes nothing; a refused cancel leaves the running action going.
    /// </para>
    /// TODO(D4): parry windows and resolution, i-frames in HitResolver. TODO(D3): Strike and Projectile effects go to
    /// HitResolver and ProjectileSystem, heals and statuses in the StatusSystem (D6). TODO(WP11): ground effects. TODO(D5): attack and cast speed scale
    /// the durations (R-3.1-02).
    /// </summary>
    public static class ActionMachine
    {
        /// <summary>Most effects one step can fire: only the running action fires, and a cancelled one fires nothing.</summary>
        public const int MaxFiredPerStep = ActionEffectDef.MaxEffectsPerAction;

        /// <param name="resources">Pays the stamina of an action that starts (server: the entity's meter).</param>
        public static ActionStepResult Step(ActionMachineState state, in ActionInput input, IActionResources resources,
            Span<FiredEffect> fired)
        {
            var catalog = state.Catalog;
            var result = ActionStepResult.None;

            // (1) The machine's clock (charge meters), then cooldowns.
            state.Tick = unchecked(state.Tick + 1);
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
                TryStart(state, buffered, bufferedSequence, bufferedSlot, input.Aim, input.Keys, resources, ref result);

            // (4) This tick's press.
            if (input.HasInput)
            {
                InputActions pressed = input.Held & ~state.PreviousHeld;
                bool slotPressed = input.SkillSlot != 0 && input.SkillSlot != state.PreviousSlot;
                state.PreviousHeld = input.Held;
                state.PreviousSlot = input.SkillSlot;
                if (catalog.Count > 0)
                    HandlePress(state, input, pressed, slotPressed, resources, ref result);
            }
            else if (input.RequestedAction >= 0 && input.RequestedAction < catalog.Count && state.State == ActionState.Idle)
            {
                TryStart(state, input.RequestedAction, 0, 0, input.Aim, input.Keys, resources, ref result);
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

        /// <summary>
        /// The entity died (HitResolver, when its HP reaches 0): the running action or interrupt ends, its unfired effects
        /// are dropped, and the machine stays <see cref="ActionState.Dead"/>. A dead machine does not advance, drops every
        /// press and takes no interrupt until <see cref="Revive"/> (player respawn).
        /// </summary>
        public static void Kill(ActionMachineState state)
        {
            state.ClearAction();
            state.InterruptTicks = 0;
            state.State = ActionState.Dead;
        }

        /// <summary>
        /// The entity came back to life (player respawn, spec 3.1): a dead machine starts over as a new one (Idle, no
        /// cooldowns, full charge pools, nothing held or buffered). A machine that is not dead is left as it is.
        /// </summary>
        /// <returns>True when the machine was dead.</returns>
        public static bool Revive(ActionMachineState state)
        {
            if (state.State != ActionState.Dead)
                return false;
            state.Reset();
            return true;
        }

        /// <summary>
        /// The action tables were reloaded in dev (spec 11.7 hot reload; the server at a tick boundary, prediction when the
        /// client applies the same reload): the machine moves to <paramref name="catalog"/>, which must hold the same actions
        /// in the same order (the reload refuses anything else), so indexes and cooldowns stay valid. A running action ends
        /// like a cancel to Idle (its unfired effects are dropped, no recovery): its phases and fire ticks come from one
        /// timing, and a mix of old and new would match neither table. A flinch, a stagger or death runs on. The buffered
        /// press is forgotten. Cooldowns keep counting down, clamped to the new cooldowns; the charge pools are unchanged
        /// (their sizes are not reloaded).
        /// </summary>
        public static void Retune(ActionMachineState state, ActionCatalog catalog, ActionLoadout loadout)
        {
            if (catalog == null)
                throw new ArgumentNullException(nameof(catalog));
            if (catalog.Count != state.Catalog.Count || catalog.TickRateHz != state.Catalog.TickRateHz)
                throw new ArgumentException("A reload keeps the same actions in the same order and the tick rate.", nameof(catalog));
            switch (state.State)
            {
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                    state.ClearAction();
                    state.State = ActionState.Idle;
                    break;
                default:
                    // Elapsed counts the interrupt: keep it, forget only the press.
                    state.BufferedIndex = -1;
                    state.BufferedSequence = 0;
                    state.BufferedSlot = 0;
                    break;
            }
            state.Catalog = catalog;
            state.Loadout = loadout;
            var cooldowns = state.Cooldowns;
            for (int i = 0; i < cooldowns.Length; i++)
                cooldowns[i] = Math.Min(cooldowns[i], catalog[i].CooldownTicks);
        }

        /// <summary>The state as snapshots send it (spec 1.7): code, action wire id, age and interrupt length, each capped.</summary>
        public static ActionWireState ToWire(ActionMachineState state) => new ActionWireState(
            (byte)state.State,
            state.ActionIndex >= 0 ? (ushort)state.Catalog[state.ActionIndex].NetId : (ushort)0,
            (byte)Math.Min(state.Elapsed, byte.MaxValue),
            (byte)Math.Min(state.InterruptTicks, byte.MaxValue));

        /// <summary>
        /// Client reconciliation: takes the server's running action or interrupt from a snapshot (spec 2.2) and keeps the
        /// rest (buffered press, cooldowns, charge meters, held buttons, the machine's tick) as predicted, since snapshots
        /// do not carry it. Effects due up to the server's age count as fired; <paramref name="facing"/> is the snapshot's
        /// (locked) facing. An action the client predicted but the server is not running gets its predicted cooldown back,
        /// unless the server is flinched or staggered since the action's start: then the server started it, the interrupt
        /// cancelled it, and the cooldown stays (spec 3.1).
        /// TODO(WP2 merge): the charges are not given back or taken here (a lost dodge press leaves the predicted pool a
        /// charge short until it recharges); take the dodge and parry charges from the own-entity snapshot once it
        /// carries them (spec 9.4 <c>ChargeMeterUpdate</c>).
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
            var predictedKeys = state.StartKeys;
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
                    // Snapshots do not carry the start's movement keys; the action the client predicted started from the
                    // same input frame, so its keys are the server's. An action it did not predict dashes along the facing.
                    if (index == predicted)
                        state.StartKeys = predictedKeys;
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
            // A flinch or stagger that started no earlier than the predicted action (its age is not more than the action's
            // elapsed ticks) interrupted an action the server had started, and the server keeps its cooldown (spec 3.1,
            // QA wave 5 G2). One older than the press means the server never started it: the cooldown goes back.
            bool interruptedByServer = (serverState == ActionState.Flinched || serverState == ActionState.Staggered)
                && age <= predictedElapsed;
            if (predicted >= 0 && predicted != state.ActionIndex && !interruptedByServer)
            {
                var gone = catalog[predicted];
                state.Cooldowns[predicted] = Math.Max(0, state.Cooldowns[predicted] - (gone.CooldownTicks - predictedElapsed));
            }
        }

        private static void HandlePress(ActionMachineState state, in ActionInput input, InputActions pressed, bool slotPressed,
            IActionResources resources, ref ActionStepResult result)
        {
            var catalog = state.Catalog;
            var loadout = state.Loadout;
            int index;
            byte slot = 0;
            if ((pressed & InputActions.Dodge) != 0 && loadout.DodgeIndex >= 0)
                index = loadout.DodgeIndex;
            else if ((pressed & InputActions.Parry) != 0 && loadout.ParryIndex >= 0)
                index = loadout.ParryIndex;
            else if (slotPressed)
            {
                slot = input.SkillSlot;
                index = loadout.SlotIndex(slot);
                if (index < 0)
                {
                    Refuse(ref result, -1, input.Sequence, slot, ActionReason.SkillNotLearned);
                    return;
                }
            }
            else if ((pressed & InputActions.Attack) != 0 && loadout.AttackIndex >= 0)
                index = loadout.AttackIndex;
            else
                return; // TODO(later): Interact (harvest, arrest) are actions too (R-3.1-01).

            switch (state.State)
            {
                case ActionState.Idle:
                    TryStart(state, index, input.Sequence, slot, input.Aim, input.Keys, resources, ref result);
                    break;
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                {
                    var running = catalog[state.ActionIndex];
                    if (running.CanBeCancelledBy(index, state.State))
                    {
                        TryStart(state, index, input.Sequence, slot, input.Aim, input.Keys, resources, ref result);
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

        private static void TryStart(ActionMachineState state, int index, ushort sequence, byte slot, ushort aim, MoveKeys keys,
            IActionResources resources, ref ActionStepResult result)
        {
            var catalog = state.Catalog;
            var action = catalog[index];
            if (state.Cooldowns[index] > 0)
            {
                Refuse(ref result, index, sequence, slot, ActionReason.OnCooldown);
                return;
            }
            var pool = action.ChargePool;
            if (pool != ChargePool.None && state.Meter(pool).ChargesAt(state.Tick) <= 0)
            {
                Refuse(ref result, index, sequence, slot, pool == ChargePool.Dodge ? ActionReason.DodgeNoCharge : ActionReason.ParryNoCharge);
                return;
            }
            if (action.StaminaCost > 0 || action.MpCost > 0)
            {
                // Stamina, then MP (R-3.1-04); both or nothing.
                var refused = resources.TrySpend(action.StaminaCost, action.MpCost);
                if (refused != ActionReason.None)
                {
                    Refuse(ref result, index, sequence, slot, refused);
                    return;
                }
            }
            // R-3.3-03: the charge is spent on the start, even if the parry later fails.
            if (pool != ChargePool.None)
                state.Meter(pool).TrySpend(state.Tick);
            if (state.ActionIndex >= 0)
            {
                // A cancel: the running action's unfired effects are dropped.
                result.CancelledIndex = state.ActionIndex;
            }
            state.ClearAction();
            state.ActionIndex = index;
            state.Elapsed = 0;
            state.State = action.PhaseAt(0);
            state.Facing = aim;
            state.StartKeys = keys;
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
                    result.DashDelta += DashStep(DashHeading(effect, state.Facing, state.StartKeys), effect, elapsed - effect.FireTick);
            }
        }

        /// <summary>
        /// Where a dash goes (<see cref="DashDirection"/>): along the movement keys held at the start for a MoveInput dash
        /// with a key held, else along the locked facing turned by the dash's direction offset.
        /// </summary>
        public static ushort DashHeading(in EffectTiming dash, ushort facing, MoveKeys startKeys)
        {
            if (dash.DashDirection == DashDirection.MoveInput && TryHeadingOf(startKeys, out ushort heading))
                return heading;
            return unchecked((ushort)(facing + dash.DashAngleOffset));
        }

        /// <summary>
        /// The binary angle of the movement keys (R-2.2-01 world axes: W +Y is 0, D +X is a quarter turn clockwise); false
        /// when none are held or opposite keys cancel out.
        /// </summary>
        public static bool TryHeadingOf(MoveKeys keys, out ushort heading)
        {
            int dx = ((keys & MoveKeys.D) != 0 ? 1 : 0) - ((keys & MoveKeys.A) != 0 ? 1 : 0);
            int dy = ((keys & MoveKeys.W) != 0 ? 1 : 0) - ((keys & MoveKeys.S) != 0 ? 1 : 0);
            heading = 0;
            if (dx == 0 && dy == 0)
                return false;
            heading = FixedTrig.AngleOf(dx, dy);
            return true;
        }

        /// <summary>
        /// Movement of step <paramref name="step"/> of a dash along <paramref name="facing"/> (the locked facing plus the
        /// dash's direction offset): the difference of the cumulative offsets, so the steps add up to exactly the dash
        /// distance whatever the rounding.
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
