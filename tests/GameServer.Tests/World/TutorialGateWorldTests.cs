using GameServer.Combat;
using GameServer.Persistence;
using GameServer.Progression;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Protocol;
using MoveKeys = Shared.Protocol.MoveKeys;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.World;
using static GameServer.Tests.Support.TestWorlds;

namespace GameServer.Tests.World;

/// <summary>
/// The tutorial gate in the world (R-2.3-06, R-12.3-01; WP-M2-10): server phase 2 (walk) and phase 3 (dodge) hold a player
/// to the level the progression has, with the shared rule client prediction runs too.
/// </summary>
public class TutorialGateWorldTests
{
    // 20 x 10 cells of 100 cm from (0, 0): columns 0-9 are the tutorial (region 1), columns 10+ the gated ring (region 2,
    // level 10). The player spawns 1.5 m before the boundary at x = 1000 cm, facing east.
    private const int BoundaryRaw = 1_000 * FixedPoint.RawPerCm;

    private static WorldMap GatedMap()
    {
        var grid = TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 0, walkable: (_, _) => true,
            region: (x, _) => (byte)(x >= 10 ? 2 : 1));
        return TestMaps.Map(grid, [TestMaps.SpawnMarker(850, 450, facing: 16384)],
        [
            new WorldRegion(1, "test.ring1", "openWorld", TestMaps.OpenFlags),
            new WorldRegion(2, "test.ring2", "openWorld", TestMaps.OpenFlags, MinLevel: 10),
        ]);
    }

    private static (WorldHarness Harness, GameServer.Entities.Player Player) Enter(int level, bool combat = false)
    {
        var tables = TestCombat.Tables();
        var harness = combat
            ? new WorldHarness(map: GatedMap(), actions: ActionCatalog.From(tables), combat: TestCombat.Rules(tables),
                progression: ProgressionRules.From(tables))
            : new WorldHarness(map: GatedMap(), progression: ProgressionRules.From(tables));
        harness.Characters.Add(1, 0, new CharacterState(level, 0, "", 0, 0, 0, 0, 100, 50, 100, SkillPoints: 0));
        var player = harness.Join(1);
        harness.Ticks(1);
        return (harness, player);
    }

    private static void Walk(WorldHarness harness, MoveKeys keys, int ticks, ref ushort sequence)
    {
        for (int i = 0; i < ticks; i++)
        {
            harness.SendInputs(1, Frame(++sequence, keys));
            harness.Ticks(1);
        }
    }

    [Fact]
    public void A_level_9_player_stops_at_the_gate_where_its_prediction_stops()
    {
        var (harness, player) = Enter(level: 9);
        var start = player.Movement;
        ushort sequence = 0;

        Walk(harness, MoveKeys.D, 20, ref sequence);

        Assert.Equal(BoundaryRaw - 1, player.Movement.Position.X);
        Assert.Equal("test.ring1", harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);
        var predicted = start;
        var gate = new MoverGate(harness.World.Map.Gates, 9);
        for (int i = 0; i < 20; i++)
            predicted = MovementSim.Step(predicted, new MovementInput(Shared.Simulation.MoveKeys.D, 0), false,
                Settings().MoveSpeedCmPerSec, Settings().TickRateHz, harness.World.Map.Grid, gate);
        Assert.Equal(predicted.Position, player.Movement.Position);
        Assert.Equal(0, harness.World.SpeedViolations);
    }

    [Fact]
    public void A_level_10_player_walks_into_ring_2_and_back()
    {
        var (harness, player) = Enter(level: 10);
        ushort sequence = 0;

        Walk(harness, MoveKeys.D, 20, ref sequence);
        Assert.True(player.Movement.Position.X > BoundaryRaw, $"x {player.XCm}");
        Assert.Equal("test.ring2", harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);

        Walk(harness, MoveKeys.A, 20, ref sequence);
        Assert.Equal("test.ring1", harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);
    }

    [Fact]
    public void The_gate_check_passes_a_closed_gate_and_reports_a_player_spawn_behind_it()
    {
        Assert.Empty(WorldLoader.CheckRegionGates(GatedMap()));

        var grid = TestMaps.Grid(0, 0, 100, 20, 10, baseHeightCm: 0, walkable: (_, _) => true,
            region: (x, _) => (byte)(x >= 10 ? 2 : 1));
        var spawnInside = TestMaps.Map(grid, [TestMaps.SpawnMarker(1_450, 450)],
        [
            new WorldRegion(1, "test.ring1", "openWorld", TestMaps.OpenFlags),
            new WorldRegion(2, "test.ring2", "openWorld", TestMaps.OpenFlags, MinLevel: 10),
        ]);
        // The spawn stands behind the gate, and from there a level-9 player walks the gated ring.
        var errors = WorldLoader.CheckRegionGates(spawnInside);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(WorldLoader.Codes.GateLeak, e.Code));
        Assert.Contains(errors, e => e.Message.Contains("stands in 'test.ring2'"));
    }

    [Theory]
    [InlineData(1, "test.ring1")]
    [InlineData(10, "test.ring2")]
    public void A_dodge_across_the_boundary_stops_at_the_gate_below_level_10(int level, string endRegion)
    {
        var (harness, player) = Enter(level, combat: true);
        ushort sequence = 0;

        harness.SendInputs(1, Frame(++sequence, MoveKeys.D, facing: 16384, actions: InputActions.Dodge));
        harness.Ticks(1);
        Walk(harness, MoveKeys.None, 20, ref sequence); // the whole dodge, from 1.5 m before the boundary

        Assert.Equal(endRegion, harness.World.Map.RegionByIndex(player.RegionIndex)!.Id);
        if (level < 10)
            Assert.Equal(BoundaryRaw - 1, player.Movement.Position.X);
    }
}
