using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace Shared.Tests.World;

/// <summary>
/// R-2.3-06, R-12.3-01: level-gated regions in the shared grid collision, the same rule for server phases 2 and 3 and client
/// prediction (WP-M2-10).
/// </summary>
public class RegionGatesTests
{
    private const int Cell = 100; // cm
    private const int RawCell = Cell * FixedPoint.RawPerCm;

    // 10 x 6 cells; digits are region indexes: 1 the tutorial (no gate), 2 the gated ring, 3 a region behind it (gated too).
    // '#' is a wall. Row 0 is the bottom line (lowest Y). The boundary 1 -> 2 is between columns 4 and 5.
    private static readonly string[] Layout =
    {
        "1111122233", // y = 0
        "1111122233",
        "11111#2233",
        "1111122233",
        "1111122233",
        "1111122233", // y = 5
    };

    private static readonly WorldGrid Grid = Build(Layout);

    private static readonly RegionGates Gates = RegionGates.FromMinLevels(new[] { 0, 0, 10, 10 });

    private static WorldGrid Build(string[] rows)
    {
        int width = rows[0].Length;
        int height = rows.Length;
        var flags = new byte[width * height];
        var regions = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                char c = rows[y][x];
                flags[y * width + x] = c == '#' ? (byte)0 : WorldGridFormat.FlagWalkable;
                regions[y * width + x] = c == '#' ? (byte)0 : (byte)(c - '0');
            }
        }
        return new WorldGrid(0, 0, Cell, width, height, 0, flags, regions, new short[width * height]);
    }

    private static FixVec2 Cm(int x, int y) => FixVec2.FromCm(x, y);

    private static MoverGate Level(int level) => new MoverGate(Gates, level);

    [Fact]
    public void A_level_9_mover_stops_just_before_the_gated_region_like_at_a_wall()
    {
        var end = GridCollision.Move(Grid, Cm(450, 50), Cm(560, 50), Level(9));

        Assert.Equal(5 * RawCell - 1, end.X);
        Assert.Equal(1, Grid.RegionIndexAt(end));
    }

    [Fact]
    public void A_level_10_mover_walks_through()
    {
        var end = GridCollision.Move(Grid, Cm(450, 50), Cm(560, 50), Level(10));

        Assert.Equal(Cm(560, 50), end);
        Assert.Equal(2, Grid.RegionIndexAt(end));
    }

    [Fact]
    public void A_diagonal_push_into_the_gate_slides_along_it()
    {
        var end = GridCollision.Move(Grid, Cm(480, 150), Cm(530, 250), Level(9));

        Assert.Equal(5 * RawCell - 1, end.X);
        Assert.Equal(Cm(0, 250).Y, end.Y);
    }

    [Fact]
    public void Inside_the_gated_region_a_low_level_mover_moves_freely_and_leaves_it()
    {
        // From ring 2 into the region behind it (same level: not a gate) and back to the tutorial.
        Assert.Equal(Cm(850, 350), GridCollision.Move(Grid, Cm(650, 350), Cm(850, 350), Level(1)));
        Assert.Equal(Cm(350, 350), GridCollision.Move(Grid, Cm(650, 350), Cm(350, 350), Level(1)));
    }

    [Fact]
    public void The_default_gate_is_the_old_collision()
    {
        Assert.Equal(GridCollision.Move(Grid, Cm(450, 50), Cm(560, 50)), GridCollision.Move(Grid, Cm(450, 50), Cm(560, 50), default));
        Assert.Equal(Cm(560, 50), GridCollision.Move(Grid, Cm(450, 50), Cm(560, 50)));
    }

    [Fact]
    public void A_walk_step_and_a_dash_are_held_to_the_gate()
    {
        var input = new MovementInput(MoveKeys.D, 0);
        var state = new MovementState(Cm(480, 50), 0);
        var walked = MovementSim.Step(state, input, false, 600, 20, Grid, Level(9));
        Assert.Equal(5 * RawCell - 1, walked.Position.X);

        var bodies = new BodyList();
        var dashed = MovementSim.Dash(Grid, Cm(300, 50), Cm(500, 0), bodies, 0, BodyCollisionRules.Off, out _, Level(9));
        Assert.Equal(5 * RawCell - 1, dashed.X);
        var through = MovementSim.Dash(Grid, Cm(300, 50), Cm(500, 0), bodies, 0, BodyCollisionRules.Off, out _, Level(10));
        Assert.Equal(Cm(800, 50), through);
    }

    [Fact]
    public void The_flood_of_a_level_9_walker_stays_in_the_tutorial_and_a_level_10_walker_reaches_everything()
    {
        var low = GridReach.Flood(Grid, Level(9), new[] { Cm(50, 50) });
        var high = GridReach.Flood(Grid, Level(10), new[] { Cm(50, 50) });

        for (int cell = 0; cell < Grid.CellCount; cell++)
        {
            int column = cell % Grid.Width;
            int row = cell / Grid.Width;
            byte region = Grid.RegionIndexOfCell(column, row);
            bool walkable = Grid.IsWalkableCell(column, row);
            Assert.Equal(walkable && region == 1, low[cell]);
            Assert.Equal(walkable, high[cell]);
        }
    }

    [Fact]
    public void Gates_come_from_the_regions_RegionDef_min_level_by_index()
    {
        var gates = RegionGates.From(new[] { "a.free", "a.gated", "a.unknown" },
            new[] { new RegionDef { Id = "a.free" }, new RegionDef { Id = "a.gated", MinLevel = 10 } });

        Assert.True(gates.Any);
        Assert.Equal(0, gates.MinLevelOf(1));
        Assert.Equal(10, gates.MinLevelOf(2));
        Assert.Equal(0, gates.MinLevelOf(3));
        Assert.True(gates.Blocks(1, 2, 9));
        Assert.False(gates.Blocks(1, 2, 10));
        Assert.False(gates.Blocks(2, 1, 1));
        Assert.False(RegionGates.None.Any);
        Assert.False(default(MoverGate).Active);
    }
}
