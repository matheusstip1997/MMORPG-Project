using Shared.Data.Tables;
using EntityKind = Shared.Protocol.EntityKind;
using Shared.Simulation;
using Shared.Simulation.Combat;
using Shared.Tests.World;
using Shared.World;

namespace Shared.Tests.Simulation;

/// <summary>R-2.2-07: body collision, shared by the server and client prediction.</summary>
public class BodyCollisionTests
{
    private const int PlayerRadiusCm = 40;
    private const int DemonRadiusCm = 50;
    private const int ToleranceCm = 2;
    private const int StepCm = 20; // 400 cm/s at 20 Hz

    private static readonly int PlayerRadius = FixedPoint.FromCm(PlayerRadiusCm);
    private static readonly int DemonRadius = FixedPoint.FromCm(DemonRadiusCm);

    /// <summary>The contact distance between a player and a demon, raw.</summary>
    private static readonly long Contact = FixedPoint.FromCm(PlayerRadiusCm + DemonRadiusCm - ToleranceCm);

    private static readonly BodyCollisionRules Rules = new(BodyResponse.Slide, BodyResponse.Stop, ToleranceCm, 2);

    private static FixVec2 Cm(int x, int y) => FixVec2.FromCm(x, y);

    private static BodyList Bodies(params (uint Id, int XCm, int YCm, int RadiusCm)[] bodies)
    {
        var list = new BodyList(1);
        foreach (var (id, x, y, radius) in bodies)
            list.Add(id, Cm(x, y), FixedPoint.FromCm(radius));
        return list;
    }

