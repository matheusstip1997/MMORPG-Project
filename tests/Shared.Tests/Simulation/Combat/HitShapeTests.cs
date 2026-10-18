using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Combat;

namespace Shared.Tests.Simulation.Combat;

/// <summary>Hit shape geometry against target circles (R-3.1-03, backlog D3): edges, ends, offsets and all facings.</summary>
public class HitShapeTests
{
    // Away from the world origin, so the caster's position is really subtracted.
    private static readonly FixVec2 Caster = FixVec2.FromCm(1000, -500);

    private static int Cm(int cm) => FixedPoint.FromCm(cm);

    /// <summary>A point <paramref name="forwardCm"/> along the facing and <paramref name="sideCm"/> to its right (facing 0: +Y, right +X).</summary>
    private static FixVec2 At(double forwardCm, double sideCm, ushort facing = 0)
    {
        double angle = facing * Math.PI * 2 / 65536;
        double x = forwardCm * Math.Sin(angle) + sideCm * Math.Cos(angle);
        double y = forwardCm * Math.Cos(angle) - sideCm * Math.Sin(angle);
        return Caster + new FixVec2((int)Math.Round(x * FixedPoint.RawPerCm), (int)Math.Round(y * FixedPoint.RawPerCm));
    }

    private static bool Hits(HitShape shape, double forwardCm, double sideCm, int radiusCm = 0, ushort facing = 0) =>
        shape.Overlaps(Caster, facing, At(forwardCm, sideCm, facing), Cm(radiusCm));

    [Theory]
    [InlineData(100, 0, 0, true)]     // inside
    [InlineData(200, 0, 0, true)]     // at max range, touching
    [InlineData(201, 0, 0, false)]    // just past it
    [InlineData(230, 0, 30, true)]    // the target's radius reaches the arc
    [InlineData(230, 0, 29, false)]
    [InlineData(-50, 0, 10, false)]   // behind the attacker
    [InlineData(-50, 0, 50, true)]    // ... unless its circle reaches the apex
    [InlineData(0, 0, 0, true)]       // on the apex
    [InlineData(0, 100, 0, false)]    // 90 degrees off a 60 degree half angle
    [InlineData(0, -100, 49, false)]  // its distance to the 60 degree edge is 100 sin 30 = 50 cm
    [InlineData(0, -100, 51, true)]
    [InlineData(50, 85, 0, true)]     // inside the edge: 60 degrees round to 171/1024 of a turn, tan * 50 = 87 cm
    [InlineData(50, 90, 0, false)]    // 1.5 cm outside it
    [InlineData(50, 90, 2, true)]
    [InlineData(150, -150, 0, false)] // inside the angle (45 degrees) but past the range (212 cm)
    [InlineData(150, -150, 13, true)]
    public void Cone(int forward, int side, int radius, bool hit) =>
        Assert.Equal(hit, Hits(HitShape.Cone(0, 0, 200, 60), forward, side, radius));

    [Theory]
    [InlineData(0, 0, true)]         // on the apex
    [InlineData(-60, 0, false)]      // behind: a half angle under 90 never reaches back
    [InlineData(100, 99, true)]      // 45 degrees: the edge passes (100, 100)
    [InlineData(100, 101, false)]
    public void Narrow_cone_edges(int forward, int side, bool hit) =>
        Assert.Equal(hit, Hits(HitShape.Cone(0, 0, 300, 45), forward, side));

    [Theory]
    [InlineData(90)]
    [InlineData(135)]
    [InlineData(180)]
    public void Wide_cones_reach_beside_and_behind(int halfAngle)
    {
        var cone = HitShape.Cone(0, 0, 200, halfAngle);
        Assert.True(Hits(cone, 0, 150));                       // beside
        Assert.Equal(halfAngle > 135, Hits(cone, -100, 10));   // nearly straight behind: only a full circle
        Assert.False(Hits(cone, 0, 201));
    }

