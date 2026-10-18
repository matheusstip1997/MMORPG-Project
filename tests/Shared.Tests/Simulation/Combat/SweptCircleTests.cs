using Shared.Simulation;
using Shared.Simulation.Combat;

namespace Shared.Tests.Simulation.Combat;

/// <summary>The projectile sweep of spec 3.2: the segment moved this tick against a target circle, so nothing tunnels.</summary>
public class SweptCircleTests
{
    private static readonly FixVec2 Start = FixVec2.FromCm(-2000, 700);

    private static int Cm(int cm) => FixedPoint.FromCm(cm);

    private static FixVec2 P(int xCm, int yCm) => Start + FixVec2.FromCm(xCm, yCm);

    private static bool Hits(FixVec2 end, FixVec2 center, int radiusCm, out int entryCm)
    {
        bool hit = SweptCircle.Hits(Start, end, center, Cm(radiusCm), out int entryRaw);
        entryCm = FixedPoint.ToCmRound(entryRaw);
        return hit;
    }

    [Fact]
    public void A_fast_segment_hits_a_circle_it_passes_through()
    {
        // 10 m in one tick past a 30 cm target: neither end is near it, the sweep still hits.
        var end = P(0, 1000);
        var target = P(0, 500);
        Assert.False((Start - target).LengthSquared <= (long)Cm(30) * Cm(30));
        Assert.False((end - target).LengthSquared <= (long)Cm(30) * Cm(30));

        Assert.True(SweptCircle.Hits(Start, end, target, Cm(30), out int entryRaw));
        Assert.Equal(Cm(470), entryRaw); // exact on an axis
    }

    [Theory]
    [InlineData(30, true, 500)]   // grazing: touching counts
    [InlineData(31, false, 0)]    // near miss
    [InlineData(-30, true, 500)]
    [InlineData(-31, false, 0)]
    public void Near_misses_and_grazes(int sideCm, bool hit, int entryCm)
    {
        Assert.Equal(hit, Hits(P(0, 1000), P(sideCm, 500), 30, out int entry));
        Assert.Equal(entryCm, entry);
    }

    [Fact]
    public void Starting_inside_hits_at_distance_zero()
    {
        Assert.True(Hits(P(0, 1000), P(10, -10), 30, out int entry));
        Assert.Equal(0, entry);
        Assert.True(Hits(P(0, 1000), Start, 0, out entry)); // on the center with no radius
    }

    [Fact]
    public void The_ends_of_the_segment_bound_the_sweep()
    {
        Assert.True(Hits(P(0, 1000), P(0, 1010), 20, out int entry)); // ends inside
        Assert.Equal(990, entry);
        Assert.True(Hits(P(0, 1000), P(0, 1030), 30, out entry));     // touches at the very end
        Assert.Equal(1000, entry);
        Assert.False(Hits(P(0, 1000), P(0, 1031), 30, out _));        // just beyond the end
        Assert.False(Hits(P(0, 1000), P(0, -31), 30, out _));         // just behind the start
        Assert.False(Hits(P(0, 1000), P(25, 1025), 30, out _));       // off the end's corner: 35 cm
    }

    [Fact]
    public void A_segment_of_length_zero_is_a_point()
    {
        Assert.True(Hits(Start, P(18, 24), 30, out int entry));
        Assert.Equal(0, entry);
        Assert.False(Hits(Start, P(18, 25), 30, out _));
    }

    [Fact]
    public void Diagonal_segments_and_the_entry_distance()
    {
        // 3-4-5: 500 cm, a target centered on the segment at 250 cm with 10 cm radius is entered at 240 cm.
        var end = P(300, 400);
        Assert.True(Hits(end, P(150, 200), 10, out int entry));
        Assert.Equal(240, entry);
        // 20 cm off the line (perpendicular (-0.8, 0.6)): entered sqrt(25^2 - 20^2) = 15 cm before the nearest point.
        Assert.True(Hits(end, P(150 - 16, 200 + 12), 25, out entry));
        Assert.Equal(235, entry);
        Assert.False(Hits(end, P(150 - 16, 200 + 12), 19, out _));
    }

    [Fact]
    public void Entry_distance_orders_targets_along_the_segment()
    {
        var end = P(-600, -800); // 10 m the other way
        Assert.True(Hits(end, P(-300, -400), 50, out int near));
        Assert.True(Hits(end, P(-450, -600), 50, out int far));
        Assert.True(near < far);
        Assert.Equal(450, near);
        Assert.Equal(700, far);
    }

    [Fact]
    public void Steps_and_radii_past_the_limit_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SweptCircle.Hits(Start, Start + new FixVec2(SweptCircle.MaxRaw + 1, 0), Start, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweptCircle.Hits(Start, Start, Start, SweptCircle.MaxRaw + 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SweptCircle.Hits(Start, Start, Start, -1, out _));
        // At the limit, with the target far away on the other side: a plain miss.
        Assert.False(SweptCircle.Hits(Start, Start + new FixVec2(SweptCircle.MaxRaw, -SweptCircle.MaxRaw),
            new FixVec2(int.MaxValue, int.MinValue), SweptCircle.MaxRaw, out _));
        Assert.True(SweptCircle.Hits(Start, Start + new FixVec2(SweptCircle.MaxRaw, SweptCircle.MaxRaw),
            Start + new FixVec2(SweptCircle.MaxRaw, 0), SweptCircle.MaxRaw / 4 * 3, out _)); // the largest products
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(3L, 1L)]
    [InlineData(4L, 2L)]
    [InlineData(99L, 9L)]
    [InlineData(100L, 10L)]
    [InlineData(4_294_967_295L, 65_535L)]
    [InlineData(4_294_967_296L, 65_536L)]
    [InlineData(long.MaxValue, 3_037_000_499L)]
    public void Integer_square_root_floors(long value, long root) => Assert.Equal(root, SweptCircle.Sqrt(value));

    [Fact]
    public void Hits_does_not_allocate()
    {
        var end = P(300, 400);
        var target = P(150, 200);
        bool any = SweptCircle.Hits(Start, end, target, Cm(10), out _); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            any &= SweptCircle.Hits(Start, end, target, Cm(10 + i % 7), out _);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(any);
    }
}