    private static long DistanceSquared(FixVec2 a, FixVec2 b)
    {
        long dx = (long)a.X - b.X, dy = (long)a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static FixVec2 Walk(FixVec2 from, FixVec2 to, BodyList bodies, BodyResponse response = BodyResponse.Slide) =>
        BodyCollision.Sweep(from, to, PlayerRadius, bodies, response, Rules, out _);

    [Fact]
    public void Who_blocks_whom()
    {
        Assert.True(BodyCollision.Blocks(EntityKind.Player, EntityKind.Monster));
        Assert.True(BodyCollision.Blocks(EntityKind.Monster, EntityKind.Player));
        Assert.True(BodyCollision.Blocks(EntityKind.Monster, EntityKind.Monster));
        Assert.False(BodyCollision.Blocks(EntityKind.Player, EntityKind.Player)); // R-2.2-07: players pass through each other
        Assert.False(BodyCollision.Blocks(EntityKind.Player, EntityKind.LootDrop));
        Assert.False(BodyCollision.Blocks(EntityKind.Monster, EntityKind.Projectile));
    }

    [Fact]
    public void A_move_away_from_every_body_is_unchanged()
    {
        var bodies = Bodies((1, 0, 0, DemonRadiusCm));
        Assert.Equal(Cm(-320, 0), Walk(Cm(-300, 0), Cm(-320, 0), bodies));
        Assert.Equal(Cm(-280, 0), Walk(Cm(-300, 0), Cm(-280, 0), Bodies()));
    }

    [Fact]
    public void Rules_off_change_nothing()
    {
        var bodies = Bodies((1, 0, 0, DemonRadiusCm));
        var end = BodyCollision.Sweep(Cm(-100, 0), Cm(0, 0), PlayerRadius, bodies, BodyResponse.Stop, BodyCollisionRules.Off,
            out int touched);
        Assert.Equal(Cm(0, 0), end);
        Assert.Equal(-1, touched);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(24576)]
    [InlineData(32768)]
    [InlineData(40960)]
    [InlineData(49152)]
    [InlineData(57344)]
    public void Walking_at_a_body_from_every_octant_stops_at_contact(int facing)
    {
        // From 3 m out, walk straight at the demon's centre for 30 ticks: the walk ends at contact, never inside.
        var demon = Cm(1000, -500);
        var bodies = Bodies((7, 1000, -500, DemonRadiusCm));
        var position = demon + FixedTrig.Along((ushort)facing, FixedPoint.FromCm(300));
        ushort towards = (ushort)(facing + 32768);
        for (int tick = 0; tick < 30; tick++)
        {
            var target = position + FixedTrig.Along(towards, FixedPoint.FromCm(StepCm));
            position = Walk(position, target, bodies);
            Assert.True(DistanceSquared(position, demon) >= Contact * Contact, $"inside the body at tick {tick}");
        }
        long distance = SweptCircle.Sqrt(DistanceSquared(position, demon));
        Assert.InRange(distance, Contact, Contact + FixedPoint.FromCm(1));
    }

    [Fact]
    public void A_walk_past_a_body_slides_round_it()
    {
        // Walking +X along a line 30 cm off the demon's centre: the walk rounds it and reaches the far side.
        var demon = Cm(0, 0);
        var bodies = Bodies((3, 0, 0, DemonRadiusCm));
        var position = Cm(-300, 30);
        bool touched = false;
        for (int tick = 0; tick < 40; tick++)
        {
            var target = position + Cm(StepCm, 0);
            position = BodyCollision.Sweep(position, target, PlayerRadius, bodies, BodyResponse.Slide, Rules, out int hit);
            touched |= hit == 0;
            Assert.True(DistanceSquared(position, demon) >= Contact * Contact, $"inside the body at tick {tick}");
        }
        Assert.True(touched);
        Assert.True(position.X > FixedPoint.FromCm(200), $"stuck at {position}");
    }

    [Fact]
    public void The_same_walk_with_the_stop_response_stays_at_the_body()
    {
        var bodies = Bodies((3, 0, 0, DemonRadiusCm));
        var position = Cm(-300, 30);
        for (int tick = 0; tick < 40; tick++)
            position = Walk(position, position + Cm(StepCm, 0), bodies, BodyResponse.Stop);
        Assert.True(position.X < 0, $"passed the body: {position}");
    }

    [Fact]
    public void A_dash_stops_at_contact()
    {
        // A 4 m dodge straight at the demon in one move ends at contact (the rest of the dodge is lost).
        var bodies = Bodies((3, 0, 0, DemonRadiusCm));
        var end = BodyCollision.Sweep(Cm(-300, 0), Cm(100, 0), PlayerRadius, bodies, Rules.DashResponse, Rules, out int touched);
        Assert.Equal(0, touched);
        Assert.Equal(0, end.Y);
        Assert.InRange(-(long)end.X, Contact, Contact + 2);
    }

    [Fact]
    public void The_nearer_contact_wins_whatever_the_ids()
    {
        var bodies = Bodies((9, 100, 0, DemonRadiusCm), (2, 300, 0, DemonRadiusCm));
        var end = BodyCollision.Sweep(Cm(-100, 0), Cm(500, 0), PlayerRadius, bodies, BodyResponse.Stop, Rules, out int touched);
        Assert.Equal(9u, bodies[touched].Id);
        Assert.True(end.X < FixedPoint.FromCm(100));
    }

    [Fact]
    public void Equal_contacts_go_to_the_lower_id()
    {
        var bodies = Bodies((8, 200, 30, DemonRadiusCm), (4, 200, -30, DemonRadiusCm));
        BodyCollision.Sweep(Cm(0, 0), Cm(400, 0), PlayerRadius, bodies, BodyResponse.Stop, Rules, out int touched);
        Assert.Equal(4u, bodies[touched].Id);
    }

    [Fact]
    public void Bodies_are_kept_in_id_order()
    {
        var bodies = Bodies((5, 0, 0, 1), (1, 0, 0, 1), (9, 0, 0, 1), (3, 0, 0, 1));
        Assert.Equal(new uint[] { 1, 3, 5, 9 }, new[] { bodies[0].Id, bodies[1].Id, bodies[2].Id, bodies[3].Id });
    }

    [Fact]
    public void The_tolerance_sets_the_contact_distance()
    {
        var bodies = Bodies((1, 0, 0, DemonRadiusCm));
        var loose = new BodyCollisionRules(BodyResponse.Slide, BodyResponse.Stop, 10, 2);
        var end = BodyCollision.Sweep(Cm(-300, 0), Cm(0, 0), PlayerRadius, bodies, BodyResponse.Stop, loose, out _);
        Assert.InRange(-(long)end.X, FixedPoint.FromCm(80), FixedPoint.FromCm(80) + 2);
    }

    [Fact]
    public void Overlapping_bodies_may_move_apart_or_sideways_but_never_deeper()
    {
        // A spawn placed the player 30 cm from the demon's centre (contact is 88 cm).
        var demon = Cm(0, 0);
        var bodies = Bodies((1, 0, 0, DemonRadiusCm));
        var start = Cm(-30, 0);

        Assert.Equal(Cm(-50, 0), Walk(start, Cm(-50, 0), bodies)); // outward: free
        Assert.Equal(Cm(-50, 0), Walk(start, Cm(-50, 0), bodies, BodyResponse.Stop));
        Assert.Equal(start, Walk(start, Cm(-10, 0), bodies, BodyResponse.Stop)); // inward, stop: no move

        var slid = Walk(start, Cm(-16, 14), bodies); // inward and sideways, slide: only the sideways part
        Assert.True(slid.Y > 0);
        Assert.True(DistanceSquared(slid, demon) >= DistanceSquared(start, demon));
    }

    [Fact]
    public void A_slide_into_a_second_body_stops_there()
    {
        // Two demons side by side with no room between them: whatever the walk, it never ends inside either.
        var bodies = Bodies((1, 0, 0, DemonRadiusCm), (2, 0, 150, DemonRadiusCm));
        var position = Cm(-300, 80);
        for (int tick = 0; tick < 40; tick++)
        {
            position = Walk(position, position + Cm(StepCm, 0), bodies);
            Assert.True(DistanceSquared(position, bodies[0].Center) >= Contact * Contact, $"inside 1 at tick {tick}");
            Assert.True(DistanceSquared(position, bodies[1].Center) >= Contact * Contact, $"inside 2 at tick {tick}");
        }
    }

    [Fact]
    public void Wandering_through_a_crowd_never_enters_a_body_nor_moves_further_than_asked()
    {
        var bodies = Bodies((1, 0, 0, DemonRadiusCm), (2, 170, 40, 45), (3, -60, 190, 120), (4, 260, 260, DemonRadiusCm));
        var position = Cm(-400, -300);
        uint seed = 12345;
        for (int tick = 0; tick < 3000; tick++)
        {
            seed = seed * 1664525 + 1013904223; // fixed sequence: the test is deterministic
            ushort heading = (ushort)(seed >> 16);
            int stepCm = tick % 7 == 0 ? 80 : StepCm; // now and then a dash-sized step
            var target = position + FixedTrig.Along(heading, FixedPoint.FromCm(stepCm));
            // Keep it near the crowd.
            if (target.X < Cm(-500, 0).X || target.X > Cm(600, 0).X || target.Y < Cm(0, -500).Y || target.Y > Cm(0, 600).Y)
                target = position + FixedTrig.Along((ushort)(heading + 32768), FixedPoint.FromCm(stepCm));
            var response = tick % 2 == 0 ? BodyResponse.Slide : BodyResponse.Stop;
            var end = BodyCollision.Sweep(position, target, PlayerRadius, bodies, response, Rules, out _);

            Assert.True(DistanceSquared(position, end) <= DistanceSquared(position, target) + 4, $"longer than asked at tick {tick}");
            for (int i = 0; i < bodies.Count; i++)
            {
                long contact = PlayerRadius + bodies[i].RadiusRaw - FixedPoint.FromCm(ToleranceCm);
                Assert.True(DistanceSquared(end, bodies[i].Center) >= contact * contact, $"inside {bodies[i].Id} at tick {tick}");
            }
            Assert.Equal(end, BodyCollision.Sweep(position, target, PlayerRadius, bodies, response, Rules, out _));
            position = end;
        }
    }

    [Fact]
    public void Is_deeper_only_when_the_end_is_inside_and_closer_than_the_start()
    {
        var bodies = Bodies((1, 0, 0, DemonRadiusCm));
        Assert.True(BodyCollision.IsDeeper(Cm(-100, 0), Cm(-50, 0), PlayerRadius, bodies, Rules));
        Assert.False(BodyCollision.IsDeeper(Cm(-50, 0), Cm(-60, 0), PlayerRadius, bodies, Rules));
        Assert.False(BodyCollision.IsDeeper(Cm(-200, 0), Cm(-100, 0), PlayerRadius, bodies, Rules));
    }

    [Fact]
    public void A_wall_next_to_a_body_cannot_push_the_end_into_it()
    {
        // A wall column at x 500..600 cm; the demon stands right of the mover against it. A diagonal walk towards the
        // demon's top-left slides along the body, then the wall clip keeps only one axis: never deeper.
        string[] layout =
        {
            ".....#....", ".....#....", ".....#....", ".....#....", ".....#....",
            ".....#....", ".....#....", ".....#....", ".....#....", ".....#....",
        };
        var grid = GridCollisionTests.Build(layout);
        var bodies = Bodies((1, 450, 500, DemonRadiusCm));
        var position = Cm(300, 450);
        for (int tick = 0; tick < 40; tick++)
        {
            var target = position + Cm(14, 14);
            position = MovementSim.Move(grid, position, target, bodies, PlayerRadius, BodyResponse.Slide, Rules, out _);
            Assert.True(DistanceSquared(position, bodies[0].Center) >= Contact * Contact, $"inside at tick {tick}");
            Assert.True(position.X < FixedPoint.FromCm(500), "inside the wall");
        }
    }

    [Fact]
    public void Without_bodies_the_body_aware_step_is_the_grid_step()
    {
        var grid = GridCollisionTests.Build(new[] { "..........", "..........", "..........", ".....#....", "..........",
            "..........", "..........", "..........", "..........", ".........." });
        var state = new MovementState(Cm(450, 350), 0);
        var input = new MovementInput(MoveKeys.D | MoveKeys.W, 0);
        for (int tick = 0; tick < 20; tick++)
        {
            var plain = MovementSim.Step(state, input, false, 400, 20, grid);
            var bodies = MovementSim.Step(state, input, false, 400, 20, grid, new BodyList(), PlayerRadius, Rules);
            Assert.Equal(plain, bodies);
            state = plain;
        }
    }

    [Fact]
    public void The_largest_bodies_and_steps_do_not_overflow()
    {
        int radius = SweptCircle.MaxRaw / 2;
        var list = new BodyList();
        list.Add(1, new FixVec2(SweptCircle.MaxRaw, 0), radius);
        var end = BodyCollision.Sweep(new FixVec2(-SweptCircle.MaxRaw, 0), new FixVec2(0, SweptCircle.MaxRaw), radius, list,
            BodyResponse.Slide, new BodyCollisionRules(BodyResponse.Slide, BodyResponse.Slide, 0, 4), out _);
        Assert.True(DistanceSquared(end, list[0].Center) >= (long)SweptCircle.MaxRaw * SweptCircle.MaxRaw);
    }
}