    [Fact]
    public void A_half_cone_hits_everything_in_front()
    {
        var cone = HitShape.Cone(0, 0, 200, 90);
        Assert.True(Hits(cone, 1, -199));
        Assert.False(Hits(cone, -2, -100)); // 2 cm behind the 90 degree edge
        Assert.True(Hits(cone, -2, -100, radiusCm: 2));
    }

    [Theory]
    [InlineData(150, 0, 0, true)]     // inside
    [InlineData(300, 0, 0, true)]     // front edge
    [InlineData(301, 0, 0, false)]
    [InlineData(150, 100, 0, true)]   // side edge (width 200)
    [InlineData(150, -101, 0, false)]
    [InlineData(-10, 0, 10, true)]    // the circle touches the back edge
    [InlineData(-10, 0, 9, false)]    // behind the attacker
    [InlineData(330, 140, 50, true)]  // off the corner: 30 and 40 cm away, 50 cm diagonally
    [InlineData(330, -140, 49, false)]
    public void Box(int forward, int side, int radius, bool hit) =>
        Assert.Equal(hit, Hits(HitShape.Box(0, 0, 300, 200), forward, side, radius));

    [Theory]
    [InlineData(100, 0, 0, true)]
    [InlineData(-50, 0, 0, true)]     // the rounded back end reaches the radius behind the origin
    [InlineData(-51, 0, 0, false)]
    [InlineData(250, 0, 0, true)]     // and past the far end
    [InlineData(251, 0, 0, false)]
    [InlineData(100, 50, 0, true)]    // side
    [InlineData(100, -51, 0, false)]
    [InlineData(230, 40, 0, true)]    // on the far end's arc: 30 and 40 cm from the end, 50 cm
    [InlineData(230, 41, 0, false)]
    [InlineData(-30, -41, 0, false)]  // just off the back end's arc (51 cm from the origin)
    [InlineData(-30, -41, 1, true)]   // ... which the target's radius closes
    public void Capsule(int forward, int side, int radius, bool hit) =>
        Assert.Equal(hit, Hits(HitShape.Capsule(0, 0, 200, 50), forward, side, radius));

    [Fact]
    public void A_capsule_of_length_zero_is_a_circle()
    {
        var circle = HitShape.Capsule(0, 0, 0, 100);
        Assert.True(Hits(circle, 60, -80));
        Assert.False(Hits(circle, -61, 80));
        Assert.True(Hits(circle, -61, 80, radiusCm: 1));
    }

    [Fact]
    public void Offsets_move_the_origin_forward_and_to_the_right()
    {
        // Facing 0 is +Y, so right is +X (FixedTrig.Along).
        var cone = HitShape.Cone(100, 50, 100, 30);
        Assert.True(Hits(cone, 150, 50));
        Assert.False(Hits(cone, 90, 50));   // behind the apex
        Assert.False(Hits(cone, 150, -50)); // mirrored to the left
        Assert.True(cone.Overlaps(Caster, 0, Caster + FixVec2.FromCm(50, 150), 0));

        var circle = HitShape.Capsule(300, 0, 0, 300);
        Assert.True(Hits(circle, 590, 0));
        Assert.False(Hits(circle, -10, 0));

        var box = HitShape.Box(-100, -30, 200, 40); // from 1 m behind, 30 cm to the left
        Assert.True(Hits(box, -50, -45));
        Assert.False(Hits(box, -50, -51));
        Assert.False(Hits(box, -50, 30));
    }

    public static IEnumerable<object[]> Octants() => Enumerable.Range(0, 8).Select(i => new object[] { (ushort)(i * 8192) });

