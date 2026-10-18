using System;
using Shared.Protocol;

namespace Shared.Simulation.Actions
{
    /// <summary>
    /// What an entity is doing (spec 3.1 <c>ActionMachine</c> states). The values are the wire state codes of snapshots
    /// (<see cref="EntitySnapshot.State"/>, spec 1.7); 1 to 4 equal <c>Shared.Data.Tables.ActionPhase</c>. Never renumber.
    /// Dodge and parry are actions of their own kind running the normal phases, so they need no state of their own.
    /// </summary>
    public enum ActionState : byte
    {
        Idle = 0,
        Startup = 1,
        Channel = 2,
        Active = 3,
        Recovery = 4,
        /// <summary>Small stagger (R-3.3-07), started by <see cref="ActionMachine.Interrupt"/>.</summary>
        Flinched = 5,
        /// <summary>Poise break (R-3.3-01), started by <see cref="ActionMachine.Interrupt"/>.</summary>
        Staggered = 6,
        /// <summary>Reserved for death handling (backlog D, F); nothing sets it yet.</summary>
        Dead = 7,
    }

    /// <summary>
    /// One entity's <see cref="ActionMachine"/> state. A class so it can hold per-action cooldowns without allocating per
    /// tick; prediction keeps copies with <see cref="CopyFrom"/>. Everything is in ticks of the catalog's tick rate.
    /// </summary>
    public sealed class ActionMachineState
    {
        private readonly int[] _cooldownTicks;

        public ActionMachineState(ActionCatalog catalog)
        {
            Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _cooldownTicks = new int[catalog.Count];
            Reset();
        }

        public ActionCatalog Catalog { get; }

        public ActionState State { get; internal set; }

        /// <summary>Catalog index of the running action, -1 when idle, flinched, staggered or dead.</summary>
        public int ActionIndex { get; internal set; }

        /// <summary>Ticks since the action or interrupt started (0 on the tick it started).</summary>
        public int Elapsed { get; internal set; }

        /// <summary>Length of a <see cref="ActionState.Flinched"/> or <see cref="ActionState.Staggered"/> state in ticks.</summary>
        public int InterruptTicks { get; internal set; }

        /// <summary>Facing captured when the action started (R-3.1-01); dashes move along it.</summary>
        public ushort Facing { get; internal set; }

        /// <summary>Bit per effect of the running action that has fired.</summary>
        public byte FiredEffects { get; internal set; }

        /// <summary>Catalog index of the press waiting for the action to end (spec 2.2 input buffering), or -1.</summary>
        public int BufferedIndex { get; internal set; }

        public ushort BufferedSequence { get; internal set; }

        public byte BufferedSlot { get; internal set; }

        /// <summary>Action buttons held in the last input; a press is a button held now and not then.</summary>
        public InputActions PreviousHeld { get; internal set; }

        public byte PreviousSlot { get; internal set; }

        /// <summary>Movement keys and input facing are ignored (R-2.2-02, R-3.1-01).</summary>
        public bool IsBusy => State != ActionState.Idle;

        public ActionTiming? Action => ActionIndex >= 0 ? Catalog[ActionIndex] : null;

        /// <summary>Ticks until the action with this catalog index may start again.</summary>
        public int CooldownTicks(int actionIndex) => _cooldownTicks[actionIndex];

        internal int[] Cooldowns => _cooldownTicks;

        public void Reset()
        {
            State = ActionState.Idle;
            ClearAction();
            InterruptTicks = 0;
            Facing = 0;
            PreviousHeld = InputActions.None;
            PreviousSlot = 0;
            Array.Clear(_cooldownTicks, 0, _cooldownTicks.Length);
        }

        public void CopyFrom(ActionMachineState other)
        {
            if (other.Catalog != Catalog)
                throw new ArgumentException("States of different catalogs.", nameof(other));
            State = other.State;
            ActionIndex = other.ActionIndex;
            Elapsed = other.Elapsed;
            InterruptTicks = other.InterruptTicks;
            Facing = other.Facing;
            FiredEffects = other.FiredEffects;
            BufferedIndex = other.BufferedIndex;
            BufferedSequence = other.BufferedSequence;
            BufferedSlot = other.BufferedSlot;
            PreviousHeld = other.PreviousHeld;
            PreviousSlot = other.PreviousSlot;
            Array.Copy(other._cooldownTicks, _cooldownTicks, _cooldownTicks.Length);
        }

        /// <summary>True when both would behave the same from here (tests, determinism checks).</summary>
        public bool SameAs(ActionMachineState other)
        {
            if (State != other.State || ActionIndex != other.ActionIndex || Elapsed != other.Elapsed
                || InterruptTicks != other.InterruptTicks || Facing != other.Facing || FiredEffects != other.FiredEffects
                || BufferedIndex != other.BufferedIndex || BufferedSequence != other.BufferedSequence
                || BufferedSlot != other.BufferedSlot || PreviousHeld != other.PreviousHeld || PreviousSlot != other.PreviousSlot)
                return false;
            for (int i = 0; i < _cooldownTicks.Length; i++)
            {
                if (_cooldownTicks[i] != other._cooldownTicks[i])
                    return false;
            }
            return true;
        }

        internal void ClearAction()
        {
            ActionIndex = -1;
            Elapsed = 0;
            FiredEffects = 0;
            BufferedIndex = -1;
            BufferedSequence = 0;
            BufferedSlot = 0;
        }

        public override string ToString() =>
            Action != null ? $"{State} {Action.Id} +{Elapsed}" : State == ActionState.Idle ? "Idle" : $"{State} +{Elapsed}/{InterruptTicks}";
    }
}
