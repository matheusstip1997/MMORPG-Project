#nullable enable
using System;
using System.Collections.Generic;
using Shared.Protocol;
using Shared.Simulation.Actions;

namespace Game.Gameplay.Prediction
{
    /// <summary>
    /// An entity entered a new action phase (spec 3.1, backlog D2), for visuals: animations follow the simulation's
    /// timings, never the other way round. Ticks are the clock of whoever reports: client ticks for the local player
    /// (<see cref="LocalPlayerPredictor"/>), server ticks for remote entities (<see cref="RemoteActionTracker"/>, which are
    /// drawn <c>InterpolationDelayMs</c> behind).
    /// </summary>
    public readonly struct ActionPhaseChange
    {
        public ActionPhaseChange(uint entityId, ActionState phase, ushort actionNetId, string actionId, uint actionStartTick,
            uint phaseStartTick, int phaseDurationTicks, int tickRateHz, bool isCorrection)
        {
            EntityId = entityId;
            Phase = phase;
            ActionNetId = actionNetId;
            ActionId = actionId;
            ActionStartTick = actionStartTick;
            PhaseStartTick = phaseStartTick;
            PhaseDurationTicks = phaseDurationTicks;
            TickRateHz = tickRateHz;
            IsCorrection = isCorrection;
        }

        public uint EntityId { get; }

        /// <summary>Startup, Channel, Active, Recovery for actions; Flinched, Staggered; Idle when it ends.</summary>
        public ActionState Phase { get; }

        /// <summary><c>ActionDef.NetId</c>; 0 when no action runs (idle, flinched, staggered).</summary>
        public ushort ActionNetId { get; }

        /// <summary><c>ActionDef.Id</c>, the key visuals map to animations; empty when no action runs.</summary>
        public string ActionId { get; }

        public uint ActionStartTick { get; }

        public uint PhaseStartTick { get; }

        /// <summary>Length of the phase as the simulation runs it (whole ticks); 0 for Idle.</summary>
        public int PhaseDurationTicks { get; }

        public int TickRateHz { get; }

        /// <summary>The phase length in ms as the simulation runs it (config ms rounded up to whole ticks).</summary>
        public int PhaseDurationMs => TickRateHz > 0 ? (int)((long)PhaseDurationTicks * 1000 / TickRateHz) : 0;

        /// <summary>True when a server correction, not the player's input, changed the phase (snap the animation).</summary>
        public bool IsCorrection { get; }

        public override string ToString() =>
            $"#{EntityId} {Phase} {(ActionId.Length > 0 ? ActionId : "-")} from {PhaseStartTick} for {PhaseDurationTicks} ticks";
    }

    /// <summary>An effect of the local player's action fired in prediction (R-3.1-07): for effects that only look, never to resolve.</summary>
    public readonly struct ActionEffectEvent
    {
        public ActionEffectEvent(uint entityId, in FiredEffect effect, uint tick)
        {
            EntityId = entityId;
            Effect = effect;
            Tick = tick;
        }

        public uint EntityId { get; }
        public FiredEffect Effect { get; }
        public uint Tick { get; }
    }

    /// <summary>Receives action phases and fired effects (the visuals adapt it to their presentation interface).</summary>
    public interface IActionPhaseListener
    {
        void OnActionPhase(in ActionPhaseChange change);

        void OnActionEffect(in ActionEffectEvent effect);
    }

    /// <summary>Remembers what was last reported for one entity and reports changes.</summary>
    internal struct ActionPhaseReport
    {
        private bool _any;
        private ActionState _phase;
        private int _actionIndex;
        private uint _actionStartTick;

        public void Clear() => _any = false;

        /// <summary>Reports the phase an entity is in at <paramref name="tick"/> if it differs from the last report.</summary>
        public void Update(IActionPhaseListener? listener, ActionCatalog catalog, uint entityId, ActionState phase,
            int actionIndex, int elapsed, int interruptTicks, uint tick, bool isCorrection, bool ageCapped = false)
        {
            uint actionStart = unchecked(tick - (uint)elapsed);
            // A capped age (snapshots send at most 255 ticks) says nothing new about the start of the same action.
            if (_any && _phase == phase && _actionIndex == actionIndex
                && (phase == ActionState.Idle || ageCapped || _actionStartTick == actionStart))
                return;
            _any = true;
            _phase = phase;
            _actionIndex = actionIndex;
            _actionStartTick = actionStart;
            if (listener == null)
                return;

            ushort netId = 0;
            string id = string.Empty;
            uint phaseStart = actionStart;
            int duration = 0;
            if (actionIndex >= 0)
            {
                var action = catalog[actionIndex];
                netId = (ushort)action.NetId;
                id = action.Id;
                phaseStart = unchecked(actionStart + (uint)action.PhaseStart(phase));
                duration = action.PhaseEnd(phase) - action.PhaseStart(phase);
            }
            else if (phase == ActionState.Flinched || phase == ActionState.Staggered || phase == ActionState.Stunned)
            {
                duration = interruptTicks;
            }
            else
            {
                phaseStart = tick;
            }
            listener.OnActionPhase(new ActionPhaseChange(entityId, phase, netId, id, actionStart, phaseStart, duration,
                catalog.TickRateHz, isCorrection));
        }
    }

    /// <summary>
    /// Turns the action state of remote entities in snapshots into <see cref="ActionPhaseChange"/>s (backlog D2), in
    /// server ticks. Remote actions are not simulated: the snapshot's action id and age place each entity on the
    /// action's timeline. Allocates only the first time it sees an entity.
    /// </summary>
    public sealed class RemoteActionTracker
    {
        private ActionCatalog _catalog;
        private readonly Dictionary<uint, ActionPhaseReport> _reports = new Dictionary<uint, ActionPhaseReport>();

        public RemoteActionTracker(ActionCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>Dev hot reload (spec 11.7): the reloaded timings from the next snapshot (same actions in the same order).</summary>
        public void Retune(ActionCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

        public IActionPhaseListener? Listener { get; set; }

        /// <summary>Call for each remote entity of each applied snapshot.</summary>
        public void Apply(uint serverTick, in EntitySnapshot entity)
        {
            var wire = ActionWireState.From(entity);
            var phase = (ActionState)wire.State;
            int index = -1;
            int elapsed = wire.Age;
            switch (phase)
            {
                case ActionState.Startup:
                case ActionState.Channel:
                case ActionState.Active:
                case ActionState.Recovery:
                    if (!_catalog.TryGetIndex(wire.ActionId, out index))
                    {
                        phase = ActionState.Idle; // tables differ from the server's
                        index = -1;
                    }
                    break;
                case ActionState.Flinched:
                case ActionState.Staggered:
                case ActionState.Stunned:
                    break;
                default:
                    elapsed = 0;
                    break;
            }
            _reports.TryGetValue(entity.EntityId, out var report);
            report.Update(Listener, _catalog, entity.EntityId, phase, index, elapsed, wire.DurationTicks, serverTick,
                isCorrection: false, ageCapped: wire.Age == byte.MaxValue);
            _reports[entity.EntityId] = report;
        }

        /// <summary>The entity left interest (despawn).</summary>
        public void Forget(uint entityId) => _reports.Remove(entityId);

        public void Clear() => _reports.Clear();
    }
}
