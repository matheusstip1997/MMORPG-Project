#nullable enable
using System;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.World;
using WireMoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Gameplay.Prediction
{
    /// <summary>What a snapshot did to the prediction.</summary>
    public enum ReconcileOutcome : byte
    {
        /// <summary>First server state: the prediction starts from it.</summary>
        Initialized,
        /// <summary>Not newer than a snapshot already applied (duplicate or reordered); ignored.</summary>
        Stale,
        /// <summary>Acknowledges a sequence this client never sent; ignored.</summary>
        UnknownAck,
        /// <summary>The prediction at the acknowledged input is within the tolerance; nothing changes.</summary>
        WithinTolerance,
        /// <summary>Rebased on the server state and replayed the unacknowledged inputs.</summary>
        Corrected,
    }

    public readonly struct ReconcileResult
    {
        public ReconcileResult(ReconcileOutcome outcome, FixVec2 shift, long errorSquaredRaw, bool actionMismatch = false)
        {
            Outcome = outcome;
            Shift = shift;
            ErrorSquaredRaw = errorSquaredRaw;
            ActionMismatch = actionMismatch;
        }

        /// <summary>The server ran another action (or phase, or age) than predicted at the acknowledged input (backlog D2).</summary>
        public bool ActionMismatch { get; }

        public ReconcileOutcome Outcome { get; }

        /// <summary>New predicted position minus the old one (Q24.8 cm); non-zero only when corrected.</summary>
        public FixVec2 Shift { get; }

        /// <summary>Squared distance between the server state and the prediction at the acknowledged input (raw units squared).</summary>
        public long ErrorSquaredRaw { get; }

        /// <summary>For display and telemetry only.</summary>
        public double ErrorCm => Math.Sqrt(ErrorSquaredRaw) / FixedPoint.RawPerCm;
    }

    /// <summary>
    /// Client prediction with reconciliation for the local player (spec 2.2, backlog D1).
    /// <list type="bullet">
    /// <item><see cref="Predict"/> makes one input frame per client tick and steps the shared <see cref="MovementSim"/>
    /// with it exactly as the server will (the server applies one frame per tick and never invents frames).</item>
    /// <item>Frames stay pending until a snapshot acknowledges them. <see cref="Reconcile"/> compares the server state
    /// with the state predicted after the acknowledged frame; only an error above
    /// <c>ClientPrediction.CorrectionToleranceCm</c> rebases on the server state and replays the frames after the ack.
    /// Within the tolerance the prediction is kept, so whole-cm snapshot rounding never causes corrections.</item>
    /// <item>Snapshots are ordered by server tick: duplicated and reordered ones are ignored, dropped ones only delay
    /// the next comparison.</item>
    /// </list>
    /// Every predicted and replayed step collides with the exported world grid, as the server's phase 2 does (backlog
    /// C3), and <see cref="HeightCm"/> is the grid height under the predicted position.
    /// Facing is not reconciled while the player is free: it comes from the player's own input every frame (R-2.2-01).
    /// <para>
    /// Actions (backlog D2): every frame also steps the shared <see cref="ActionMachine"/> exactly as server phase 3 does
    /// (movement first with the machine's state from the previous frame, then the machine, then a dash with collision), and
    /// the machine state is kept per pending frame. A snapshot whose action state (code, action, age) differs from the
    /// prediction at the acknowledged frame is a correction like a position error: the replay starts from the server's
    /// action and facing and steps actions and movement together. Phase changes and fired effects go to
    /// <see cref="Listener"/> (fired effects only when first predicted, not on replays).
    /// Stamina costs and dodge and parry charges (backlog D4) run through the same machine; the charges are predicted and
    /// stay as predicted on a rebase. TODO(WP2 merge): reconcile the charges and stamina from the own-entity snapshot.
    /// </para>
    /// TODO(D5): speed from the movement-speed stat (R-2.2-03). TODO(D4): sub-tick timestamps of presses (R-3.1-04).
    /// Plain C# with no UnityEngine API (GameServer.Tests compiles it); does not allocate after construction.
    /// </summary>
    public sealed class LocalPlayerPredictor
    {
        private readonly PredictionSettings _settings;
        private readonly WorldGrid _grid;
        private readonly long _toleranceSquaredRaw;
        private readonly InputFrame[] _frames;
        private readonly MovementState[] _statesAfter;
        private readonly ActionCatalog _catalog;
        private readonly ActionMachineState[] _actionsAfter;
        private readonly ActionMachineState _baseActions;
        private readonly ActionMachineState _actions;
        private readonly FiredEffect[] _fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
        private ActionPhaseReport _report;
        private int _head;
        private int _count;
        private MovementState _base;
        private uint _lastServerTick;
        private ushort _lastSequence;
        private uint _clientTick;

        /// <param name="grid">The world the server simulates this player in (the same exported files).</param>
        public LocalPlayerPredictor(PredictionSettings settings, WorldGrid grid)
            : this(settings, grid, ActionCatalog.Empty)
        {
        }

        /// <param name="actions">The same action tables as the server (<see cref="ActionCatalog.From"/>).</param>
        public LocalPlayerPredictor(PredictionSettings settings, WorldGrid grid, ActionCatalog actions)
        {
            _catalog = actions ?? throw new ArgumentNullException(nameof(actions));
            if (actions.Count > 0 && actions.TickRateHz != settings.TickRateHz)
                throw new ArgumentException("The action catalog was built for another tick rate.", nameof(actions));
            _actions = new ActionMachineState(actions);
            _baseActions = new ActionMachineState(actions);
            _actionsAfter = new ActionMachineState[settings.MaxPendingInputs];
            for (int i = 0; i < _actionsAfter.Length; i++)
                _actionsAfter[i] = new ActionMachineState(actions);
            _settings = settings;
            _grid = grid ?? throw new ArgumentNullException(nameof(grid));
            HeightCm = grid.BaseHeightCm;
            long toleranceRaw = (long)settings.CorrectionToleranceCm * FixedPoint.RawPerCm;
            _toleranceSquaredRaw = toleranceRaw * toleranceRaw;
            _frames = new InputFrame[settings.MaxPendingInputs];
            _statesAfter = new MovementState[settings.MaxPendingInputs];
        }

        /// <summary>False until the first snapshot gave the starting state.</summary>
        public bool HasState { get; private set; }

        /// <summary>Predicted state after the newest frame.</summary>
        public MovementState State { get; private set; }

        /// <summary>
        /// Ground height in cm under <see cref="State"/>, from the grid; kept when the position is outside the grid,
        /// like the server's height in phase 2. For rendering (the simulation is ground-plane only).
        /// </summary>
        public int HeightCm { get; private set; }

        public int PendingCount => _count;

        /// <summary>Predicted action state after the newest frame. Read only.</summary>
        public ActionMachineState Actions => _actions;

        /// <summary>Entity id reported in phase changes (the local entity, from the first snapshot entity).</summary>
        public uint EntityId { get; set; }

        /// <summary>Gets the local player's action phases and fired effects; null for none.</summary>
        public IActionPhaseListener? Listener { get; set; }

        /// <summary>Client tick of the newest frame: the tick base of the local player's phase reports.</summary>
        public uint ClientTick => _clientTick;

        /// <summary>Sequence of the newest frame made; 0 before the first.</summary>
        public ushort LastSequence => _lastSequence;

        public long Corrections { get; private set; }

        /// <summary>Pending frames dropped because the server stopped acknowledging (<c>MaxPendingInputs</c>).</summary>
        public long DroppedPending { get; private set; }

        /// <summary>Forgets everything (new connection or new entity).</summary>
        public void Reset()
        {
            HasState = false;
            State = default;
            HeightCm = _grid.BaseHeightCm;
            _base = default;
            _head = 0;
            _count = 0;
            _lastServerTick = 0;
            _lastSequence = 0;
            _clientTick = 0;
            Corrections = 0;
            DroppedPending = 0;
            _actions.Reset();
            _baseActions.Reset();
            _report.Clear();
        }

        /// <summary>
        /// Makes the next input frame from the held keys, facing, action buttons and hotbar slot (R-2.2-01), steps the
        /// prediction with it, and returns it for sending. Requires <see cref="HasState"/>.
        /// </summary>
        public InputFrame Predict(WireMoveKeys keys, ushort facing, InputActions actions = InputActions.None, byte skillSlot = 0)
        {
            if (skillSlot > InputPacket.MaxSkillSlot)
                throw new ArgumentOutOfRangeException(nameof(skillSlot));
            if (!HasState)
                throw new InvalidOperationException("Predict needs the first server state (Reconcile) first.");

            var frame = new InputFrame
            {
                Sequence = (ushort)(_lastSequence + 1),
                ClientTick = ++_clientTick,
                // TODO(D2): sub-tick timestamp of action presses (R-3.1-04); movement only needs the tick.
                SubTick = 0,
                Keys = keys,
                Facing = Quantize.RoundTripAngle(facing, InputPacket.FacingBits),
                Actions = actions,
                SkillSlot = skillSlot,
            };
            _lastSequence = frame.Sequence;

            if (_count == _frames.Length)
            {
                // Stalled: take the oldest frame as acknowledged as predicted; the next snapshot corrects if not.
                _base = _statesAfter[_head];
                _baseActions.CopyFrom(_actionsAfter[_head]);
                _head = (_head + 1) % _frames.Length;
                _count--;
                DroppedPending++;
            }

            SetState(Step(State, _actions, frame, out int fired));
            int slot = (_head + _count) % _frames.Length;
            _frames[slot] = frame;
            _statesAfter[slot] = State;
            _actionsAfter[slot].CopyFrom(_actions);
            _count++;
            ReportPhase(isCorrection: false);
            for (int i = 0; i < fired && Listener != null; i++)
                Listener.OnActionEffect(new ActionEffectEvent(EntityId, _fired[i], frame.ClientTick));
            return frame;
        }

        /// <summary>
        /// Applies the server's state of the local entity (the first entity of the snapshot, spec 1.7) with the
        /// snapshot's header, without its action state (movement only). <paramref name="serverState"/> is in simulation
        /// ground coordinates (see <c>SnapshotAxes</c>).
        /// </summary>
        public ReconcileResult Reconcile(in SnapshotHeader header, MovementState serverState) =>
            Reconcile(header, serverState, null);

        /// <summary>
        /// Applies the server's state of the local entity with its action state (<see cref="ActionWireState.From"/> of the
        /// snapshot entity).
        /// </summary>
        public ReconcileResult Reconcile(in SnapshotHeader header, MovementState serverState, ActionWireState? serverAction)
        {
            serverState = new MovementState(UnroundOffWall(serverState.Position), serverState.Facing);
            if (!HasState)
            {
                HasState = true;
                _lastServerTick = header.ServerTick;
                _base = serverState;
                _actions.Reset();
                if (serverAction.HasValue)
                    ActionMachine.Rebase(_actions, serverAction.Value, serverState.Facing);
                _baseActions.CopyFrom(_actions);
                SetState(serverState);
                ReportPhase(isCorrection: true);
                // A server that already applied frames (a reattached session) gets sequences after its ack.
                if (header.HasInputAck)
                    _lastSequence = header.LastInputSequence;
                return new ReconcileResult(ReconcileOutcome.Initialized, FixVec2.Zero, 0);
            }

            if ((int)(header.ServerTick - _lastServerTick) <= 0)
                return new ReconcileResult(ReconcileOutcome.Stale, FixVec2.Zero, 0);

            MovementState predictedAtAck = _base;
            ActionMachineState actionsAtAck = _baseActions;
            int acked = 0;
            if (header.HasInputAck)
            {
                ushort ack = header.LastInputSequence;
                if (Sequence.IsNewer(ack, _lastSequence))
                    return new ReconcileResult(ReconcileOutcome.UnknownAck, FixVec2.Zero, 0);
                while (acked < _count && !Sequence.IsNewer(_frames[(_head + acked) % _frames.Length].Sequence, ack))
                {
                    predictedAtAck = _statesAfter[(_head + acked) % _frames.Length];
                    actionsAtAck = _actionsAfter[(_head + acked) % _frames.Length];
                    acked++;
                }
            }
            // Copied before the ring slots are reused: actionsAtAck may be one of them.
            if (!ReferenceEquals(actionsAtAck, _baseActions))
                _baseActions.CopyFrom(actionsAtAck);
            _lastServerTick = header.ServerTick;
            _head = (_head + acked) % _frames.Length;
            _count -= acked;

            var error = serverState.Position - predictedAtAck.Position;
            long errorSquared = error.LengthSquared;
            bool positionOk = errorSquared <= _toleranceSquaredRaw;
            bool actionOk = !serverAction.HasValue || ActionMachine.ToWire(_baseActions).Equals(serverAction.Value);
            if (positionOk && actionOk)
            {
                _base = predictedAtAck;
                return new ReconcileResult(ReconcileOutcome.WithinTolerance, FixVec2.Zero, errorSquared);
            }

            // Rebase on the server and replay what it has not applied yet. A position within the tolerance is kept as
            // predicted (only the action was wrong); the facing is the server's, locked while it runs an action.
            var before = State.Position;
            var state = positionOk ? new MovementState(predictedAtAck.Position, serverState.Facing) : serverState;
            if (!actionOk)
                ActionMachine.Rebase(_baseActions, serverAction!.Value, serverState.Facing);
            _base = state;
            _actions.CopyFrom(_baseActions);
            for (int i = 0; i < _count; i++)
            {
                int slot = (_head + i) % _frames.Length;
                state = Step(state, _actions, _frames[slot], out _);
                _statesAfter[slot] = state;
                _actionsAfter[slot].CopyFrom(_actions);
            }
            SetState(state);
            Corrections++;
            ReportPhase(isCorrection: true);
            return new ReconcileResult(ReconcileOutcome.Corrected, state.Position - before, errorSquared, !actionOk);
        }

        /// <summary>
        /// The server's phases 2 and 3 for one applied frame: MovementSim with grid collision, ignoring keys and facing while
        /// the machine is busy (spec 2.2, backlog C3, R-2.2-02), then the action machine, the facing lock of a started
        /// action, and a dash with collision (backlog D2). Mirrors <c>GameWorld.MoveEntities</c> and <c>ActionRunner.Step</c>.
        /// </summary>
        private MovementState Step(in MovementState state, ActionMachineState actions, in InputFrame frame, out int fired)
        {
            var movement = MovementSim.Step(state, MovementInputMapping.FromFrame(frame), actions.IsBusy,
                _settings.MoveSpeedCmPerSec, _settings.TickRateHz, _grid);
            var input = new ActionInput(true, frame.Actions, frame.SkillSlot, frame.Facing, frame.Sequence);
            // TODO(WP2 merge): predict stamina from the own-entity snapshot's stamina; until then every spend succeeds (the
            // server refuses with NoStamina). Dodge and parry charges are predicted: they are in the machine state.
            var result = ActionMachine.Step(actions, input, UnlimitedActionResources.Instance, _fired);
            fired = result.FiredCount;
            if (result.StartedIndex >= 0)
                movement = new MovementState(movement.Position, actions.Facing);
            if (result.DashDelta != FixVec2.Zero)
                movement = new MovementState(GridCollision.Move(_grid, movement.Position, movement.Position + result.DashDelta),
                    movement.Facing);
            return movement;
        }

        private void ReportPhase(bool isCorrection) =>
            _report.Update(Listener, _catalog, EntityId, _actions.State, _actions.ActionIndex, _actions.Elapsed,
                _actions.InterruptTicks, _clientTick, isCorrection);

        private void SetState(in MovementState state)
        {
            State = state;
            if (_grid.TryGetHeightCm(state.Position, out int height))
                HeightCm = height;
        }

        /// <summary>
        /// Snapshots carry whole cm, rounded. A move blocked at a wall's low edge on +X or +Y stops one raw unit before
        /// the wall cell (<see cref="GridCollision"/>), which rounds up onto the wall cell; replaying from there would
        /// let the next step walk further into the wall (cells being left are not checked) and correct again every
        /// snapshot. Such a position steps back by one raw unit on the blocked axis, which is exactly where the server
        /// stopped. Positions on walkable cells are used as sent.
        /// </summary>
        private FixVec2 UnroundOffWall(FixVec2 position)
        {
            if (_grid.IsWalkable(position))
                return position;
            var back = new FixVec2(position.X - 1, position.Y);
            if (_grid.IsWalkable(back))
                return back;
            back = new FixVec2(position.X, position.Y - 1);
            if (_grid.IsWalkable(back))
                return back;
            back = new FixVec2(position.X - 1, position.Y - 1);
            return _grid.IsWalkable(back) ? back : position;
        }
    }
}
