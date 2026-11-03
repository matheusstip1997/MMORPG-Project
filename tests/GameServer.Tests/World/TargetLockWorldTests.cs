using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.TestWorlds;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// R-2.2-06, M3 decision 15: the lock reaches the server as the frames' locked flag plus a reliable
/// <see cref="TargetLockChanged"/>; a locked frame's facing is checked against the server's angle to the locked target and
/// clamped to <c>StartingValuesM1.lockFacingToleranceDeg</c> (10 in the test settings) around it.
/// </summary>
public class TargetLockWorldTests
{
    private sealed class RecordingSink : IMovementViolationSink
    {
        public List<(uint EntityId, ushort Sent, ushort Allowed, bool NoTarget)> Clamps { get; } = [];

        public void SpeedViolation(uint serverTick, uint entityId, long accountId, long distanceCm, long allowedCm)
        {
        }

        public void LockFacingClamp(uint serverTick, uint entityId, long accountId, ushort sentFacing, ushort allowedFacing, bool noTarget) =>
            Clamps.Add((entityId, sentFacing, allowedFacing, noTarget));
    }

    /// <summary>Monsters never see players: they stay where they spawned.</summary>
    private sealed class PlayersUnseen : IVisibilityQuery
    {
        public bool CanSee(Entity observer, Entity target) => !(observer is Monster && target is Player);
    }

