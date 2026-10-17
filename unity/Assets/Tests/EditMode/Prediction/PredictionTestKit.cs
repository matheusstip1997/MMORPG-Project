using System;
using System.Collections.Generic;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    /// <summary>Settings and a fake server for the prediction tests.</summary>
    internal static class PredictionTestKit
    {
        public const int SpeedCmPerSec = 400;
        public const int TickRateHz = 20;
        /// <summary>One straight step: 400 cm/s at 20 Hz is 20 cm per tick, a whole cm, so straight runs are exact.</summary>
        public const int StepCm = SpeedCmPerSec / TickRateHz;

        public static PredictionSettings Settings(int toleranceCm = 2, int snapCm = 300, int smoothingMs = 120,
            int maxPending = 64, int maxTicksPerFrame = 4, int delayMs = 100, int catchUpMs = 500) =>
            new PredictionSettings(
                new StartingValuesM1
                {
                    TickRateHz = TickRateHz, SnapshotRateHz = TickRateHz, InterpolationDelayMs = delayMs,
                    InputRedundancy = 3, MoveSpeedCmPerSec = SpeedCmPerSec,
                },
                new ClientPrediction
                {
                    CorrectionToleranceCm = toleranceCm, CorrectionSnapCm = snapCm, CorrectionSmoothingMs = smoothingMs,
                    MaxPendingInputs = maxPending, MaxTicksPerFrame = maxTicksPerFrame, InterpolationClockCatchUpMs = catchUpMs,
                });
    }

    /// <summary>
    /// The server's movement path in miniature, as GameWorld and InputRouter do it (spec 2.2): frames newer than the
    /// newest queued are queued, the queue drops its oldest beyond <see cref="QueueCap"/>, one frame is applied per
    /// tick with MovementSim and never invented, and the snapshot carries the state in whole cm with the ack.
    /// </summary>
    internal sealed class FakeServer
    {
        public const int QueueCap = 4;
        private readonly Queue<InputFrame> _queue = new Queue<InputFrame>();
        private bool _anyQueued;
        private ushort _newestQueued;

        public FakeServer(MovementState spawn, ushort? previousAck = null)
        {
            State = spawn;
            if (previousAck.HasValue)
            {
                HasAck = true;
                LastApplied = previousAck.Value;
                _anyQueued = true;
                _newestQueued = previousAck.Value;
            }
        }

        public MovementState State { get; set; }
        public bool HasAck { get; private set; }
        public ushort LastApplied { get; private set; }
        public uint Tick { get; private set; } = 1000;

        /// <summary>Frames to never apply, as if every packet carrying them was lost.</summary>
        public HashSet<ushort> Lost { get; } = new HashSet<ushort>();

        public void Receive(byte[] packet)
        {
            var frames = new InputFrame[InputPacket.MaxFrames];
            if (!InputPacket.TryRead(packet, frames, out int count))
                throw new InvalidOperationException("client sent a malformed packet");
            for (int i = count - 1; i >= 0; i--)
            {
                if (Lost.Contains(frames[i].Sequence) || (_anyQueued && !Sequence.IsNewer(frames[i].Sequence, _newestQueued)))
                    continue;
                _anyQueued = true;
                _newestQueued = frames[i].Sequence;
                _queue.Enqueue(frames[i]);
                if (_queue.Count > QueueCap)
                    _queue.Dequeue();
            }
        }

        public void Step()
        {
            if (_queue.Count > 0)
            {
                var frame = _queue.Dequeue();
                State = MovementSim.Step(State, MovementInputMapping.FromFrame(frame), false,
                    PredictionTestKit.SpeedCmPerSec, PredictionTestKit.TickRateHz);
                HasAck = true;
                LastApplied = frame.Sequence;
            }
            Tick++;
        }

        public (SnapshotHeader Header, EntitySnapshot Self) Snapshot() =>
            (new SnapshotHeader { ServerTick = Tick, HasInputAck = HasAck, LastInputSequence = LastApplied },
             new EntitySnapshot
             {
                 EntityId = 1,
                 XCm = FixedPoint.ToCmRound(State.Position.X),
                 YCm = 0,
                 ZCm = FixedPoint.ToCmRound(State.Position.Y),
                 Facing = Quantize.RoundTripAngle(State.Facing, Shared.Protocol.Snapshot.FacingBits),
             });
    }

    /// <summary>
    /// Client, fake server and a network with a fixed delay in ticks each way, loss and reordering of snapshots.
    /// Every tick: the client predicts and sends, the server applies and snapshots, the client reconciles what arrived.
    /// </summary>
    internal sealed class PredictionRig
    {
        private readonly List<(int At, byte[] Packet)> _toServer = new List<(int, byte[])>();
        private readonly List<(int At, SnapshotHeader Header, EntitySnapshot Self)> _toClient = new List<(int, SnapshotHeader, EntitySnapshot)>();
        private readonly InputPacketBuilder _packets = new InputPacketBuilder(3);
        private readonly byte[] _buffer;
        private int _now;

        public PredictionRig(PredictionSettings settings, int delayTicks, MovementState? spawn = null, ushort? previousAck = null)
        {
            DelayTicks = delayTicks;
            Server = new FakeServer(spawn ?? new MovementState(FixVec2.FromCm(100, -50), 0), previousAck);
            Predictor = new LocalPlayerPredictor(settings);
            _buffer = new byte[_packets.MaxPacketSize];
            var (header, self) = Server.Snapshot();
            Results.Add(Predictor.Reconcile(header, SnapshotAxes.GroundState(self)));
        }

        public int DelayTicks { get; }
        public FakeServer Server { get; }
        public LocalPlayerPredictor Predictor { get; }
        public List<ReconcileResult> Results { get; } = new List<ReconcileResult>();

        /// <summary>Called with each snapshot before delivery: return false to drop it.</summary>
        public Func<int, bool> DeliverSnapshot { get; set; } = _ => true;

        /// <summary>Extra delay in ticks for a snapshot (reordering).</summary>
        public Func<int, int> SnapshotExtraDelay { get; set; } = _ => 0;

        public int Corrections => Count(ReconcileOutcome.Corrected);

        public int Count(ReconcileOutcome outcome)
        {
            int n = 0;
            foreach (var r in Results)
                if (r.Outcome == outcome) n++;
            return n;
        }

        public void Run(int ticks, MoveKeys keys, ushort facing = 0)
        {
            for (int i = 0; i < ticks; i++)
                Tick(keys, facing);
        }

        /// <summary>Stops sending inputs and runs until every frame is acknowledged and its snapshot has arrived.</summary>
        public void Settle()
        {
            for (int i = 0; i < 2 * DelayTicks + 4; i++)
                Tick(MoveKeys.None, 0, send: false);
        }

        public void Tick(MoveKeys keys, ushort facing, bool send = true)
        {
            if (send)
            {
                var frame = Predictor.Predict(keys, facing);
                int size = _packets.Write(frame, _buffer);
                _toServer.Add((_now + DelayTicks, new ReadOnlySpan<byte>(_buffer, 0, size).ToArray()));
            }

            for (int i = 0; i < _toServer.Count; i++)
            {
                if (_toServer[i].At > _now) continue;
                Server.Receive(_toServer[i].Packet);
                _toServer.RemoveAt(i--);
            }
            Server.Step();
            int index = (int)Server.Tick;
            if (DeliverSnapshot(index))
            {
                var (header, self) = Server.Snapshot();
                _toClient.Add((_now + DelayTicks + SnapshotExtraDelay(index), header, self));
            }

            for (int i = 0; i < _toClient.Count; i++)
            {
                if (_toClient[i].At > _now) continue;
                var header = _toClient[i].Header;
                var result = Predictor.Reconcile(header, SnapshotAxes.GroundState(_toClient[i].Self));
                Results.Add(result);
                if (result.Outcome == ReconcileOutcome.Corrected)
                    _log.AppendLine($"tick {_now}: corrected {result.ErrorCm:0.###} cm at ack {header.LastInputSequence}, " +
                                    $"server {_toClient[i].Self.XCm},{_toClient[i].Self.ZCm} cm, shift {result.Shift}");
                _toClient.RemoveAt(i--);
            }
            _now++;
        }

        private readonly System.Text.StringBuilder _log = new System.Text.StringBuilder();

        /// <summary>Every correction, for assertion messages.</summary>
        public string Describe() => _log.Length == 0 ? "no corrections" : _log.ToString();

        /// <summary>Distance in cm between the prediction and the server's exact state.</summary>
        public double ErrorCm()
        {
            var d = Predictor.State.Position - Server.State.Position;
            return Math.Sqrt(d.LengthSquared) / FixedPoint.RawPerCm;
        }
    }
}
