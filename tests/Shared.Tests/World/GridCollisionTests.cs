using Shared.Simulation;
using Shared.World;

namespace Shared.Tests.World;

/// <summary>Backlog C3: collision against the walkability grid, shared by the server and client prediction.</summary>
public class GridCollisionTests
{
    private const int Cell = 100; // cm
    private const int RawCell = Cell * FixedPoint.RawPerCm;

    // 10 x 10 cells at origin (0, 0); '#' is blocked. Row 0 is the bottom line (lowest Y).
    private static readonly string[] Layout =
    {
        "..........", // y = 0
        "..........",
        "..........",
        ".....#....",
        ".....#....",
        ".....#....", // y = 5
        "..........",
        "...##.....",
        "..........",
        "..........", // y = 9
    };

    private static readonly WorldGrid Grid = Build(Layout);

    internal static WorldGrid Build(string[] rows, int originXCm = 0, int originYCm = 0, int cellCm = Cell)
    {
        int width = rows[0].Length;
        int height = rows.Length;
        var flags = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                flags[y * width + x] = rows[y][x] == '#' ? (byte)0 : WorldGridFormat.FlagWalkable;
        return new WorldGrid(originXCm, originYCm, cellCm, width, height, 0, flags, new byte[width * height],
            new short[width * height]);
    }

    private static FixVec2 Cm(int x, int y) => FixVec2.FromCm(x, y);

    [Fact]
    public void A_free_move_is_unchanged()
    {
        Assert.Equal(Cm(150, 120), GridCollision.Move(Grid, Cm(110, 110), Cm(150, 120)));
    }

    [Fact]
    public void Walking_into_a_wall_stops_just_before_it()
    {
        // Wall column x = 5 covers [500, 600) cm on rows 3..5.
        var end = GridCollision.Move(Grid, Cm(480, 450), Cm(520, 450));

        Assert.Equal(5 * RawCell - 1, end.X);
        Assert.Equal(Cm(0, 450).Y, end.Y);
        Assert.False(Grid.IsWalkable(new FixVec2(end.X + 1, end.Y)));
        Assert.True(Grid.IsWalkable(end));
    }

    [Fact]
    public void Walking_into_a_wall_from_the_other_side_stops_on_its_far_edge()
    {
        var end = GridCollision.Move(Grid, Cm(620, 450), Cm(580, 450));

        Assert.Equal(6 * RawCell, end.X);
    }

    [Fact]
    public void A_wall_cannot_be_walked_through_even_by_a_long_step()
    {
        var end = GridCollision.Move(Grid, Cm(150, 400), Cm(950, 400));

        Assert.Equal(5 * RawCell - 1, end.X);
    }

    [Fact]
    public void Pushing_diagonally_into_a_wall_slides_along_it()
    {
        var start = Cm(490, 400);
        var end = GridCollision.Move(Grid, start, Cm(510, 420));

        Assert.Equal(5 * RawCell - 1, end.X); // X stopped by the wall
        Assert.Equal(Cm(0, 420).Y, end.Y);     // Y kept: sliding
    }

    [Fact]
    public void Sliding_along_a_floor_keeps_the_free_axis()
    {
        // Blocks at (3, 7) and (4, 7): walking up-right from below them.
        var end = GridCollision.Move(Grid, Cm(350, 690), Cm(370, 710));

        Assert.Equal(Cm(370, 0).X, end.X);
        Assert.Equal(7 * RawCell - 1, end.Y);
    }

    [Fact]
    public void Corners_cannot_be_cut_between_diagonal_blocks()
    {
        var grid = Build(new[]
        {
            "...",
            ".#.",
            "#..",
        });
        // Row 0 is the first string: blocks at (1, 1) and (0, 2). From (0, 1) up-right towards the free (1, 2), the
        // gap is only the shared corner: X is stopped by (1, 1), then Y in column 0 by (0, 2).
        var end = GridCollision.Move(grid, Cm(90, 190), Cm(110, 210));

        Assert.Equal((0, 1), (grid.ColumnOf(end.X), grid.RowOf(end.Y)));
        Assert.Equal(1 * RawCell - 1, end.X);
        Assert.Equal(2 * RawCell - 1, end.Y);
    }

    [Theory]
    [InlineData(950, 500, 1_050, 500)]   // +X edge
    [InlineData(50, 500, -50, 500)]      // -X edge
    [InlineData(500, 950, 500, 1_050)]   // +Y edge
    [InlineData(500, 50, 500, -50)]      // -Y edge
    public void The_grid_edge_blocks(int fromX, int fromY, int toX, int toY)
    {
        var end = GridCollision.Move(Grid, Cm(fromX, fromY), Cm(toX, toY));

        Assert.True(Grid.IsWalkable(end));
        Assert.True(Grid.InBounds(Grid.ColumnOf(end.X), Grid.RowOf(end.Y)));
    }

    [Fact]
    public void An_entity_inside_a_blocked_cell_can_walk_out()
    {
        var end = GridCollision.Move(Grid, Cm(550, 450), Cm(650, 450));

        Assert.Equal(Cm(650, 450), end);
    }

    [Fact]
    public void An_entity_outside_the_grid_does_not_move()
    {
        Assert.Equal(Cm(-500, 50), GridCollision.Move(Grid, Cm(-500, 50), Cm(-400, 50)));
    }

    [Fact]
    public void Negative_origins_floor_correctly()
    {
        var grid = Build(new[] { "..#." }, originXCm: -200, originYCm: -100);
        // Cell 2 covers [0, 100) cm; a mover at -10 cm walking right stops at -1 raw.
        var end = GridCollision.Move(grid, Cm(-10, -50), Cm(20, -50));

        Assert.Equal(-1, end.X);
    }

    [Fact]
    public void MovementSim_with_the_grid_matches_the_open_step_when_nothing_blocks()
    {
        var state = new MovementState(Cm(150, 150), 0);
        var input = new MovementInput(MoveKeys.W | MoveKeys.D, 1234);

        var open = MovementSim.Step(state, input, actionActive: false, 400, 20);
        var collided = MovementSim.Step(state, input, actionActive: false, 400, 20, Grid);

        Assert.Equal(open, collided);
    }

    [Fact]
    public void Walking_east_into_the_wall_for_many_ticks_never_enters_it_and_is_deterministic()
    {
        var input = new MovementInput(MoveKeys.D | MoveKeys.W, 0);
        var a = new MovementState(Cm(100, 330), 0);
        var b = a;
        for (int tick = 0; tick < 200; tick++)
        {
            a = MovementSim.Step(a, input, actionActive: false, 400, 20, Grid);
            b = MovementSim.Step(b, input, actionActive: false, 400, 20, Grid);
            Assert.True(Grid.IsWalkable(a.Position), $"tick {tick} at {a.Position}");
        }
        Assert.Equal(a, b);
    }
}
