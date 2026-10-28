using Game.Gameplay.Entities;
using Game.Gameplay.Prediction;
using GameServer.Combat;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.World;
using static GameServer.Tests.Support.TestWorlds;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// Backlog D2 in the world loop: phase 3 runs every player's action machine (spec 1.6), refusals go out on the reliable
/// channel (spec 1.7, 9.7), snapshots carry the action state, and client prediction replays actions with movement.
/// </summary>
public class WorldActionsTests
{
    private static readonly ActionCatalog Catalog = TestActions.Catalog();

    private static WorldHarness Harness(WorldMap? map = null, EventBus? bus = null, int lingerMs = 0) =>
        new(map: map, actions: Catalog, bus: bus, events: bus != null ? new EventDispatchHook(bus) : null,
            values: TestSessions.Values(lingerOutOfCombatMs: lingerMs));

    // 20 x 10 cells of 100 cm from (0, 0); column 10 (x 1000-1100 cm) is a wall.
    private static WorldMap WalledMap() =>
        TestMaps.Map(TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 0, walkable: (x, _) => x != 10),
            [TestMaps.SpawnMarker(650, 450)]);

    [Fact]
    public void An_attack_stops_movement_from_the_next_tick_and_locks_the_facing_until_it_ends()
    {
        var harness = Harness();
        var player = harness.Join(1);
        ushort sequence = 0;
        var positions = new List<int>();
        var facings = new List<ushort>();
        for (int tick = 0; tick < 12; tick++)
        {
            var actions = tick == 1 ? InputActions.Attack : InputActions.None;
            ushort facing = Quantize.RoundTripAngle((ushort)(4_096 * (tick + 1)), InputPacket.FacingBits);
            harness.SendInputs(1, Frame(++sequence, MoveKeys.W, facing, actions));
            harness.Ticks(1);
            positions.Add(player.YCm);
            facings.Add(player.Movement.Facing);
        }
        // Tick 1 moves (phase 2) and starts the attack (phase 3); it lasts 6 ticks, so ticks 2-7 stand still.
        Assert.Equal([20, 40, 40, 40, 40, 40, 40, 40, 60, 80, 100, 120], positions);
        Assert.All(facings.Skip(1).Take(7), f => Assert.Equal(facings[1], f));
        Assert.NotEqual(facings[1], facings[8]);
    }

    [Fact]
    public void Snapshots_carry_the_action_state()
    {
        var harness = Harness();
        harness.Join(1);
        harness.SendInputs(1, Frame(1, actions: InputActions.Attack));
        harness.Ticks(1);
        var self = harness.LastSnapshot(1).Self;
        Assert.Equal(((byte)ActionState.Startup, (ushort)TestActions.AttackNetId, (byte)0),
            (self.State, self.ActionId, self.StateAge));

        harness.Ticks(2);
        self = harness.LastSnapshot(1).Self;
        Assert.Equal(((byte)ActionState.Active, (byte)2), (self.State, self.StateAge));

        harness.Ticks(4);
        self = harness.LastSnapshot(1).Self;
        Assert.Equal(((byte)ActionState.Idle, (ushort)0), (self.State, self.ActionId));
    }

    [Fact]
    public void Refused_presses_send_reason_codes_on_the_reliable_channel()
    {
        var harness = Harness();
        harness.Join(1);
        harness.SendInputs(1, Frame(1, skillSlot: 3)); // empty slot
        harness.Ticks(1);
        harness.SendInputs(1, Frame(2, skillSlot: 1)); // starts, 1 s cooldown
        harness.Ticks(1);
        harness.SendInputs(1, Frame(3));
        harness.Ticks(10); // the skill ended after 6 ticks
        harness.SendInputs(1, Frame(4, skillSlot: 1));
        harness.Ticks(1);

        Assert.Equal(
            [((ushort)1, (ushort)0, (byte)3, ActionReason.SkillNotLearned), ((ushort)4, (ushort)TestActions.CooldownSkillNetId, (byte)1, ActionReason.OnCooldown)],
            harness.Transport.ActionResultsFor(1));
        Assert.Equal((byte)ActionState.Idle, harness.LastSnapshot(1).Self.State);
    }

    [Fact]
    public void A_fourth_dodge_in_a_row_is_refused_for_lack_of_a_charge()
    {
        // R-3.3-02, spec 12.7: 3 dodge charges, one back every 4 s (80 ticks); a dodge lasts 10 ticks.
        var harness = Harness();
        var player = harness.Join(1);
        ushort sequence = 0;
        for (int dodge = 0; dodge < 4; dodge++)
        {
            harness.SendInputs(1, Frame(++sequence, actions: InputActions.Dodge));
            harness.Ticks(1);
            harness.SendInputs(1, Frame(++sequence));
            harness.Ticks(10);
        }

        Assert.Equal([((ushort)7, (ushort)TestActions.DodgeNetId, (byte)0, ActionReason.DodgeNoCharge)],
            harness.Transport.ActionResultsFor(1));
        Assert.Equal(0, player.Actions.Charges(ChargePool.Dodge));
        Assert.Equal(3, player.Actions.Charges(ChargePool.Parry));
        Assert.Equal((byte)ActionState.Idle, harness.LastSnapshot(1).Self.State);
    }

    [Fact]
    public void Starting_an_action_publishes_ActionStarted_with_the_locked_facing()
    {
        var bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
        var seen = new Recorder();
        bus.Subscribe(seen);
        var harness = Harness(bus: bus);
        var player = harness.Join(1);
        ushort facing = Quantize.RoundTripAngle(20_000, InputPacket.FacingBits);
        uint tick = harness.World.ServerTick;
        harness.SendInputs(1, Frame(1, facing: facing, actions: InputActions.Attack));
        harness.Ticks(1);

        Assert.Equal([new ActionStarted(player.Id, TestActions.AttackNetId, tick, facing, ParryColor.None)], seen.Events);
        Assert.Equal(player.CharacterId, seen.Actors[0]);
    }

    [Fact]
    public void A_dash_moves_the_player_with_collision_and_is_not_a_speed_violation()
    {
        var harness = Harness(WalledMap());
        var player = harness.Join(1);
        harness.SendInputs(1, Frame(1, facing: 16_384, skillSlot: 2)); // +X, 400 cm, the wall is at 1000 cm
        harness.Ticks(1);
        Assert.Equal(750, player.XCm); // 100 cm on the start tick
        harness.Ticks(10);
        Assert.Equal(1_000 * FixedPoint.RawPerCm - 1, player.Movement.Position.X); // stopped before the wall
        Assert.Equal(450, player.YCm);
        Assert.Equal(0, harness.World.SpeedViolations);
        var near = new List<Entity>();
        harness.World.Grid.QueryCells(player.XCm, player.YCm, player.XCm, player.YCm, near);
        Assert.Contains(player, near); // the grid follows the dash
    }

    [Fact]
    public void Effects_other_than_dash_reach_the_sink_in_phase_3()
    {
        var harness = Harness();
        var sink = new EffectRecorder(harness.World);
        harness.World.ActionEffects = sink;
        var player = harness.Join(1);
        uint start = harness.World.ServerTick;
        harness.SendInputs(1, Frame(1, skillSlot: 2));
        harness.Ticks(10);

        // The strike fires when Active starts: Startup is 300 ms = 6 ticks. The dash is carried out, not handed over.
        Assert.Equal([(player.Id, "skill.dash.strike", start + 6, TickPhase.ActionStateMachines)], sink.Seen);
    }

    [Fact]
    public void A_lingering_player_finishes_its_action()
    {
        var harness = Harness(lingerMs: 10_000);
        var player = harness.Join(1);
        harness.SendInputs(1, Frame(1, actions: InputActions.Attack));
        harness.Ticks(1);
        harness.Leave(1);
        harness.Ticks(1);
        Assert.False(player.IsConnected);
        Assert.True(player.Actions.IsBusy);
        harness.Ticks(5);
        Assert.False(player.Actions.IsBusy);
    }

    [Fact]
    public void Prediction_with_actions_matches_the_server_without_corrections()
    {
        var harness = Harness(WalledMap());
        var client = new Client(harness, WalledMap().Grid);
        // Walk, attack, cancel its recovery with a dodge, refuse an empty slot, dash into the wall, then walk again.
        var script = new List<(MoveKeys Keys, ushort Facing, InputActions Actions, byte Slot)>();
        for (int i = 0; i < 4; i++) script.Add((MoveKeys.W, 0, InputActions.None, 0));
        script.Add((MoveKeys.W, 0, InputActions.Attack, 0));
        for (int i = 0; i < 3; i++) script.Add((MoveKeys.D, 16_384, InputActions.None, 0));
        script.Add((MoveKeys.D, 16_384, InputActions.Dodge, 0)); // Recovery of the attack: cancelled
        for (int i = 0; i < 10; i++) script.Add((MoveKeys.S, 8_192, InputActions.None, 0));
        script.Add((MoveKeys.None, 16_384, InputActions.None, 3));
        script.Add((MoveKeys.None, 16_384, InputActions.None, 2)); // dash +X into the wall
        for (int i = 0; i < 12; i++) script.Add((MoveKeys.A, 49_152, InputActions.None, 0));

        foreach (var step in script)
            client.Tick(step.Keys, step.Facing, step.Actions, step.Slot);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.True(ActionMachine.ToWire(client.Server.Actions).Equals(ActionMachine.ToWire(client.Predictor.Actions)));
        Assert.Equal([TestActions.AttackNetId, TestActions.DodgeNetId, TestActions.DashSkillNetId], client.LocalPhases
            .Where(p => p.Phase == ActionState.Startup).Select(p => (int)p.ActionNetId));
        Assert.Single(harness.Transport.ActionResultsFor(1)); // the empty slot
    }

    [Fact]
    public void A_server_stun_is_corrected_once_and_then_predicted_like_the_server()
    {
        // WP-M2-9: a stun is a server-side status (phase 6 of the server, no input of the client's): the snapshot's
        // Stunned state rebases the machine once, the presses during it are dropped on both sides, and the attack after it
        // starts on both (ActionMachine.Rebase).
        var harness = Harness();
        var client = new Client(harness, TestMaps.Open().Grid);
        for (int i = 0; i < 3; i++)
            client.Tick(MoveKeys.W, 0, InputActions.None, 0);
        Assert.True(ActionMachine.Interrupt(client.Server.Actions, InterruptKind.Stun, 500));
        for (int i = 0; i < 14; i++)
            client.Tick(MoveKeys.W, 0, i % 2 == 0 ? InputActions.Attack : InputActions.None, 0);

        Assert.Equal(1, client.Predictor.Corrections);
        Assert.Contains(client.LocalPhases, p => p.IsCorrection && p.Phase == ActionState.Stunned);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(ActionMachine.ToWire(client.Server.Actions), ActionMachine.ToWire(client.Predictor.Actions));
        Assert.True(client.Server.Actions.IsBusy); // the attack pressed after the stun runs on both sides
    }

    [Fact]
    public void A_throw_with_an_impact_effect_is_predicted_without_corrections()
    {
        // WP-M2-9: Bomb Throw and Rock Throw carry an Impact effect the action machine never fires (the server places it
        // where the projectile ends), so the client's timeline of the action is the server's.
        var throwAction = new ActionDef
        {
            Id = "skill.throw", NetId = 103, Kind = ActionKind.Skill, StartupMs = 200, ChannelMs = 400, ActiveMs = 100,
            RecoveryMs = 300, CancelFrom = ActionPhase.Startup,
        };
        var projectile = new ActionEffectDef
        {
            Id = "skill.throw.projectile", ActionId = "skill.throw", Kind = EffectKind.Projectile, Phase = ActionPhase.Active,
            PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, DamagePercent = 100, DamageKind = EffectDamageKind.Magic,
            ProjectileId = "rock", ImpactEffectId = "skill.throw.explosion",
        };
        var explosion = new ActionEffectDef
        {
            Id = "skill.throw.explosion", ActionId = "skill.throw", Kind = EffectKind.Impact, Phase = ActionPhase.Active,
            PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, DamagePercent = 100, DamageKind = EffectDamageKind.Magic,
        };
        var base_ = TestActions.Catalog();
        var actions = Enumerable.Range(0, base_.Count).Select(i => base_[i].Def).Append(throwAction).ToArray();
        var effects = Enumerable.Range(0, base_.Count).SelectMany(i => base_[i].Effects.Select(e => e.Def))
            .Append(projectile).Append(explosion).ToArray();
        var catalog = new ActionCatalog(20, actions, effects,
            new ActionBindings { NormalAttack = "attack", Dodge = "dodge", Parry = "parry", SkillSlots = ["skill.cooldown", "skill.dash", "skill.throw"] },
            new ChargePoolConfig(3, 4000), new ChargePoolConfig(3, 5000));
        var harness = new WorldHarness(actions: catalog);
        var client = new Client(harness, TestMaps.Open().Grid, catalog);

        client.Tick(MoveKeys.None, 0, InputActions.None, 3);
        for (int i = 0; i < catalog.Find("skill.throw")!.TotalTicks + 4; i++)
            client.Tick(MoveKeys.W, 0, InputActions.None, 0);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Contains(client.LocalPhases, p => p.Phase == ActionState.Active && p.ActionNetId == 103);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
    }

    [Fact]
    public void A_slowed_player_is_predicted_at_the_servers_speed()
    {
        // WP-M2-9, Cold's slow: the server walks at MovementSim.SpeedFor(walk speed, moveSpeedPercent); the client takes
        // the stat from its OwnStats and predicts the same steps.
        var tables = TestCombat.Tables();
        var combat = TestCombat.Rules(tables);
        var harness = new WorldHarness(actions: ActionCatalog.From(tables), combat: combat);
        harness.Characters.AutoCreateClassId = "warrior";
        var client = new Client(harness, TestMaps.Open().Grid, harness.World.Actions);
        for (int i = 0; i < 3; i++)
            client.Tick(MoveKeys.D, 16_384, InputActions.None, 0);
        var before = client.Server.Movement.Position;
        client.Tick(MoveKeys.D, 16_384, InputActions.None, 0);
        int free = (client.Server.Movement.Position - before).X;

        Assert.True(combat.Statuses.TryGet("cold", out var cold));
        Assert.True(harness.World.Statuses!.Apply(client.Server, client.Server, cold, harness.World.ServerTick));
        client.Predictor.MoveSpeedPercent = client.Server.Combatant!.Stats.Get(combat.Config.Ids.MoveSpeedPercent); // OwnStats
        before = client.Server.Movement.Position;
        for (int i = 0; i < 10; i++)
            client.Tick(MoveKeys.D, 16_384, InputActions.None, 0);

        Assert.Equal(-30, client.Predictor.MoveSpeedPercent);
        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(10 * MovementSim.StepPerTick(MovementSim.SpeedFor(400, -30), 20), (client.Server.Movement.Position - before).X);
        Assert.True(free > MovementSim.StepPerTick(MovementSim.SpeedFor(400, -30), 20));
    }

    [Fact]
    public void A_lost_press_is_corrected_and_actions_replay_with_movement()
    {
        var harness = Harness();
        var client = new Client(harness, TestMaps.Open().Grid);
        for (int i = 0; i < 3; i++)
            client.Tick(MoveKeys.W, 0, InputActions.None, 0);
        // A click inside one lost frame never reaches the server: the client predicted an attack and standing still,
        // the server kept walking. The correction takes the server's idle state and replays the walk.
        client.Tick(MoveKeys.W, 0, InputActions.Attack, 0, lose: true);
        for (int i = 0; i < 3; i++)
            client.Tick(MoveKeys.W, 0, InputActions.None, 0);

        Assert.Contains(client.Results, r => r.Outcome == ReconcileOutcome.Corrected && r.ActionMismatch);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.False(client.Server.Actions.IsBusy);
        Assert.False(client.Predictor.Actions.IsBusy);
        Assert.Contains(client.LocalPhases, p => p.IsCorrection && p.Phase == ActionState.Idle);
    }

    [Fact]
    public void A_held_button_still_presses_on_the_server_when_its_first_frame_is_lost()
    {
        var harness = Harness();
        var client = new Client(harness, TestMaps.Open().Grid);
        client.Tick(MoveKeys.W, 0, InputActions.None, 0);
        client.Tick(MoveKeys.W, 0, InputActions.Attack, 0, lose: true);
        for (int i = 0; i < 3; i++)
            client.Tick(MoveKeys.W, 0, InputActions.Attack, 0);

        // The server saw the press one frame later; the client rebased onto that attack and agrees from there on.
        Assert.True(client.Server.Actions.IsBusy);
        Assert.Equal(ActionMachine.ToWire(client.Server.Actions), ActionMachine.ToWire(client.Predictor.Actions));
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(1, client.Predictor.Corrections);
    }

    [Fact]
    public void A_press_the_client_missed_is_taken_from_the_server()
    {
        var harness = Harness();
        var client = new Client(harness, TestMaps.Open().Grid);
        client.Tick(MoveKeys.W, 0, InputActions.None, 0);
        // The server applies an attack the client did not predict (the frame changed on the way, standing in for any
        // divergence): the snapshot's action state rebases the prediction, which then stands still with the server.
        client.Tick(MoveKeys.W, 0, InputActions.None, 0, tamper: f => { f.Actions = InputActions.Attack; return f; });
        var corrected = client.Results[^1];
        Assert.Equal((ReconcileOutcome.Corrected, true), (corrected.Outcome, corrected.ActionMismatch));
        Assert.Equal(Catalog.Loadout(null).AttackIndex, client.Predictor.Actions.ActionIndex);
        for (int i = 0; i < 8; i++)
            client.Tick(MoveKeys.W, 0, InputActions.None, 0);
        Assert.Equal(1, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Contains(client.LocalPhases, p => p.IsCorrection && p.ActionId == "attack");
    }

    [Fact]
    public void A_mashed_attack_chain_predicts_like_the_server_over_a_100_ms_link()
    {
        // R-3.1-10 with the committed tables: the warrior's four hits run on the client and the server from the same input
        // frames, a tick late each way (50 ms up, 50 ms down: a 100 ms round trip), with no action correction.
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var harness = new WorldHarness(actions: catalog, combat: TestCombat.Rules(tables)); // the combat rules give the player its class
        var client = new Client(harness, TestMaps.Open().Grid, catalog, linkTicks: 1);
        client.Predictor.SetLoadout(catalog.Loadout("warrior"));
        for (int i = 0; i < 4; i++)
            client.Tick(MoveKeys.None, 0, InputActions.None, 0);
        for (int i = 0; i < 60; i++)
            client.Tick(MoveKeys.None, 0, i % 2 == 0 && i < 44 ? InputActions.Attack : InputActions.None, 0);

        string[] chain = ["attack.warrior", "attack.warrior.2", "attack.warrior.3", "attack.warrior.4"];
        Assert.Equal(chain.Select(id => catalog.Find(id)!.NetId),
            client.LocalPhases.Where(p => p.Phase == ActionState.Startup && !p.IsCorrection).Select(p => (int)p.ActionNetId).Take(4));
        Assert.DoesNotContain(client.Results, r => r.ActionMismatch);
        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(ActionMachine.ToWire(client.Server.Actions), ActionMachine.ToWire(client.Predictor.Actions));
    }

    /// <summary>
    /// The Unity client's prediction core against the harness, in process: one frame per tick, no loss unless asked. The
    /// catalog defaults to the test actions; the harness must run the same one. <paramref name="linkTicks"/> delays the
    /// frames on their way to the server and the snapshots on their way back by that many ticks each. With
    /// <paramref name="bodyTables"/> it also keeps an <see cref="EntityRegistry"/> of the snapshots and predicts body
    /// collision (R-2.2-07) with those tables' <c>BodyCollisionSettings</c> and radii, as the Unity client does.
    /// </summary>
    internal sealed class Client : IActionPhaseListener
    {
        private readonly WorldHarness _harness;
        private readonly int _linkTicks;
        private readonly Queue<InputFrame?> _upstream = new();
        private readonly Queue<(DecodedSnapshot Snapshot, byte[] Bytes)> _downstream = new();

        public Client(WorldHarness harness, WorldGrid grid, ActionCatalog? catalog = null, int linkTicks = 0,
            ConfigSet? bodyTables = null)
        {
            _harness = harness;
            _linkTicks = linkTicks;
            Server = harness.Join(1);
            var settings = new PredictionSettings(
                new StartingValuesM1 { TickRateHz = 20, MoveSpeedCmPerSec = 400, InputRedundancy = 3, InterpolationDelayMs = 100 },
                new ClientPrediction
                {
                    CorrectionToleranceCm = 2, CorrectionSnapCm = 300, CorrectionSmoothingMs = 100, MaxPendingInputs = 64,
                    MaxTicksPerFrame = 4, InterpolationClockCatchUpMs = 500,
                },
                bodyTables?.Single<BodyCollisionSettings>());
            Predictor = new LocalPlayerPredictor(settings, grid, catalog ?? Catalog) { Listener = this };
            if (bodyTables != null)
            {
                Registry = new EntityRegistry(settings);
                Predictor.UseBodies(new RegistryBodyBlockers(Registry, BodyRadii.From(bodyTables)));
                Predictor.OwnBodyRadiusRaw = Server.Combatant!.BodyRadiusRaw;
            }
            Reconcile();
            Assert.True(Predictor.HasState);
        }

        public Player Server { get; }
        public LocalPlayerPredictor Predictor { get; }

        /// <summary>The remote entities of the delivered snapshots; null without body collision.</summary>
        public EntityRegistry? Registry { get; }
        public List<ActionPhaseChange> LocalPhases { get; } = [];
        public List<ReconcileResult> Results { get; } = [];

        public void Tick(MoveKeys keys, ushort facing, InputActions actions, byte slot, bool lose = false,
            Func<InputFrame, InputFrame>? tamper = null)
        {
            var frame = Predictor.Predict(keys, facing, actions, slot);
            _upstream.Enqueue(lose ? null : tamper != null ? tamper(frame) : frame);
            if (_upstream.Count > _linkTicks && _upstream.Dequeue() is { } sent)
                _harness.SendInputs(1, sent);
            _harness.Ticks(1);
            _downstream.Enqueue((_harness.LastSnapshot(1), _harness.Transport.LastSnapshotBytes(1)));
            if (_downstream.Count > _linkTicks)
            {
                var (snapshot, bytes) = _downstream.Dequeue();
                Reconcile(snapshot, bytes);
            }
        }

        private void Reconcile() => Reconcile(_harness.LastSnapshot(1), _harness.Transport.LastSnapshotBytes(1));

        private void Reconcile(DecodedSnapshot snapshot, byte[] bytes)
        {
            // As PredictionDevClient: the registry takes the snapshot first, so prediction collides with its positions.
            Registry?.ApplySnapshot(bytes, out _, out _, out _);
            Predictor.EntityId = snapshot.Self.EntityId;
            Results.Add(Predictor.Reconcile(snapshot.Header, SnapshotAxes.GroundState(snapshot.Self),
                ActionWireState.From(snapshot.Self)));
        }

        public void OnActionPhase(in ActionPhaseChange change) => LocalPhases.Add(change);

        public void OnActionEffect(in ActionEffectEvent effect)
        {
        }
    }

    private sealed class Recorder : IEventConsumer<ActionStarted>
    {
        public List<ActionStarted> Events { get; } = [];
        public List<long> Actors { get; } = [];

        public void Handle(in EventEnvelope envelope, in ActionStarted payload)
        {
            Events.Add(payload);
            Actors.Add(envelope.ActorCharacterId);
        }
    }

    private sealed class EffectRecorder : IActionEffectSink
    {
        private TickPhase _phase;

        public EffectRecorder(GameWorld world) => world.PhaseStarted = phase => _phase = phase;

        public List<(uint Entity, string Effect, uint Tick, TickPhase Phase)> Seen { get; } = [];

        public void OnEffect(Entity entity, in FiredEffect effect, uint tick) =>
            Seen.Add((entity.Id, effect.Effect.Def.Id, tick, _phase));
    }
}
