using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using static GameServer.Tests.Support.TestWorlds;
using MoveKeys = Shared.Protocol.MoveKeys;

namespace GameServer.Tests.World;

/// <summary>
/// The dodge's dash with the committed tables (R-3.3-02, spec 12.7; first play session P2): the dodge moves the player
/// StartingValuesM1.dodgeDistanceCm over its Active phase, which is its i-frame window, with world collision and without a
/// speed violation, and client prediction agrees with the server. Its direction is data (ActionEffectDef dashDirection):
/// R-3.3-02 (user decision 2026-10-26) puts the dodge on the movement keys held at the press, backward without a key; the
/// other modes are checked on a copy of the tables.
/// </summary>
public class DodgeWorldTests
{
    private const ushort North = 0;
    private const ushort East = 16_384;

    // 20 x 10 cells of 100 cm from (0, 0); column 10 (x 1000-1100 cm) is a wall. The player spawns at (650, 450).
    private static WorldMap WalledMap() =>
        TestMaps.Map(TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 0, walkable: (x, _) => x != 10),
            [TestMaps.SpawnMarker(650, 450)]);

    private static ConfigSet Tables(DashDirection direction = DashDirection.Facing, int offsetDeg = 0)
    {
        var tables = TestCombat.Tables();
        Assert.True(tables.TryGet(typeof(ActionEffectDef), out var effects));
        var dash = effects.Rows.Cast<ActionEffectDef>().Single(e => e.ActionId == "dodge");
        dash.DashDirection = direction;
        dash.DirectionOffsetDeg = offsetDeg;
        return tables;
    }

    private static int DodgeDistanceCm(ConfigSet tables) => tables.Single<StartingValuesM1>().DodgeDistanceCm;

    /// <summary>A warrior (its ActionBindings row has the dodge) with the combat rules of <paramref name="tables"/>.</summary>
    private static WorldHarness Harness(ConfigSet tables, ActionCatalog catalog, WorldMap? map = null) =>
        new(map: map ?? TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0)]), actions: catalog,
            values: TestSessions.Values(lingerOutOfCombatMs: 0), combat: TestCombat.Rules(tables));

    /// <summary>Presses the dodge with <paramref name="keys"/> held, then holds nothing until it ended.</summary>
    private static void Dodge(WorldHarness harness, ActionCatalog catalog, MoveKeys keys, ushort facing, List<bool>? iFramesWhileMoving = null)
    {
        var player = harness.World.Entities.OfType<GameServer.Entities.Player>().Single();
        ushort sequence = 1;
        harness.SendInputs(1, Frame(sequence, keys, facing, InputActions.Dodge));
        harness.Ticks(1);
        int total = catalog.Find("dodge")!.TotalTicks;
        for (int i = 0; i < total + 1; i++)
        {
            var before = player.Movement.Position;
            harness.SendInputs(1, Frame(++sequence, facing: facing));
            harness.Ticks(1);
            if (player.Movement.Position != before)
                iFramesWhileMoving?.Add(player.Actions.InIFrames);
        }
        Assert.False(player.Actions.IsBusy);
    }

    [Fact]
    public void The_committed_dodge_dashes_the_dodge_length_over_its_active_phase_and_keeps_its_i_frames()
    {
        // Spec 12.7: 0.5 s, 250 ms of i-frames, about 4 m. The dash spans exactly the Active phase, the i-frame window.
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var dodge = catalog.Find("dodge")!;
        var dash = Assert.Single(dodge.Effects, e => e.Kind == EffectKind.Dash);
        var values = tables.Single<StartingValuesM1>();

        Assert.Equal(FixedPoint.FromCm(values.DodgeDistanceCm), dash.DashDistanceRaw);
        Assert.Equal((dodge.StartupEnd, dodge.ActiveEnd - dodge.StartupEnd), (dash.FireTick, dash.DashTicks));
        Assert.Equal((values.DodgeDurationMs, values.DodgeIFramesMs), (dodge.Def.TotalMs(), dodge.Def.IFramesMs));
        for (int tick = 0; tick < dodge.TotalTicks; tick++)
            Assert.Equal(tick >= dash.FireTick && tick < dash.FireTick + dash.DashTicks, dodge.HasIFramesAt(tick));
    }

    [Theory]
    [InlineData(North, 0, -1)] // R-3.3-02: no movement key held, backward from the facing
    [InlineData(East, -1, 0)]
    public void The_server_dodge_moves_the_player_its_length_during_its_i_frames_without_a_speed_violation(ushort facing, int dirX, int dirY)
    {
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var harness = Harness(tables, catalog);
        var player = harness.Join(1);
        var start = player.Movement.Position;
        var iFrames = new List<bool>();

        Dodge(harness, catalog, MoveKeys.None, facing, iFrames);

        int distance = DodgeDistanceCm(tables);
        Assert.Equal(start + FixVec2.FromCm(dirX * distance, dirY * distance), player.Movement.Position);
        Assert.Equal(catalog.Find("dodge")!.Effects[0].DashTicks, iFrames.Count);
        Assert.All(iFrames, inside => Assert.True(inside)); // every step of the move is inside the i-frame window
        // The speed-check reference moved with the dash: walking on afterwards is no violation either.
        for (ushort i = 0; i < 10; i++)
        {
            harness.SendInputs(1, Frame((ushort)(100 + i), MoveKeys.W, North));
            harness.Ticks(1);
        }
        Assert.Equal(0, harness.World.SpeedViolations);
    }

    [Theory]
    [InlineData(MoveKeys.D, 1, 0)]
    [InlineData(MoveKeys.W | MoveKeys.A, -1, 1)]
    public void The_committed_dodge_goes_along_the_keys_held_at_the_press_whatever_the_facing(MoveKeys keys, int dirX, int dirY)
    {
        // R-3.3-02 (user decision 2026-10-26): the movement input as MovementSim resolves it, not the facing.
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var harness = Harness(tables, catalog);
        var player = harness.Join(1);
        var start = player.Movement.Position;

        Dodge(harness, catalog, keys, North);

        var walked = MovementSim.Step(new MovementState(start, North), new MovementInput((Shared.Simulation.MoveKeys)(byte)keys, North),
            false, TestWorlds.Settings().MoveSpeedCmPerSec, TestWorlds.Settings().TickRateHz).Position;
        ushort heading = FixedTrig.AngleOf(dirX, dirY);
        Assert.Equal(walked + FixedTrig.Along(heading, FixedPoint.FromCm(DodgeDistanceCm(tables))), player.Movement.Position);
    }

    [Fact]
    public void A_dodge_into_a_wall_stops_before_it()
    {
        var tables = TestCombat.Tables();
        var catalog = ActionCatalog.From(tables);
        var harness = Harness(tables, catalog, WalledMap());
        var player = harness.Join(1);

        Dodge(harness, catalog, MoveKeys.None, 49_152); // facing west, backward east: 650 + 400 would end in the wall at 1000

        Assert.Equal(1_000 * FixedPoint.RawPerCm - 1, player.Movement.Position.X);
        Assert.Equal(450, player.YCm);
        Assert.Equal(0, harness.World.SpeedViolations);
    }

    [Theory]
    // dashDirection "facing": the move keys never steer it.
    [InlineData(DashDirection.Facing, 0, MoveKeys.D, North, 0, 1)]
    [InlineData(DashDirection.Facing, 0, MoveKeys.None, North, 0, 1)]
    // "moveInput": along the keys held at the press, whatever the facing.
    [InlineData(DashDirection.MoveInput, 0, MoveKeys.D, North, 1, 0)]
    [InlineData(DashDirection.MoveInput, 0, MoveKeys.S, East, 0, -1)]
    [InlineData(DashDirection.MoveInput, 180, MoveKeys.A, North, -1, 0)]
    // "moveInput" with no key held: the locked facing turned by directionOffsetDeg (forward, or a step back).
    [InlineData(DashDirection.MoveInput, 0, MoveKeys.None, East, 1, 0)]
    [InlineData(DashDirection.MoveInput, 180, MoveKeys.None, North, 0, -1)]
    public void The_dodge_direction_follows_the_rows_dash_direction(DashDirection direction, int offsetDeg, MoveKeys keys,
        ushort facing, int dirX, int dirY)
    {
        var tables = Tables(direction, offsetDeg);
        var catalog = ActionCatalog.From(tables);
        var harness = Harness(tables, catalog);
        var player = harness.Join(1);
        var start = player.Movement.Position;

        Dodge(harness, catalog, keys, facing);

        // Phase 2 walks the press tick's keys before phase 3 starts the dodge (spec 1.6), then the dash adds its length.
        var walked = MovementSim.Step(new MovementState(start, facing), new MovementInput((Shared.Simulation.MoveKeys)(byte)keys, facing),
            false, TestWorlds.Settings().MoveSpeedCmPerSec, TestWorlds.Settings().TickRateHz).Position;
        int distance = DodgeDistanceCm(tables);
        Assert.Equal(walked + FixVec2.FromCm(dirX * distance, dirY * distance), player.Movement.Position);
        Assert.Equal(facing, player.Movement.Facing); // the dodge never turns the player
    }

    [Theory]
    [InlineData(DashDirection.Facing)]
    [InlineData(DashDirection.MoveInput)]
    public void Prediction_of_dodges_matches_the_server_without_corrections(DashDirection direction)
    {
        var tables = Tables(direction);
        var catalog = ActionCatalog.From(tables);
        var harness = Harness(tables, catalog, WalledMap());
        var client = new WorldActionsTests.Client(harness, WalledMap().Grid, catalog);
        client.Predictor.SetLoadout(catalog.Loadout("warrior")); // the client takes it from its own spawn's class
        // Walk, dodge sideways while facing north, walk, dodge east into the wall, dodge back west with no key held.
        var script = new List<(MoveKeys Keys, ushort Facing, InputActions Actions)>();
        for (int i = 0; i < 3; i++) script.Add((MoveKeys.W, North, InputActions.None));
        script.Add((MoveKeys.A, North, InputActions.Dodge));
        for (int i = 0; i < 12; i++) script.Add((MoveKeys.None, North, InputActions.None));
        for (int i = 0; i < 3; i++) script.Add((MoveKeys.D, East, InputActions.None));
        script.Add((MoveKeys.D, East, InputActions.Dodge));
        for (int i = 0; i < 12; i++) script.Add((MoveKeys.None, East, InputActions.None));
        script.Add((MoveKeys.None, 49_152, InputActions.Dodge));
        for (int i = 0; i < 12; i++) script.Add((MoveKeys.None, 49_152, InputActions.None));

        var start = client.Server.Movement.Position;
        foreach (var step in script)
            client.Tick(step.Keys, step.Facing, step.Actions, 0);

        Assert.Equal(0, client.Predictor.Corrections);
        Assert.Equal(client.Server.Movement.Position, client.Predictor.State.Position);
        Assert.NotEqual(start, client.Server.Movement.Position);
        Assert.Equal(3, client.LocalPhases.Count(p => p.Phase == ActionState.Startup && p.ActionId == "dodge"));
        Assert.Equal(0, harness.World.SpeedViolations);
    }

    [Fact]
    public void A_rebased_dodge_keeps_the_predicted_move_keys()
    {
        // Snapshots carry the facing, not the keys held at the press: a correction during a moveInput dodge keeps the keys
        // the client pressed with, so the replay still dashes sideways.
        var catalog = ActionCatalog.From(Tables(DashDirection.MoveInput));
        var predicted = new ActionMachineState(catalog, catalog.Loadout("warrior"));
        var input = new ActionInput(true, InputActions.Dodge, 0, North, 1, Shared.Simulation.MoveKeys.D);
        ActionMachine.Step(predicted, input, UnlimitedActionResources.Instance, new FiredEffect[ActionMachine.MaxFiredPerStep]);
        var wire = ActionMachine.ToWire(predicted);

        ActionMachine.Rebase(predicted, wire, North);

        Assert.Equal(Shared.Simulation.MoveKeys.D, predicted.StartKeys);
        var other = new ActionMachineState(catalog, catalog.Loadout("warrior"));
        ActionMachine.Rebase(other, wire, North); // the client never pressed it: along the facing
        Assert.Equal(Shared.Simulation.MoveKeys.None, other.StartKeys);
    }
}
