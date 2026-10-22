using Shared.Simulation;
using Shared.Simulation.Combat;
using Shared.Tests.World;

namespace Shared.Tests.Simulation.Combat;

/// <summary>Spec 3.2: the straight projectile path the server simulates and the client draws.</summary>
public class ProjectilePathTests
{
    private const int TickRate = 20;
    private static readonly FixVec2 Origin = FixVec2.FromCm(1000, -500);

    private static ProjectilePath Path(ushort facing = 0, int speed = 2400, int range = 2000, uint start = 100) =>
        ProjectilePath.Create(Origin, facing, speed, range, start, TickRate, grid: null);

    [Fact]
    public void Each_step_is_speed_over_tick_rate_along_the_facing()
    {
        var path = Path(); // 120 cm per tick, facing +Y
        Assert.Equal(Origin, path.PositionAt(0));
        Assert.Equal(Origin + FixVec2.FromCm(0, 120), path.PositionAt(1));
        Assert.Equal(Origin + FixVec2.FromCm(0, 600), path.PositionAt(5));
        Assert.Equal(FixedPoint.FromCm(240), path.DistanceRawAt(2));
    }

    [Fact]
    public void Slow_projectiles_keep_the_fraction_of_a_cm()
    {
        var path = Path(speed: 30); // 1.5 cm per tick
        Assert.Equal(FixedPoint.FromCm(1) + FixedPoint.RawPerCm / 2, path.DistanceRawAt(1));
        Assert.Equal(FixedPoint.FromCm(3), path.DistanceRawAt(2));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(16384, 1, 0)]
    [InlineData(32768, 0, -1)]
    [InlineData(49152, -1, 0)]
    public void It_flies_along_the_four_axes(int facing, int x, int y)
    {
        var path = Path((ushort)facing);
        Assert.Equal(Origin + FixVec2.FromCm(x * 120, y * 120), path.PositionAt(1));
    }

    [Theory]
    [InlineData(8192)]
    [InlineData(24576)]
    [InlineData(40960)]
    [InlineData(57344)]
    public void Diagonals_match_the_facing_table(int facing)
    {
        var path = Path((ushort)facing);
        Assert.Equal(Origin + FixedTrig.Along((ushort)facing, FixedPoint.FromCm(360)), path.PositionAt(3));
    }

    [Fact]
    public void It_stops_at_its_range()
    {
        var path = Path(); // 2000 cm at 120 per tick: 16.67 steps
        Assert.False(path.StoppedByWorld);
        Assert.Equal(FixedPoint.FromCm(2000), path.LimitRaw);
        Assert.Equal(17, path.FlightSteps);
        Assert.True(path.DistanceRawAt(16) < path.LimitRaw);
        Assert.Equal(path.LimitRaw, path.DistanceRawAt(17));
        Assert.Equal(path.End, path.PositionAt(1000));
        Assert.Equal(116u, path.LastTick); // spawned in tick 100, its 17th step is phase 4 of tick 116
    }

    [Fact]
    public void Steps_count_from_the_spawn_tick()
    {
        var path = Path(start: 100);
        Assert.Equal(0, path.StepsAfter(99));
        Assert.Equal(1, path.StepsAfter(100)); // spawned in phase 3, first step in phase 4 of the same tick
        Assert.Equal(11, path.StepsAfter(110));
    }

    [Fact]
    public void A_wall_cuts_the_path_where_the_ray_meets_it()
    {
        // 10 x 10 cells of 100 cm at the origin; a wall row at y = 5 (500..600 cm).
        var grid = GridCollisionTests.Build(new[]
        {
            "..........", "..........", "..........", "..........", "..........",
            "##########", "..........", "..........", "..........", "..........",
        });
        var origin = FixVec2.FromCm(250, 150);
        var path = ProjectilePath.Create(origin, 0, 2400, 2000, 0, TickRate, grid);
        Assert.True(path.StoppedByWorld);
        // The last raw point before y = 500 cm: 350 cm minus 1 raw from the origin.
        Assert.Equal(FixedPoint.FromCm(350) - 1, path.LimitRaw);
        Assert.Equal(3, path.FlightSteps); // 120, 240, 360 -> clamped to the wall
        Assert.Equal(new FixVec2(origin.X, FixedPoint.FromCm(500) - 1), path.End);

        var open = ProjectilePath.Create(origin, 32768, 2400, 2000, 0, TickRate, grid); // away from the wall, out of the grid
        Assert.True(open.StoppedByWorld);
        Assert.Equal(FixedPoint.FromCm(150), open.LimitRaw); // the grid edge at y = 0
    }

    [Fact]
    public void The_same_spawn_gives_the_same_path()
    {
        for (int facing = 0; facing < 65536; facing += 997)
        {
            var a = Path((ushort)facing, speed: 3333, range: 1777);
            var b = Path((ushort)facing, speed: 3333, range: 1777);
            for (int step = 0; step <= a.FlightSteps; step++)
                Assert.Equal(a.PositionAt(step), b.PositionAt(step));
        }
    }

    [Fact]
    public void Bad_arguments_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Path(speed: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Path(range: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectilePath.Create(Origin, 0, 100, 100, 0, 0, null));
    }

    [Fact]
    public void Stepping_does_not_allocate()
    {
        var path = Path((ushort)12345);
        long sum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int step = 0; step < 1000; step++)
            sum += path.PositionAt(step).X;
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotEqual(0, sum);
    }
}
