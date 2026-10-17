#nullable enable
using System;
using Shared.Protocol;
using Shared.Simulation;
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
        public ReconcileResult(ReconcileOutcome outcome, FixVec2 shift, long errorSquaredRaw)
        {
            Outcome = outcome;
            Shift = shift;
            ErrorSquaredRaw = errorSquaredRaw;
        }

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
    /// Facing is not reconciled: it comes from the player's own input every frame (R-2.2-01).
    /// TODO(D2): actionActive from the predicted ActionMachine (R-2.2-02). TODO(D5): speed from the movement-speed stat
    /// (R-2.2-03). TODO(C2): step with the world grid (collision) once the client loads it.
    /// Plain C# with no UnityEngine API (GameServer.Tests compiles it); does not allocate after construction.
    /// </summary>
    public sealed class LocalPlayerPredictor
    {
        private readonly PredictionSettings _settings;
        private readonly long _toleranceSquaredRaw;
        private readonly InputFrame[] _frames;
        private readonly MovementState[] _statesAfter;
        private int _head;
        private int _count;
        private MovementState _base;
        private uint _lastServerTick;
        private ushort _lastSequence;
        private uint _clientTick;

        public LocalPlayerPredictor(PredictionSettings settings)
        {
            _settings = settings;
            long toleranceRaw = (long)settings.CorrectionToleranceCm * FixedPoint.RawPerCm;
            _toleranceSquaredRaw = toleranceRaw * toleranceRaw;
            _frames = new InputFrame[settings.MaxPendingInputs];
            _statesAfter = new MovementState[settings.MaxPendingInputs];
        }

        /// <summary>False until the first snapshot gave the starting state.</summary>
        public bool HasState { get; private set; }

        /// <summary>Predicted state after the newest frame.</summary>
        public MovementState State { get; private set; }

        public int PendingCount => _count;

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
            _base = default;
            _head = 0;
            _count = 0;
            _lastServerTick = 0;
            _lastSequence = 0;
            _clientTick = 0;
            Corrections = 0;
            DroppedPending = 0;
        }

        /// <summary>
        /// Makes the next input frame from the held keys and facing, steps the prediction with it, and returns it for
        /// sending. Requires <see cref="HasState"/>.
        /// </summary>
        public InputFrame Predict(WireMoveKeys keys, ushort facing)
        {
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
            };
            _lastSequence = frame.Sequence;

            if (_count == _frames.Length)
            {
                // Stalled: take the oldest frame as acknowledged as predicted; the next snapshot corrects if not.
                _base = _statesAfter[_head];
                _head = (_head + 1) % _frames.Length;
                _count--;
                DroppedPending++;
            }

            State = Step(State, frame);
            int slot = (_head + _count) % _frames.Length;
            _frames[slot] = frame;
            _statesAfter[slot] = State;
            _count++;
            return frame;
        }

        /// <summary>
        /// Applies the server's state of the local entity (the first entity of the snapshot, spec 1.7) with the
        /// snapshot's header. <paramref name="serverState"/> is in simulation ground coordinates (see <c>SnapshotAxes</c>).
        /// </summary>
        public ReconcileResult Reconcile(in SnapshotHeader header, MovementState serverState)
        {
            if (!HasState)
            {
                HasState = true;
                _lastServerTick = header.ServerTick;
                _base = serverState;
                State = serverState;
                // A server that already applied frames (a reattached session) gets sequences after its ack.
                if (header.HasInputAck)
                    _lastSequence = header.LastInputSequence;
                return new ReconcileResult(ReconcileOutcome.Initialized, FixVec2.Zero, 0);
            }

            if ((int)(header.ServerTick - _lastServerTick) <= 0)
                return new ReconcileResult(ReconcileOutcome.Stale, FixVec2.Zero, 0);

            MovementState predictedAtAck = _base;
            int acked = 0;
            if (header.HasInputAck)
            {
                ushort ack = header.LastInputSequence;
                if (Sequence.IsNewer(ack, _lastSequence))
                    return new ReconcileResult(ReconcileOutcome.UnknownAck, FixVec2.Zero, 0);
                while (acked < _count && !Sequence.IsNewer(_frames[(_head + acked) % _frames.Length].Sequence, ack))
                {
                    predictedAtAck = _statesAfter[(_head + acked) % _frames.Length];
                    acked++;
                }
            }
            _lastServerTick = header.ServerTick;
            _head = (_head + acked) % _frames.Length;
            _count -= acked;

            var error = serverState.Position - predictedAtAck.Position;
            long errorSquared = error.LengthSquared;
            if (errorSquared <= _toleranceSquaredRaw)
            {
                _base = predictedAtAck;
                return new ReconcileResult(ReconcileOutcome.WithinTolerance, FixVec2.Zero, errorSquared);
            }

            // Rebase on the server and replay what it has not applied yet.
            var before = State.Position;
            _base = serverState;
            var state = serverState;
            for (int i = 0; i < _count; i++)
            {
                int slot = (_head + i) % _frames.Length;
                state = Step(state, _frames[slot]);
                _statesAfter[slot] = state;
            }
            State = state;
            Corrections++;
            return new ReconcileResult(ReconcileOutcome.Corrected, state.Position - before, errorSquared);
        }

        private MovementState Step(in MovementState state, in InputFrame frame) =>
            MovementSim.Step(state, MovementInputMapping.FromFrame(frame), actionActive: false,
                _settings.MoveSpeedCmPerSec, _settings.TickRateHz);
    }
}
