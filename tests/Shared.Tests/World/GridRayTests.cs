using Shared.Simulation;
using Shared.World;

namespace Shared.Tests.World;

/// <summary>Spec 3.2: where a projectile's straight segment first meets a non-walkable cell (server and client alike).</summary>
public class GridRayTests
{
    // 10 x 10 cells of 100 cm at origin (0, 0); '#' is blocked. Row 0 is the bottom line (lowest Y).
    private static readonly WorldGrid Grid = GridCollisionTests.Build(new[]
    {
        "..........", // y = 0
        "..........",
        "..........",
        ".....#....",
        "......#...",
        "..........", // y = 5
        "..........",
        "...##.....",
        "..........",
        "..........", // y = 9
    });

    private static FixVec2 Cm(int x, int y) => FixVec2.FromCm(x, y);

    [Fact]
    public void An_open_segment_reaches_its_end()
    {
        Assert.False(GridRay.Cast(Grid, Cm(50, 50), Cm(950, 50), out var stop));
        Assert.Equal(Cm(950, 50), stop);
        Assert.False(GridRay.Cast(Grid, Cm(150, 150), Cm(150, 150), out stop)); // zero length
        Assert.Equal(Cm(150, 150), stop);
    }

    [Fact]
    public void Along_x_it_stops_in_front_of_the_wall()
    {
        Assert.True(GridRay.Cast(Grid, Cm(50, 350), Cm(950, 350), out var stop));
        Assert.Equal(new FixVec2(FixedPoint.FromCm(500) - 1, FixedPoint.FromCm(350)), stop);

        Assert.True(GridRay.Cast(Grid, Cm(950, 350), Cm(50, 350), out stop)); // from the other side
        Assert.Equal(Cm(600, 350), stop);
    }

    [Fact]
    public void Along_y_it_stops_in_front_of_the_wall()
    {
        Assert.True(GridRay.Cast(Grid, Cm(450, 950), Cm(450, 50), out var stop));
        Assert.Equal(Cm(450, 800), stop); // the wall row 7 spans x 300..500

        Assert.True(GridRay.Cast(Grid, Cm(350, 50), Cm(350, 950), out stop));
        Assert.Equal(new FixVec2(FixedPoint.FromCm(350), FixedPoint.FromCm(700) - 1), stop);
    }

    [Fact]
    public void A_diagonal_stops_on_the_segment()
    {
        // From (50, 50) towards (950, 650): slope 2/3; it enters x = 500 at y = 350, inside the blocked cell (5, 3).
        Assert.True(GridRay.Cast(Grid, Cm(50, 50), Cm(950, 650), out var stop));
        Assert.Equal(FixedPoint.FromCm(500) - 1, stop.X);
        long dy = (long)(stop.Y - FixedPoint.FromCm(50)) * 900 * 256 - (long)(stop.X - FixedPoint.FromCm(50)) * 600 * 256;
        Assert.True(Math.Abs(dy) <= 900L * 256, "the stop is on the segment"); // within a raw unit
        Assert.Equal(3, Grid.RowOf(stop.Y));
    }

    [Fact]
    public void It_does_not_slip_between_cells_that_touch_at_a_corner()
    {
        // Cells (5, 3) and (6, 4) are blocked and touch at (600, 400). A ray exactly through that corner stops.
        Assert.True(GridRay.Cast(Grid, Cm(450, 250), Cm(750, 550), out var stop));
        Assert.True(Grid.IsWalkable(stop));
        Assert.True(stop.X < FixedPoint.FromCm(600) && stop.Y < FixedPoint.FromCm(400));
    }

    [Fact]
    public void Outside_the_grid_is_blocked()
    {
        Assert.True(GridRay.Cast(Grid, Cm(950, 50), Cm(1500, 50), out var stop));
        Assert.Equal(new FixVec2(FixedPoint.FromCm(1000) - 1, FixedPoint.FromCm(50)), stop);

        Assert.True(GridRay.Cast(Grid, Cm(-50, 50), Cm(500, 50), out stop)); // starts outside
        Assert.Equal(Cm(-50, 50), stop);
    }

    [Fact]
    public void A_ray_from_inside_a_blocked_cell_still_flies_out()
    {
        Assert.False(GridRay.Cast(Grid, Cm(550, 350), Cm(550, 150), out var stop));
        Assert.Equal(Cm(550, 150), stop);
    }

    [Fact]
    public void Every_stop_is_walkable_and_on_the_way()
    {
        var random = new Random(77);
        for (int i = 0; i < 5000; i++)
        {
            var from = FixVec2.FromCm(random.Next(0, 1000), random.Next(0, 1000));
            var to = FixVec2.FromCm(random.Next(-200, 1200), random.Next(-200, 1200));
            if (!Grid.IsWalkable(from))
                continue;
            bool blocked = GridRay.Cast(Grid, from, to, out var stop);
            Assert.True(Grid.IsWalkable(stop) || !blocked && stop == to, $"{from} -> {to} stopped at {stop}");
            Assert.True(Math.Min(from.X, to.X) <= stop.X && stop.X <= Math.Max(from.X, to.X));
            Assert.True(Math.Min(from.Y, to.Y) <= stop.Y && stop.Y <= Math.Max(from.Y, to.Y));
        }
    }

    [Fact]
    public void Casting_does_not_allocate()
    {
        GridRay.Cast(Grid, Cm(50, 50), Cm(950, 650), out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            GridRay.Cast(Grid, Cm(50, 50), Cm(950, 650), out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
