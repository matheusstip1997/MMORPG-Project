using System;
using System.Collections.Generic;
using System.Linq;
using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.World;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace Game.Tests.EditMode.Prediction
{
    /// <summary>
    /// Backlog D2 on the client: the predictor steps the shared ActionMachine with movement, reconciles the snapshot's
    /// action state, replays actions with movement, and reports phases for the visuals.
    /// </summary>
    public class ActionPredictionTests
    {
        private static ActionDef Action(string id, int netId, ActionKind kind, int startup, int active, int recovery,
            int cooldown = 0) => new ActionDef
        {
            Id = id, NetId = netId, Kind = kind, StartupMs = startup, ActiveMs = active, RecoveryMs = recovery,
            CooldownMs = cooldown, CancelableBy = new[] { "dodge" }, CancelFrom = ActionPhase.Recovery,
            SuperArmorPhases = Array.Empty<ActionPhase>(),
            StaminaCost = kind == ActionKind.Parry ? 0 : 10, ChargePool = ActionDef.PoolOf(kind),
            IFramesStartMs = kind == ActionKind.Dodge ? startup : 0, IFramesMs = kind == ActionKind.Dodge ? active : 0,
        };

        /// <summary>
        /// attack 100/100/100 ms (6 ticks); dodge 50/250/200; slot 1 a dash of 300 cm over 150 ms, then 200 ms. Charges as
        /// spec 12.7: dodge 3, one per 4 s; parry 3, one per 5 s.
        /// </summary>
        private static ActionCatalog Catalog()
        {
            var actions = new[]
            {
                Action("attack", 1, ActionKind.Attack, 100, 100, 100),
                Action("dodge", 2, ActionKind.Dodge, 50, 250, 200),
                Action("parry", 3, ActionKind.Parry, 50, 200, 250),
                Action("skill.dash", 101, ActionKind.Skill, 200, 100, 100, cooldown: 2000),
            };
            var effects = new[]
            {
                new ActionEffectDef
                {
                    Id = "skill.dash.dash", ActionId = "skill.dash", Kind = EffectKind.Dash, Phase = ActionPhase.Startup,
                    DistanceCm = 300, DurationMs = 150,
                },
                new ActionEffectDef
                {
                    Id = "attack.strike", ActionId = "attack", Kind = EffectKind.Strike, Phase = ActionPhase.Active,
                    PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, DamagePercent = 100, DamageKind = EffectDamageKind.Physical,
                },
            };
            var bindings = new ActionBindings
            {
                NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = new[] { "skill.dash" },
            };
            return new ActionCatalog(PredictionTestKit.TickRateHz, actions, effects, bindings,
                new ChargePoolConfig(3, 4000), new ChargePoolConfig(3, 5000));
        }

        [Test]
        public void The_class_loadout_decides_what_a_press_predicts()
        {
            // Backlog E: one ActionBindings row per class; the client takes its class's row from its own spawn (SetLoadout).
            var actions = new[]
            {
                Action("attack.sword", 1, ActionKind.Attack, 100, 100, 100),
                Action("attack.bow", 2, ActionKind.Attack, 150, 100, 100),
                Action("dodge", 3, ActionKind.Dodge, 50, 250, 200),
                Action("parry", 4, ActionKind.Parry, 50, 200, 250),
            };
            var catalog = new ActionCatalog(PredictionTestKit.TickRateHz, actions, Array.Empty<ActionEffectDef>(), new[]
            {
                new ActionBindings { ClassId = "warrior", NormalAttack = "attack.sword", Dodge = "dodge", Parry = "parry", SkillSlots = Array.Empty<string>() },
                new ActionBindings { ClassId = "archer", NormalAttack = "attack.bow", Dodge = "dodge", Parry = "parry", SkillSlots = Array.Empty<string>() },
            }, new ChargePoolConfig(3, 4000), new ChargePoolConfig(3, 5000));
            var predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(), PredictionTestKit.OpenGrid(), catalog);
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.Zero, 0));

            Assert.AreSame(ActionLoadout.None, predictor.Loadout); // two rows: no class until the own spawn names it
            predictor.Predict(MoveKeys.None, 0, InputActions.Attack);
            Assert.IsFalse(predictor.Actions.IsBusy);

            predictor.SetLoadout(catalog.Loadout("archer"));
            predictor.Predict(MoveKeys.None, 0);
            predictor.Predict(MoveKeys.None, 0, InputActions.Attack);
            Assert.AreEqual("attack.bow", predictor.Actions.Action!.Id);

            predictor.Reset();
            Assert.AreEqual("archer", predictor.Loadout.ClassId); // kept: the class does not change with a reset
        }

        [TestCase("mage", 0, "attack.mage")]
        [TestCase("mage", 1, "skill.mage.magicBarrier")]
        [TestCase("mage", 7, "skill.mage.fireSpark")]
        [TestCase("mage", 8, "skill.mage.iceShard")]
        [TestCase("mage", 9, "skill.mage.windCut")]
        [TestCase("mage", 10, "skill.mage.flamethrower")]
        [TestCase("assist", 0, "attack.assist")]
        [TestCase("assist", 1, "skill.assist.lifeRegeneration")]
        [TestCase("assist", 7, "skill.assist.holyStrike")]
        [TestCase("assist", 8, "skill.assist.healWave")]
        [TestCase("assist", 9, "skill.assist.flash")]
        [TestCase("assist", 10, "skill.assist.simpleCleanse")]
        [TestCase("warrior", 1, "skill.warrior.staminaBuff")]
        public void The_committed_kits_predict_their_skills_on_the_class_keys(string classId, int slot, string actionId)
        {
            // Backlog E: the committed ActionBindings rows (left click, 1, Q E R F = slots 7-10) on the client's predictor.
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var catalog = ActionCatalog.From(config);
            var predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(), PredictionTestKit.OpenGrid(), catalog);
            predictor.Reconcile(new SnapshotHeader { ServerTick = 1 }, new MovementState(FixVec2.Zero, 0));
            predictor.SetLoadout(catalog.Loadout(classId));
            predictor.Predict(MoveKeys.None, 0);

            if (slot == 0)
                predictor.Predict(MoveKeys.None, 0, InputActions.Attack);
            else
                predictor.Predict(MoveKeys.None, 0, skillSlot: (byte)slot);

            Assert.AreEqual(actionId, predictor.Actions.Action!.Id);
        }

        [Test]
        public void Actions_and_movement_predict_without_corrections_at_latency()
        {
            var rig = new ActionRig(Catalog(), delayTicks: 3);
            rig.Run(5, MoveKeys.W, 0);
            rig.Tick(MoveKeys.W, 0, InputActions.Attack);
            rig.Run(4, MoveKeys.W, 0);
            rig.Tick(MoveKeys.W, 0, InputActions.Dodge); // cancels the attack's recovery
            rig.Run(12, MoveKeys.D, 16384);
            rig.Tick(MoveKeys.None, 16384, InputActions.None, 1); // dash
            rig.Run(10, MoveKeys.None, 16384);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(rig.ServerState.Position, rig.Predictor.State.Position);
            Assert.IsTrue(ActionMachine.ToWire(rig.ServerActions).Equals(ActionMachine.ToWire(rig.Predictor.Actions)));
        }

        [Test]
        public void A_dodge_without_a_charge_is_refused_in_prediction_as_on_the_server()
        {
            // R-3.3-02 (backlog D4): the charges are machine state, so the client predicts the refusal of the 4th dodge.
            var rig = new ActionRig(Catalog(), delayTicks: 3);
            var listener = new Listener();
            rig.Predictor.Listener = listener;
            ushort fourth = 0;
            for (int i = 0; i < 4; i++)
            {
                if (i == 3)
                    fourth = (ushort)(rig.Predictor.LastSequence + 1);
                rig.Tick(MoveKeys.None, 0, InputActions.Dodge);
                rig.Run(10, MoveKeys.None, 0); // a dodge lasts 10 ticks
            }
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(3, listener.Phases.Count(p => !p.IsCorrection && p.Phase == ActionState.Startup && p.ActionId == "dodge"));
            Assert.AreEqual(0, rig.Predictor.Actions.Charges(ChargePool.Dodge));
            Assert.AreEqual(0, rig.ServerActions.Charges(ChargePool.Dodge));
            CollectionAssert.AreEqual(new[] { (fourth, ActionReason.DodgeNoCharge) }, rig.ServerRefusals);
            Assert.IsFalse(rig.Predictor.Actions.IsBusy);
        }

        [Test]
        public void A_dash_into_a_wall_stops_where_the_server_stops()
        {
            var grid = PredictionTestKit.Grid(wall: (300, -1000, 400, 1000));
            var rig = new ActionRig(Catalog(), delayTicks: 2, grid: grid);
            rig.Tick(MoveKeys.None, 16384, InputActions.None, 1); // +X from x = 100 cm: 300 cm, the wall starts at 300
            rig.Run(10, MoveKeys.None, 16384);
            rig.Settle();

            Assert.AreEqual(0, rig.Corrections, rig.Describe());
            Assert.AreEqual(300 * FixedPoint.RawPerCm - 1, rig.Predictor.State.Position.X);
            Assert.AreEqual(rig.ServerState.Position, rig.Predictor.State.Position);
        }

        [Test]
        public void A_lost_press_is_corrected_by_replaying_actions_with_movement()
        {
            var rig = new ActionRig(Catalog(), delayTicks: 3);
            rig.Run(3, MoveKeys.W, 0);
            rig.Lost.Add((ushort)(rig.Predictor.LastSequence + 1));
            rig.Tick(MoveKeys.W, 0, InputActions.Attack); // never applied: the server keeps walking
            rig.Run(8, MoveKeys.W, 0);
            rig.Settle();

            Assert.Greater(rig.Corrections, 0);
            Assert.IsTrue(rig.Results.Any(r => r.ActionMismatch));
            Assert.AreEqual(rig.ServerState.Position, rig.Predictor.State.Position);
            Assert.IsFalse(rig.Predictor.Actions.IsBusy);
        }

        [Test]
        public void The_listener_hears_each_phase_with_its_action_id_and_length()
        {
            var rig = new ActionRig(Catalog(), delayTicks: 1);
            var listener = new Listener();
            rig.Predictor.Listener = listener;
            rig.Tick(MoveKeys.None, 0, InputActions.Attack);
            rig.Run(8, MoveKeys.None, 0);

            var phases = listener.Phases.Where(p => !p.IsCorrection).ToList();
            CollectionAssert.AreEqual(
                new[] { ActionState.Startup, ActionState.Active, ActionState.Recovery, ActionState.Idle },
                phases.Select(p => p.Phase).ToArray());
            Assert.IsTrue(phases.Take(3).All(p => p.ActionId == "attack" && p.ActionNetId == 1 && p.PhaseDurationMs == 100));
            Assert.AreEqual(phases[0].ActionStartTick, phases[0].PhaseStartTick);
            Assert.AreEqual(phases[0].PhaseStartTick + 2, phases[1].PhaseStartTick);
            Assert.AreEqual(1, listener.Effects.Count);
            Assert.AreEqual("attack.strike", listener.Effects[0].Effect.Effect.Def.Id);
        }

        [Test]
        public void The_remote_tracker_reports_changes_from_snapshots_only()
        {
            var tracker = new RemoteActionTracker(Catalog());
            var listener = new Listener();
            tracker.Listener = listener;
            var entity = new EntitySnapshot { EntityId = 9, State = (byte)ActionState.Startup, ActionId = 1, StateAge = 1 };
            tracker.Apply(100, entity);
            tracker.Apply(101, new EntitySnapshot { EntityId = 9, State = (byte)ActionState.Startup, ActionId = 1, StateAge = 2 });
            tracker.Apply(102, new EntitySnapshot { EntityId = 9, State = (byte)ActionState.Active, ActionId = 1, StateAge = 3 });
            tracker.Apply(110, new EntitySnapshot { EntityId = 9, State = (byte)ActionState.Flinched, StateAge = 0, StateDurationTicks = 6 });
            tracker.Apply(116, new EntitySnapshot { EntityId = 9 });

            CollectionAssert.AreEqual(
                new[] { ActionState.Startup, ActionState.Active, ActionState.Flinched, ActionState.Idle },
                listener.Phases.Select(p => p.Phase).ToArray());
            Assert.AreEqual(99u, listener.Phases[0].ActionStartTick);
            Assert.AreEqual(101u, listener.Phases[1].PhaseStartTick);
            Assert.AreEqual(300, listener.Phases[2].PhaseDurationMs);
            Assert.AreEqual(string.Empty, listener.Phases[2].ActionId);
        }

        private sealed class Listener : IActionPhaseListener
        {
            public readonly List<ActionPhaseChange> Phases = new List<ActionPhaseChange>();
            public readonly List<ActionEffectEvent> Effects = new List<ActionEffectEvent>();

            public void OnActionPhase(in ActionPhaseChange change) => Phases.Add(change);

            public void OnActionEffect(in ActionEffectEvent effect) => Effects.Add(effect);
        }

        /// <summary>
        /// The server's phases 1-3 in miniature (GameWorld, ActionRunner): one queued frame per tick, movement while the
        /// machine is idle, then the machine with the facing lock and the dash, and a snapshot with the action state. A
        /// fixed delay each way; frames in <see cref="Lost"/> never arrive.
        /// </summary>
        private sealed class ActionRig
        {
            private readonly WorldGrid _grid;
            private readonly Queue<InputFrame> _queue = new Queue<InputFrame>();
            private readonly List<(int At, InputFrame Frame)> _toServer = new List<(int, InputFrame)>();
            private readonly List<(int At, SnapshotHeader Header, EntitySnapshot Self)> _toClient =
                new List<(int, SnapshotHeader, EntitySnapshot)>();
            private readonly FiredEffect[] _fired = new FiredEffect[ActionMachine.MaxFiredPerStep];
            private readonly System.Text.StringBuilder _log = new System.Text.StringBuilder();
            private int _now;
            private uint _tick = 500;
            private bool _hasAck;
            private ushort _ack;

            public ActionRig(ActionCatalog catalog, int delayTicks, WorldGrid grid = null)
            {
                _grid = grid ?? PredictionTestKit.OpenGrid();
                DelayTicks = delayTicks;
                ServerState = new MovementState(FixVec2.FromCm(100, -50), 0);
                ServerActions = new ActionMachineState(catalog);
                Predictor = new LocalPlayerPredictor(PredictionTestKit.Settings(), _grid, catalog);
                var (header, self) = Snapshot();
                Results.Add(Predictor.Reconcile(header, SnapshotAxes.GroundState(self), ActionWireState.From(self)));
            }

            public int DelayTicks { get; }
            public MovementState ServerState { get; private set; }
            public ActionMachineState ServerActions { get; }
            public LocalPlayerPredictor Predictor { get; }
            public HashSet<ushort> Lost { get; } = new HashSet<ushort>();
            public List<(ushort Sequence, ActionReason Reason)> ServerRefusals { get; } = new List<(ushort, ActionReason)>();
            public List<ReconcileResult> Results { get; } = new List<ReconcileResult>();
            public int Corrections => Results.Count(r => r.Outcome == ReconcileOutcome.Corrected);
            public string Describe() => _log.Length == 0 ? "no corrections" : _log.ToString();

            public void Run(int ticks, MoveKeys keys, ushort facing)
            {
                for (int i = 0; i < ticks; i++)
                    Tick(keys, facing);
            }

            public void Settle()
            {
                for (int i = 0; i < 2 * DelayTicks + 4; i++)
                    Tick(MoveKeys.None, 0, InputActions.None, 0, send: false);
            }

            public void Tick(MoveKeys keys, ushort facing, InputActions actions = InputActions.None, byte slot = 0, bool send = true)
            {
                if (send)
                {
                    var frame = Predictor.Predict(keys, facing, actions, slot);
                    if (!Lost.Contains(frame.Sequence))
                        _toServer.Add((_now + DelayTicks, frame));
                }
                for (int i = 0; i < _toServer.Count; i++)
                {
                    if (_toServer[i].At > _now) continue;
                    _queue.Enqueue(_toServer[i].Frame);
                    _toServer.RemoveAt(i--);
                }
                ServerStep();
                var (header, self) = Snapshot();
                _toClient.Add((_now + DelayTicks, header, self));
                for (int i = 0; i < _toClient.Count; i++)
                {
                    if (_toClient[i].At > _now) continue;
                    var result = Predictor.Reconcile(_toClient[i].Header, SnapshotAxes.GroundState(_toClient[i].Self),
                        ActionWireState.From(_toClient[i].Self));
                    Results.Add(result);
                    if (result.Outcome == ReconcileOutcome.Corrected)
                        _log.AppendLine($"tick {_now}: corrected {result.ErrorCm:0.###} cm, action mismatch {result.ActionMismatch}");
                    _toClient.RemoveAt(i--);
                }
                _now++;
            }

            private void ServerStep()
            {
                bool hasInput = _queue.Count > 0;
                var frame = hasInput ? _queue.Dequeue() : default;
                if (hasInput)
                {
                    ServerState = MovementSim.Step(ServerState, MovementInputMapping.FromFrame(frame), ServerActions.IsBusy,
                        PredictionTestKit.SpeedCmPerSec, PredictionTestKit.TickRateHz, _grid);
                    _hasAck = true;
                    _ack = frame.Sequence;
                }
                var input = new ActionInput(hasInput, frame.Actions, frame.SkillSlot,
                    hasInput ? frame.Facing : ServerState.Facing, frame.Sequence);
                var result = ActionMachine.Step(ServerActions, input, UnlimitedActionResources.Instance, _fired);
                if (result.Refused != ActionReason.None)
                    ServerRefusals.Add((result.RefusedSequence, result.Refused));
                if (result.StartedIndex >= 0)
                    ServerState = new MovementState(ServerState.Position, ServerActions.Facing);
                if (result.DashDelta != FixVec2.Zero)
                    ServerState = new MovementState(
                        GridCollision.Move(_grid, ServerState.Position, ServerState.Position + result.DashDelta), ServerState.Facing);
                _tick++;
            }

            private (SnapshotHeader Header, EntitySnapshot Self) Snapshot()
            {
                var wire = ActionMachine.ToWire(ServerActions);
                return (new SnapshotHeader { ServerTick = _tick, HasInputAck = _hasAck, LastInputSequence = _ack },
                    new EntitySnapshot
                    {
                        EntityId = 1,
                        XCm = FixedPoint.ToCmRound(ServerState.Position.X),
                        ZCm = FixedPoint.ToCmRound(ServerState.Position.Y),
                        Facing = Quantize.RoundTripAngle(ServerState.Facing, Shared.Protocol.Snapshot.FacingBits),
                        State = wire.State,
                        ActionId = wire.ActionId,
                        StateAge = wire.Age,
                        StateDurationTicks = wire.DurationTicks,
                    });
            }
        }
    }
}