    [Theory]
    [MemberData(nameof(Octants))]
    public void Every_facing_octant_turns_the_shape_with_the_caster(ushort facing)
    {
        // Asymmetric on purpose: 50 cm ahead and 20 cm to the right, so a wrong rotation or mirror shows.
        var box = HitShape.Box(50, 20, 300, 100);
        Assert.True(Hits(box, 200, 20, facing: facing));
        Assert.True(Hits(box, 340, 60, facing: facing));
        Assert.False(Hits(box, 200, 80, facing: facing));
        Assert.False(Hits(box, 200, -40, facing: facing));
        Assert.False(Hits(box, 40, 20, facing: facing));
        Assert.False(Hits(box, 360, 20, facing: facing));

        var cone = HitShape.Cone(0, 0, 200, 45);
        Assert.True(Hits(cone, 150, 0, facing: facing));
        Assert.True(Hits(cone, 100, 90, facing: facing));
        Assert.False(Hits(cone, 100, 110, facing: facing));
        Assert.False(Hits(cone, -150, 0, facing: facing));

        var capsule = HitShape.Capsule(0, 0, 200, 50);
        Assert.True(Hits(capsule, 245, 0, facing: facing));
        Assert.False(Hits(capsule, 255, 0, facing: facing));
        Assert.False(Hits(capsule, -55, 0, facing: facing));
    }

    [Fact]
    public void Facing_between_the_octants_works_too()
    {
        var box = HitShape.Box(0, 0, 300, 100);
        for (int step = 0; step < 1024; step += 37)
        {
            ushort facing = (ushort)(step << 6);
            Assert.True(Hits(box, 295, 45, facing: facing));
            Assert.False(Hits(box, 305, 0, facing: facing));
            Assert.False(Hits(box, 100, 55, facing: facing));
        }
    }

    [Fact]
    public void Far_targets_are_rejected_without_overflow()
    {
        var circle = HitShape.Capsule(0, 0, 0, 2500);
        Assert.False(circle.Overlaps(new FixVec2(int.MinValue, int.MinValue), 8192, new FixVec2(int.MaxValue, int.MaxValue), HitShape.MaxTargetRadiusRaw));
        Assert.False(circle.Overlaps(FixVec2.Zero, 0, FixVec2.FromCm(0, 70_000), 0));
    }

    [Fact]
    public void From_a_row_matches_the_factories()
    {
        var row = new HitShapeDef { Id = "s", EffectId = "e", Kind = HitShapeKind.Box, ForwardOffsetCm = 10, LengthCm = 100, WidthCm = 40 };
        var shape = HitShape.From(row);
        Assert.Equal(HitShapeKind.Box, shape.Kind);
        Assert.True(Hits(shape, 110, 20));
        Assert.False(Hits(shape, 111, 0));
        Assert.False(Hits(shape, 9, 0));

        row = new HitShapeDef { Id = "c", EffectId = "e", Kind = HitShapeKind.Cone, RangeCm = 100, HalfAngleDeg = 180 };
        Assert.True(Hits(HitShape.From(row), -100, 0));
        row = new HitShapeDef { Id = "p", EffectId = "e", Kind = HitShapeKind.Capsule, RadiusCm = 10 };
        Assert.True(Hits(HitShape.From(row), 0, -10));

        Assert.Throws<ArgumentException>(() => HitShape.From(new HitShapeDef { Id = "x" }));
    }

    [Fact]
    public void Bad_sizes_and_radii_throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HitShape.Cone(0, 0, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitShape.Cone(0, 0, 100, 181));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitShape.Box(0, 0, 100, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitShape.Capsule(0, 0, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => HitShape.Capsule(0, 0, 0, 10).Overlaps(Caster, 0, Caster, -1));
    }

    [Fact]
    public void Overlaps_does_not_allocate()
    {
        var shapes = new[] { HitShape.Cone(0, 0, 200, 60), HitShape.Box(0, 0, 300, 200), HitShape.Capsule(0, 0, 200, 50) };
        var target = At(120, 30);
        bool any = false;
        foreach (var shape in shapes)
            any |= shape.Overlaps(Caster, 1234, target, Cm(40)); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            foreach (var shape in shapes)
                any |= shape.Overlaps(Caster, (ushort)(i * 64), target, Cm(40));
        }
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
        Assert.True(any);
    }
}