    private static (WorldHarness Harness, Player Player, Monster Demon) Setup(IMovementViolationSink? violations = null)
    {
        // The player at the origin, a demon 10 m east of it.
        var tables = TestCombat.Tables();
        var markers = new List<WorldMarker> { TestMaps.SpawnMarker(0, 0), TestCombat.MonsterMarker(1_000, 0, id: "demon.a") };
        var harness = new WorldHarness(map: TestMaps.Map(TestMaps.Open().Grid, markers), visibility: new PlayersUnseen(), violations: violations,
            actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables, [TestCombat.Point("demon.a", TestCombat.MonsterId)]));
        var player = harness.Join(1);
        harness.Ticks(1);
        return (harness, player, harness.World.Entities.OfType<Monster>().Single());
    }

    /// <summary>
    /// The lock message: kept on the session's input queue, applied from the frame <paramref name="fromSequence"/> on (protocol
    /// 40; 0 is not newer than any frame these tests send, so it applies to the next one).
    /// </summary>
    private static void Lock(WorldHarness harness, uint target, ushort fromSequence = 0) =>
        harness.Router.OnDataReceived(1, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.TargetLockChanged,
            new TargetLockChanged { TargetEntityId = target, FromSequence = fromSequence }));

    private static ushort Degrees(double degrees) => Quantize.RoundTripAngle((ushort)(degrees * 65536 / 360), InputPacket.FacingBits);

    [Fact]
    public void A_locked_facing_within_the_tolerance_is_used_as_sent()
    {
        var (harness, player, demon) = Setup();
        Lock(harness, demon.Id);

        harness.SendInputs(1, Frame(1, MoveKeys.None, Degrees(95), locked: true)); // 5 degrees off the demon in the east
        harness.Ticks(1);

        Assert.Equal(Degrees(95), player.Movement.Facing);
        Assert.Equal(0, harness.World.LockFacingClamps);
        Assert.Equal(0, harness.World.LockFacingUnchecked);
    }

    [Fact]
    public void A_locked_facing_beyond_the_tolerance_is_clamped_toward_the_target_and_counted()
    {
        var sink = new RecordingSink();
        var (harness, player, demon) = Setup(sink);
        Lock(harness, demon.Id);

        harness.SendInputs(1, Frame(1, MoveKeys.W, Degrees(150), locked: true)); // 60 degrees off
        harness.Ticks(1);

        // Clamped to 10 degrees past the demon's angle (90), and W walks along the clamped facing.
        double facing = player.Movement.Facing * 360.0 / 65536;
        Assert.InRange(facing, 99.5, 100.5);
        Assert.Equal(1, harness.World.LockFacingClamps);
        var reported = Assert.Single(sink.Clamps); // telemetry LockFacingClamp, beside SpeedViolation
        Assert.Equal((player.Id, Degrees(150), player.Movement.Facing, false), (reported.EntityId, reported.Sent, reported.Allowed, reported.NoTarget));
        double walked = Math.Atan2(player.Movement.Position.X, player.Movement.Position.Y) * 180 / Math.PI;
        Assert.InRange(walked, 99.4, 100.6);
    }

    [Fact]
    public void Without_a_known_target_a_locked_frame_is_applied_unlocked_and_counted()
    {
        // Wave 2 fixer: a locked flag the server cannot check gives no free heading; the frame is an unlocked one (world keys,
        // the body faces its walk), so a modified client that never names a target gains nothing.
        var sink = new RecordingSink();
        var (harness, player, demon) = Setup(sink);

        harness.SendInputs(1, Frame(1, MoveKeys.None, Degrees(200), locked: true)); // no TargetLockChanged yet
        harness.Ticks(1);
        Assert.Equal(0, player.Movement.Facing); // standing unlocked keeps the facing it had
        Assert.Equal(1, harness.World.LockFacingUnchecked);
        Assert.True(Assert.Single(sink.Clamps).NoTarget);

        harness.SendInputs(1, Frame(2, MoveKeys.D, Degrees(200), locked: true));
        harness.Ticks(1);
        Assert.Equal(16_384, player.Movement.Facing); // D walks east and faces it, not 200 degrees or relative to it
        Assert.True(player.Movement.Position.X > 0 && player.Movement.Position.Y == 0);

        Lock(harness, demon.Id);
        Lock(harness, 0); // cleared again
        harness.SendInputs(1, Frame(3, MoveKeys.None, Degrees(250), locked: true));
        harness.Ticks(1);
        Assert.Equal(16_384, player.Movement.Facing);
        Assert.Equal(3, harness.World.LockFacingUnchecked);
        Assert.Equal(0, harness.World.LockFacingClamps);
    }

    [Fact]
    public void A_target_killed_and_removed_is_still_checked_where_the_client_drew_it()
    {
        // The client keeps its lock until it sees the death, a round trip and its view lag later: those frames are neither
        // applied unlocked (a correction at every kill) nor unchecked.
        var (harness, player, demon) = Setup();
        Lock(harness, demon.Id);
        harness.SendInputs(1, Frame(1, MoveKeys.None, Degrees(92), locked: true));
        harness.Ticks(1);
        demon.Combatant!.Die(harness.World.ServerTick);
        for (int i = 0; i < 200 && harness.World.Entities.Contains(demon); i++)
            harness.Ticks(1);
        Assert.DoesNotContain(demon, harness.World.Entities);

        harness.SendInputs(1, Frame(2, MoveKeys.None, Degrees(88), locked: true));
        harness.Ticks(1);
        harness.SendInputs(1, Frame(3, MoveKeys.None, Degrees(150), locked: true));
        harness.Ticks(1);

        Assert.Equal(1, harness.World.LockFacingClamps); // frame 3 only, against where the demon fell
        Assert.Equal(0, harness.World.LockFacingUnchecked);
        Assert.InRange(player.Movement.Facing * 360.0 / 65536, 99.5, 100.5);
    }

    [Fact]
    public void Frames_queued_before_a_lock_cycle_keep_the_target_they_were_aimed_at()
    {
        // Wave 2 fixer: the lock message arrives as soon as it is sent, while frames aimed at the old target still wait in the
        // queue. TargetLockChanged.FromSequence keeps them on it, so cycling from the demon (east) to a player (north) never
        // checks a frame against the other target.
        var (harness, player, demon) = Setup(new RecordingSink());
        var north = harness.Join(2);
        north.Movement = new MovementState(FixVec2.FromCm(0, 1_000), 0);
        harness.Ticks(1);
        Lock(harness, demon.Id, fromSequence: 1);

        harness.SendInputs(1, Frame(1, MoveKeys.None, Degrees(90), locked: true));
        harness.SendInputs(1, Frame(2, MoveKeys.None, Degrees(90), locked: true));
        Lock(harness, north.Id, fromSequence: 3); // a Tab cycle: frames 3 and on face north
        harness.SendInputs(1, Frame(3, MoveKeys.None, Degrees(0), locked: true));
        harness.SendInputs(1, Frame(4, MoveKeys.None, Degrees(0), locked: true));
        Lock(harness, 0, fromSequence: 5); // and a clear, before frame 3 is even dequeued
        for (int i = 0; i < 4; i++)
            harness.Ticks(1);

        Assert.Equal(0, player.Inputs.Count);
        Assert.Equal(0, harness.World.LockFacingClamps);
        Assert.Equal(0, harness.World.LockFacingUnchecked);
        Assert.Equal(Degrees(0), player.Movement.Facing);
    }

    [Fact]
    public void An_honest_client_locked_on_a_fast_close_target_is_never_clamped_also_through_catch_up_frames()
    {
        // Wave 2 fixer (M3 decision 15, R-2.2-06): the client aims at the target where it draws it, interpolated between two
        // snapshots at its view clock (view tick plus SubTick/64), from its predicted position; on a catch-up tick each frame
        // starts where the one before ended. The target runs past 80 cm away at 20 cm per tick.
        var sink = new RecordingSink();
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var map = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0)]);
        var harness = new WorldHarness(Settings(inputBacklogTicks: 1), map: map, violations: sink,
            values: TestSessions.Values(lingerOutOfCombatMs: 0), actions: catalog, combat: TestCombat.Rules(tables));
        var client = new WorldActionsTests.Client(harness, map.Grid, catalog);
        var target = harness.Join(2);
        target.Movement = new MovementState(FixVec2.FromCm(80, -300), 0);
        var drawn = new Dictionary<uint, FixVec2>(); // what the snapshots show: the end of each tick
        ushort targetSequence = 0;
        void Tick()
        {
            harness.SendInputs(2, Frame(++targetSequence, MoveKeys.W)); // the target runs north
            client.Step();
            drawn[harness.World.ServerTick - 1] = target.Movement.Position;
        }
        for (int i = 0; i < 3; i++)
            Tick();
        Lock(harness, target.Id, fromSequence: (ushort)(client.Predictor.LastSequence + 1));

        const byte SubTick = 60;
        int wouldClamp = 0; // frames the integer-view-tick check from the pre-tick position would have clamped
        for (int i = 0; i < 30; i++)
        {
            var preTick = client.Predictor.State.Position;
            int frames = i % 5 == 4 ? 2 : 1; // a hitch every fifth tick: two frames, applied together (catch-up)
            for (int f = 0; f < frames; f++)
            {
                uint view = harness.World.ServerTick - 3; // the render tick two ticks behind the newest snapshot
                var from = drawn[view];
                var to = drawn[view + 1];
                double x = from.X + (to.X - from.X) * SubTick / 64.0, y = from.Y + (to.Y - from.Y) * SubTick / 64.0;
                var self = client.Predictor.State.Position;
                ushort facing = AngleTo(x - self.X, y - self.Y);
                int off = Math.Abs(unchecked((short)(ushort)(facing - AngleTo(from.X - preTick.X, from.Y - preTick.Y))));
                if (off > 10 * 65536 / 360)
                    wouldClamp++;
                client.Send(MoveKeys.D, facing, view, SubTick, locked: true); // strafing around it
            }
            Tick();
        }

        Assert.True(wouldClamp > 0, "the scenario must be one the old check clamped");
        Assert.True(harness.World.InputCatchUpFrames > 0);
        Assert.Empty(sink.Clamps);
        Assert.Equal(0, harness.World.LockFacingClamps + harness.World.LockFacingUnchecked);
        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
    }

    [Fact]
    public void A_lock_message_late_for_the_first_locked_frame_costs_one_correction_at_most()
    {
        // The honest first-frame case of the unknown-target rule: the server applies that frame unlocked, the client predicted
        // it locked; the next snapshot corrects once and every later frame is checked and predicted alike.
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var map = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0)]);
        var harness = new WorldHarness(map: map, values: TestSessions.Values(lingerOutOfCombatMs: 0), actions: catalog,
            combat: TestCombat.Rules(tables));
        var client = new WorldActionsTests.Client(harness, map.Grid, catalog);
        var target = harness.Join(2);
        target.Movement = new MovementState(FixVec2.FromCm(500, 0), 0);
        harness.Ticks(10); // past the history, so view tick 0 (the oldest entry) sees it there
        ushort first = (ushort)(client.Predictor.LastSequence + 1);

        client.Tick(MoveKeys.W, Degrees(90), InputActions.None, 0, locked: true); // the lock message is still on its way
        Lock(harness, target.Id, fromSequence: first);
        for (int i = 0; i < 10; i++)
            client.Tick(MoveKeys.W, Degrees(90), InputActions.None, 0, locked: true);

        Assert.Equal(1, harness.World.LockFacingUnchecked);
        Assert.Equal(0, harness.World.LockFacingClamps);
        Assert.InRange(client.Predictor.Corrections, 0, 1);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(client.Server.Movement.Facing, client.Predictor.State.Facing);
    }

    /// <summary>The binary angle (0 = +Y, clockwise) the client's float math gives for a direction.</summary>
    private static ushort AngleTo(double x, double y) =>
        (ushort)((int)Math.Round(Math.Atan2(x, y) * 32768 / Math.PI) & 0xFFFF);

    [Fact]
    public void Prediction_matches_the_server_across_lock_on_and_off_with_mouse_aimed_attacks_and_keyed_dodges()
    {
        // M3 decision 15: free walks (the body faces the walk), attacks at the mouse (the body turns and stays), locked walks
        // and a locked dodge relative to the facing, then free again; the client predicts every frame with no correction.
        var tables = TestCombat.Tables();
        Assert.True(tables.TryGet(typeof(Shared.Data.Tables.ActionEffectDef), out var effects));
        var dodge = effects.Rows.Cast<Shared.Data.Tables.ActionEffectDef>().Single(e => e.ActionId == "dodge");
        dodge.DashDirection = Shared.Data.Tables.DashDirection.MoveInput;
        dodge.DirectionOffsetDeg = 180;
        var catalog = ActionCatalog.From(tables);
        var map = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0)]);
        var harness = new WorldHarness(map: map, actions: catalog, values: TestSessions.Values(lingerOutOfCombatMs: 0),
            combat: TestCombat.Rules(tables));
        var client = new WorldActionsTests.Client(harness, map.Grid, catalog);
        client.Predictor.SetLoadout(catalog.Loadout("warrior"));
        var script = new List<(MoveKeys Keys, ushort Facing, ushort Aim, InputActions Actions, bool Locked)>();
        for (int i = 0; i < 4; i++) script.Add((MoveKeys.W | MoveKeys.D, 0, 50_000, InputActions.None, false));
        script.Add((MoveKeys.None, 0, 23_000, InputActions.Attack, false));                 // the attack turns the body to 23000
        for (int i = 0; i < 16; i++) script.Add((MoveKeys.None, 0, 23_000, InputActions.None, false));
        for (int i = 0; i < 6; i++) script.Add((MoveKeys.D, 10_000, 40_000, InputActions.None, true)); // around the target
        script.Add((MoveKeys.S, 10_000, 40_000, InputActions.Dodge, true));                  // away from it
        for (int i = 0; i < 12; i++) script.Add((MoveKeys.W, 10_000, 40_000, InputActions.None, true));
        for (int i = 0; i < 6; i++) script.Add((MoveKeys.A, 10_000, 40_000, InputActions.None, false)); // free again: west

        // The locked target 10 m out at about the facing 10000; locked frames face it from the predicted position, as the
        // client does (wave 2 fixer: a locked frame without a target the server knows is applied unlocked).
        var target = harness.Join(2);
        target.Movement = new MovementState(FixVec2.FromCm(831, 556), 0);
        harness.Ticks(10);
        Lock(harness, target.Id);
        long violationsBefore = harness.World.SpeedViolations; // the target's placement above

        var start = client.Server.Movement.Position;
        foreach (var step in script)
        {
            var self = client.Predictor.State.Position;
            ushort facing = step.Locked ? AngleTo(target.Movement.Position.X - self.X, target.Movement.Position.Y - self.Y) : step.Facing;
            client.Tick(step.Keys, facing, step.Actions, 0, aim: step.Aim, locked: step.Locked);
        }
        Assert.Equal(0, harness.World.LockFacingClamps + harness.World.LockFacingUnchecked);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(client.Server.Movement.Facing, client.Predictor.State.Facing);
        Assert.Equal(49_152, client.Server.Movement.Facing); // the free walk west
        Assert.NotEqual(start, client.Server.Movement.Position);
        Assert.Equal(violationsBefore, harness.World.SpeedViolations);
    }

    [Fact]
    public void An_attack_turns_the_body_to_the_mouse_and_it_stays_until_the_walk_turns_it()
    {
        // M3 decision 15 rule 2.
        var (harness, player, _) = Setup();
        harness.SendInputs(1, Frame(1, MoveKeys.W)); // walk north
        harness.Ticks(1);
        Assert.Equal(0, player.Movement.Facing);

        var attack = Frame(2, MoveKeys.None, 23_040, InputActions.Attack); // the mouse at about 126 degrees
        harness.SendInputs(1, attack);
        harness.Ticks(1);
        Assert.Equal(23_040, player.Actions.Facing);
        Assert.Equal(23_040, player.Movement.Facing);

        for (ushort i = 3; i < 40; i++) // the attack ends; standing still keeps the facing, the mouse elsewhere
        {
            harness.SendInputs(1, Frame(i, MoveKeys.None, 50_000));
            harness.Ticks(1);
        }
        Assert.False(player.Actions.IsBusy);
        Assert.Equal(23_040, player.Movement.Facing);

        harness.SendInputs(1, Frame(40, MoveKeys.D, 50_000)); // walking turns it
        harness.Ticks(1);
        Assert.Equal(16_384, player.Movement.Facing);
    }

    [Fact]
    public void Unlocked_frames_are_never_checked_and_the_body_faces_its_walk()
    {
        var (harness, player, demon) = Setup();
        Lock(harness, demon.Id); // a lock message alone changes nothing: the frames decide

        harness.SendInputs(1, Frame(1, MoveKeys.S, Degrees(300)));
        harness.Ticks(1);

        Assert.Equal(32_768, player.Movement.Facing); // the walk's heading, not the frame's facing (the mouse)
        Assert.Equal(0, harness.World.LockFacingClamps + harness.World.LockFacingUnchecked);
    }
}
