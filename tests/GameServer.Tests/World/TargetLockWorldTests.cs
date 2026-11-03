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

    /// <summary>The lock message: kept on the session's input queue, read by phase 1 before that tick's frames.</summary>
    private static void Lock(WorldHarness harness, uint target) =>
        harness.Router.OnDataReceived(1, Channel.ReliableOrdered,
            ReliableMessages.Write(ReliableMessageId.TargetLockChanged, new TargetLockChanged { TargetEntityId = target }));

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
    public void Without_a_known_target_a_locked_facing_is_used_unchecked_and_counted()
    {
        var sink = new RecordingSink();
        var (harness, player, demon) = Setup(sink);

        harness.SendInputs(1, Frame(1, MoveKeys.None, Degrees(200), locked: true)); // no TargetLockChanged yet
        harness.Ticks(1);
        Assert.Equal(Degrees(200), player.Movement.Facing);
        Assert.Equal(1, harness.World.LockFacingUnchecked);
        Assert.True(Assert.Single(sink.Clamps).NoTarget);

        Lock(harness, demon.Id);
        Lock(harness, 0); // cleared again
        harness.SendInputs(1, Frame(2, MoveKeys.None, Degrees(250), locked: true));
        harness.Ticks(1);
        Assert.Equal(Degrees(250), player.Movement.Facing);
        Assert.Equal(2, harness.World.LockFacingUnchecked);
        Assert.Equal(0, harness.World.LockFacingClamps);
    }

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

        var start = client.Server.Movement.Position;
        foreach (var step in script)
            client.Tick(step.Keys, step.Facing, step.Actions, 0, aim: step.Aim, locked: step.Locked);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.Equal(client.Server.Movement.Facing, client.Predictor.State.Facing);
        Assert.Equal(49_152, client.Server.Movement.Facing); // the free walk west
        Assert.NotEqual(start, client.Server.Movement.Position);
        Assert.Equal(0, harness.World.SpeedViolations);
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
