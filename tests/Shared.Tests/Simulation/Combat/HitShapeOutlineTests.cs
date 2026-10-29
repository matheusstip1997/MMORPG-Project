using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.Simulation.Combat;

namespace Shared.Tests.Simulation.Combat;

/// <summary>
/// The dev hitbox gizmos draw exactly what the server tests (spec 11.7): for every committed <see cref="HitShapeDef"/> and
/// hand-made edge cases, at many facings, a point is inside the drawn outline exactly when <see cref="HitShape.Overlaps"/>
/// (target radius 0) says the shape touches it, except within the drawing tolerance of the outline (arc chords and the
/// fixed-point rounding).
/// </summary>
public class HitShapeOutlineTests
{
    private const int Segments = 64;
    private static readonly FixVec2 Caster = FixVec2.FromCm(1234, -567);
    private static readonly ushort[] Facings = [0, 1_000, 16_384, 23_456, 32_768, 40_000, 49_152, 65_000];

    public static TheoryData<string> CommittedShapes()
    {
        var data = new TheoryData<string>();
        foreach (var row in Committed())
            data.Add(row.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(CommittedShapes))]
    public void A_committed_shape_is_drawn_as_the_server_tests_it(string id) =>
        AssertDrawnAsTested(Committed().Single(r => r.Id == id));

    public static TheoryData<HitShapeDef> EdgeCases() => new()
    {
        new HitShapeDef { Id = "cone.narrow", Kind = HitShapeKind.Cone, RangeCm = 300, HalfAngleDeg = 10 },
        new HitShapeDef { Id = "cone.wide", Kind = HitShapeKind.Cone, RangeCm = 250, HalfAngleDeg = 120, ForwardOffsetCm = -50 },
        new HitShapeDef { Id = "cone.circle", Kind = HitShapeKind.Cone, RangeCm = 200, HalfAngleDeg = 180, SideOffsetCm = 40 },
        new HitShapeDef { Id = "box", Kind = HitShapeKind.Box, LengthCm = 600, WidthCm = 81, ForwardOffsetCm = 30, SideOffsetCm = -70 },
        new HitShapeDef { Id = "capsule", Kind = HitShapeKind.Capsule, LengthCm = 400, RadiusCm = 60, ForwardOffsetCm = 100 },
        new HitShapeDef { Id = "circle", Kind = HitShapeKind.Capsule, LengthCm = 0, RadiusCm = 150, SideOffsetCm = 25 },
    };

    [Theory]
    [MemberData(nameof(EdgeCases))]
    public void An_edge_case_shape_is_drawn_as_the_server_tests_it(HitShapeDef row) => AssertDrawnAsTested(row);

    [Fact]
    public void The_outline_fits_the_points_buffer_of_its_segment_count()
    {
        var points = new FixVec2[HitShapeOutline.MaxPoints(Segments)];
        foreach (var row in EdgeCases().Cast<object[]>().Select(d => (HitShapeDef)d[0]).Concat(Committed()))
            Assert.InRange(HitShapeOutline.Write(row, Caster, 12_345, Segments, points), 4, points.Length);
        Assert.Throws<ArgumentException>(() =>
            HitShapeOutline.Write(Committed().First(), Caster, 0, Segments, new FixVec2[HitShapeOutline.MaxPoints(Segments) - 1]));
    }

    private static void AssertDrawnAsTested(HitShapeDef row)
    {
        var shape = HitShape.From(row);
        var points = new FixVec2[HitShapeOutline.MaxPoints(Segments)];
        int largest = Math.Max(Math.Max(row.RangeCm, row.RadiusCm), Math.Max(row.LengthCm, row.WidthCm));
        // A chord is at most r (1 - cos(pi / n)) inside its arc; plus a few raw units of fixed-point rounding.
        double tolerance = FixedPoint.FromCm(largest) * (1 - Math.Cos(Math.PI / Segments)) + 8;
        var random = new Random(row.Id.GetHashCode(StringComparison.Ordinal));
        foreach (var facing in Facings)
        {
            int count = HitShapeOutline.Write(row, Caster, facing, Segments, points);
            var polygon = points.Take(count).ToArray();
            double minX = polygon.Min(p => p.X) - 2_000, maxX = polygon.Max(p => p.X) + 2_000;
            double minY = polygon.Min(p => p.Y) - 2_000, maxY = polygon.Max(p => p.Y) + 2_000;
            int checkedPoints = 0;
            for (int i = 0; i < 3_000; i++)
            {
                var point = new FixVec2((int)(minX + random.NextDouble() * (maxX - minX)), (int)(minY + random.NextDouble() * (maxY - minY)));
                if (DistanceToOutline(polygon, point) <= tolerance)
                    continue;
                checkedPoints++;
                bool drawn = Inside(polygon, point);
                bool tested = shape.Overlaps(Caster, facing, point, 0);
                Assert.True(drawn == tested,
                    $"{row.Id} facing {facing}: point {point} is {(tested ? "hit" : "missed")} but drawn {(drawn ? "inside" : "outside")}");
            }
            Assert.True(checkedPoints > 1_000, $"{row.Id}: too few points away from the outline");
        }
    }

    private static bool Inside(FixVec2[] polygon, FixVec2 point)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            double xi = polygon[i].X, yi = polygon[i].Y, xj = polygon[j].X, yj = polygon[j].Y;
            if ((yi > point.Y) != (yj > point.Y) && point.X < (xj - xi) * (point.Y - yi) / (yj - yi) + xi)
                inside = !inside;
        }
        return inside;
    }

    private static double DistanceToOutline(FixVec2[] polygon, FixVec2 point)
    {
        double best = double.MaxValue;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            double ax = polygon[j].X, ay = polygon[j].Y, bx = polygon[i].X, by = polygon[i].Y;
            double dx = bx - ax, dy = by - ay;
            double length = dx * dx + dy * dy;
            double t = length == 0 ? 0 : Math.Clamp(((point.X - ax) * dx + (point.Y - ay) * dy) / length, 0, 1);
            double ex = ax + t * dx - point.X, ey = ay + t * dy - point.Y;
            best = Math.Min(best, Math.Sqrt(ex * ex + ey * ey));
        }
        return best;
    }

    private static IReadOnlyList<HitShapeDef> Committed()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "data", "tables", "HitShapeDef.json");
        var errors = new List<ValidationError>();
        var table = ConfigTableReader.ReadTable(File.ReadAllText(path), typeof(HitShapeDef), errors);
        Assert.Empty(errors);
        return table!.Rows.Cast<HitShapeDef>().ToList();
    }
}
