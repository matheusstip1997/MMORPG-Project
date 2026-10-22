using GameServer.Combat;
using GameServer.Entities;
using GameServer.Tests.Support;
using GameServer.World;
using GameServer.World.Map;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Combat;

namespace GameServer.Tests.Combat;

/// <summary>
/// Tick phase 4 (spec 3.2, backlog D3) on its own: straight flight, swept hits in order through a recording
/// <see cref="IHitTargetResolver"/>, maxHits and falloff (R-3.2-01), no double hits, walls, range, owner gone, the caps
/// and the pool.
/// </summary>
public class ProjectileSystemTests
{
    private const int TargetRadiusCm = 40;
    private const ushort North = 0; // +Y

    private static ProjectileDef Arrow(int speed = 2400, int range = 2000, int maxHits = 1, int falloff = 0, bool stops = true) => new()
    {
        Id = "arrow", NetId = 1, SpeedCmPerSec = speed, RangeCm = range, HitRadiusCm = 15, MaxHits = maxHits,
        StopsOnWorld = stops, FalloffPercentPerHit = falloff, VisualId = "arrow",
    };

    private static readonly ActionEffectDef Shot = new()
    {
        Id = "skill.shot.projectile", ActionId = "skill.shot", Kind = EffectKind.Projectile, Phase = ActionPhase.Active,
        PoiseDamage = 10, FlinchPower = 10, FlinchMs = 300, ProjectileId = "arrow",
    };

    private sealed class Fixture
    {
        private static readonly CombatRules Combat = TestCombat.Rules(TestCombat.Point());
        private uint _nextEntityId = 1;

        public Fixture(ProjectileDef? def = null, ProjectileSettings? settings = null, WorldMap? map = null)
        {
            Rules = new ProjectileRules([def ?? Arrow()],
                settings ?? new ProjectileSettings { MaxPerOwner = 8, MaxActive = 16, EventRepeatTicks = 3 }, 20);
            System = new ProjectileSystem(Rules, map ?? TestMaps.Open(), Grid, null, TargetRadiusCm) { Resolver = Resolver };
        }

        public ProjectileRules Rules { get; }

        public SpatialGrid Grid { get; } = new(1_500);

        public RecordingResolver Resolver { get; } = new();

        public ProjectileSystem System { get; }

        public Monster Add(int xCm, int yCm)
        {
            Assert.True(Combat.TryGetMonster(TestCombat.MonsterId, out var def));
            var monster = new Monster(_nextEntityId++, def, "test", new MovementState(FixVec2.FromCm(xCm, yCm), 0), 0,
                Combat.ForMonster(def, 0));
            Grid.Add(monster);
            return monster;
        }

        public Projectile Fire(Entity owner, uint tick, ushort facing = North) =>
            System.Spawn(owner, Shot, facing, tick) ?? throw new InvalidOperationException("refused");

        /// <summary>Phase 4 of every tick from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public void Run(uint from, uint to)
        {
            for (uint tick = from; tick <= to; tick++)
                System.Step(tick);
        }
    }

    internal sealed class RecordingResolver : IHitTargetResolver
    {
        public List<HitRequest> Requests { get; } = [];

        public Func<Entity, bool> CanHitTarget { get; set; } = _ => true;

        public Func<HitRequest, HitOutcome> Outcome { get; set; } = _ => HitOutcome.Damaged;

        public bool CanHit(Entity attacker, Entity target) => CanHitTarget(target);

        public HitOutcome Resolve(in HitRequest request)
        {
            Requests.Add(request);
            return Outcome(request);
        }
    }

    [Fact]
    public void It_flies_straight_along_the_locked_facing_from_the_spawn_tick()
    {
        var fixture = new Fixture();
        var owner = fixture.Add(0, 0);
        var arrow = fixture.Fire(owner, tick: 10, facing: 16384); // +X

        Assert.Equal(FixVec2.FromCm(0, 0), arrow.Path.Origin);
        fixture.System.Step(10); // the spawn tick's phase 4 takes the first step
        Assert.Equal(FixVec2.FromCm(120, 0), arrow.Position);
        fixture.System.Step(11);
        Assert.Equal(FixVec2.FromCm(120, 0), arrow.SegmentStart);
        Assert.Equal(FixVec2.FromCm(240, 0), arrow.Position);
        Assert.True(arrow.IsLive);
    }

    [Fact]
    public void A_fast_projectile_does_not_step_over_a_target()
    {
        var fixture = new Fixture(Arrow(speed: 60_000, range: 5_000)); // 30 m per tick
        var owner = fixture.Add(0, 0);
        var target = fixture.Add(0, 1_500); // neither end of the first segment (0 -> 3000) is near it
        var arrow = fixture.Fire(owner, 1);
        fixture.System.Step(1);

        var hit = Assert.Single(fixture.Resolver.Requests);
        Assert.Equal((owner, target, HitSource.Projectile, 1u, 100), (hit.Attacker, hit.Target, hit.Source, hit.Tick, hit.DamagePercent));
        Assert.Same(Shot, hit.Effect);
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
        Assert.Equal(1u, arrow.EndTick);
    }

    [Fact]
    public void Max_hits_one_stops_at_the_first_target_on_the_segment()
    {
        var fixture = new Fixture(Arrow(speed: 24_000)); // 12 m per tick
        var owner = fixture.Add(0, 0);
        fixture.Add(0, 900);
        var near = fixture.Add(0, 500);
        var arrow = fixture.Fire(owner, 1);
        fixture.System.Step(1);

        Assert.Same(near, Assert.Single(fixture.Resolver.Requests).Target);
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
    }

    [Fact]
    public void A_piercing_projectile_hits_in_entry_order_with_falloff_and_ties_go_by_entity_id()
    {
        var fixture = new Fixture(Arrow(speed: 24_000, maxHits: 3, falloff: 20));
        var owner = fixture.Add(0, 0);
        var third = fixture.Add(0, 1_000);
        var tieRight = fixture.Add(10, 500); // same entry distance as tieLeft, lower entity id
        var tieLeft = fixture.Add(-10, 500);
        var arrow = fixture.Fire(owner, 1);
        fixture.System.Step(1);

        Assert.Equal(new[] { tieRight, tieLeft, third }, fixture.Resolver.Requests.Select(r => r.Target));
        Assert.Equal(new[] { 100, 80, 60 }, fixture.Resolver.Requests.Select(r => r.DamagePercent));
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
        Assert.Equal(3, arrow.Hits);
    }

    [Fact]
    public void A_target_is_hit_once_and_the_owner_never()
    {
        var fixture = new Fixture(Arrow(speed: 400, maxHits: 5)); // 20 cm per tick: the target stays on several segments
        var owner = fixture.Add(0, 0);
        var target = fixture.Add(0, 100);
        var arrow = fixture.Fire(owner, 1);
        fixture.Run(1, 20);

        Assert.Same(target, Assert.Single(fixture.Resolver.Requests).Target);
        Assert.True(arrow.IsLive);
        Assert.Equal(new[] { target.Id }, arrow.HitTargets);
    }

    [Fact]
    public void Can_hit_false_lets_it_pass_and_dodged_does_not_count_but_parried_does()
    {
        var fixture = new Fixture(Arrow(speed: 24_000, range: 3_000, maxHits: 2));
        var owner = fixture.Add(0, 0);
        var ally = fixture.Add(0, 200);
        var dodger = fixture.Add(0, 400);
        var parrier = fixture.Add(0, 600);
        var victim = fixture.Add(0, 800);
        var behind = fixture.Add(0, 1_000);
        fixture.Resolver.CanHitTarget = target => target != ally;
        fixture.Resolver.Outcome = request => request.Target == dodger ? HitOutcome.Dodged
            : request.Target == parrier ? HitOutcome.Parried : HitOutcome.Damaged;
        var arrow = fixture.Fire(owner, 1);
        fixture.System.Step(1);

        // The ally is not resolved; the dodge passes (no hit spent); the parry and the victim spend both hits.
        Assert.Equal(new[] { dodger, parrier, victim }, fixture.Resolver.Requests.Select(r => r.Target));
        Assert.Equal(new[] { 100, 100, 100 }, fixture.Resolver.Requests.Select(r => r.DamagePercent));
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
        Assert.Contains(dodger.Id, arrow.HitTargets);
        Assert.DoesNotContain(behind.Id, arrow.HitTargets);
    }

    [Fact]
    public void Ignored_hits_and_dead_targets_change_nothing()
    {
        var fixture = new Fixture(Arrow(speed: 400));
        var owner = fixture.Add(0, 0);
        var dead = fixture.Add(0, 50);
        dead.Combatant!.Die(0);
        var ignoring = fixture.Add(0, 150);
        fixture.Resolver.Outcome = _ => HitOutcome.Ignored;
        var arrow = fixture.Fire(owner, 1);
        fixture.Run(1, 10);

        Assert.All(fixture.Resolver.Requests, r => Assert.Same(ignoring, r.Target));
        Assert.True(fixture.Resolver.Requests.Count > 1); // ignored targets are tested again on later segments
        Assert.Empty(arrow.HitTargets);
        Assert.True(arrow.IsLive);
    }

    [Fact]
    public void It_despawns_at_its_range()
    {
        var fixture = new Fixture(Arrow(range: 2_000));
        var owner = fixture.Add(0, 0);
        var arrow = fixture.Fire(owner, 100);
        fixture.Run(100, 115);
        Assert.True(arrow.IsLive);
        fixture.System.Step(116); // the 17th step reaches 2000 cm
        Assert.Equal((ProjectileDespawnReason.Range, 116u), (arrow.Reason, arrow.EndTick));
        Assert.Equal(FixVec2.FromCm(0, 2_000), arrow.Position);
    }

    [Fact]
    public void A_wall_stops_it_and_targets_behind_the_wall_are_safe()
    {
        // Open map, but the cells from y = 500 cm are a wall (cells of 100 cm from -10000).
        var grid = TestMaps.Grid(-10_000, -10_000, 100, 200, 200, 0, (_, row) => row < 105);
        var map = TestMaps.Map(grid, [TestMaps.SpawnMarker(0, 0)]);
        var fixture = new Fixture(Arrow(speed: 2_400), map: map);
        var owner = fixture.Add(0, 0);
        fixture.Add(0, 560); // inside the wall, past the stop point
        var arrow = fixture.Fire(owner, 1);
        Assert.True(arrow.Path.StoppedByWorld);
        fixture.Run(1, 10);

        Assert.Equal(ProjectileDespawnReason.World, arrow.Reason);
        Assert.Equal(arrow.Path.LastTick, arrow.EndTick);
        Assert.Equal(new FixVec2(0, FixedPoint.FromCm(500) - 1), arrow.Position);
        Assert.Empty(fixture.Resolver.Requests);
    }

    [Fact]
    public void Without_stops_on_world_it_flies_through_walls()
    {
        var grid = TestMaps.Grid(-10_000, -10_000, 100, 200, 200, 0, (_, row) => row < 105);
        var fixture = new Fixture(Arrow(stops: false), map: TestMaps.Map(grid, [TestMaps.SpawnMarker(0, 0)]));
        var arrow = fixture.Fire(fixture.Add(0, 0), 1);
        fixture.Run(1, 20);
        Assert.Equal(ProjectileDespawnReason.Range, arrow.Reason);
    }

    [Fact]
    public void Its_owner_leaving_ends_it()
    {
        var fixture = new Fixture();
        var owner = fixture.Add(0, 0);
        var other = fixture.Add(500, 0);
        var arrow = fixture.Fire(owner, 1);
        var kept = fixture.Fire(other, 1);
        fixture.System.Step(1);

        fixture.System.OwnerRemoved(owner, 2);
        Assert.Equal((ProjectileDespawnReason.OwnerGone, 2u), (arrow.Reason, arrow.EndTick));
        Assert.True(kept.IsLive);
        var position = arrow.Position;
        fixture.System.Step(2);
        Assert.Equal(position, arrow.Position); // an ended projectile no longer moves
    }

    [Fact]
    public void Spawns_over_the_owner_cap_or_the_pool_are_refused()
    {
        var fixture = new Fixture(settings: new ProjectileSettings { MaxPerOwner = 2, MaxActive = 3, EventRepeatTicks = 1 });
        var first = fixture.Add(0, 0);
        var second = fixture.Add(1_000, 0);
        Assert.NotNull(fixture.System.Spawn(first, Shot, North, 1));
        Assert.NotNull(fixture.System.Spawn(first, Shot, North, 1));
        Assert.Null(fixture.System.Spawn(first, Shot, North, 1));
        Assert.Equal(1, fixture.System.RefusedOwnerCap);

        Assert.NotNull(fixture.System.Spawn(second, Shot, North, 1));
        Assert.Null(fixture.System.Spawn(second, Shot, North, 1));
        Assert.Equal(1, fixture.System.RefusedPoolFull);
        Assert.Equal(3, fixture.System.LiveCount);
    }

    [Fact]
    public void Ended_projectiles_return_to_the_pool_after_their_repeats_with_a_clean_state()
    {
        var fixture = new Fixture(Arrow(speed: 24_000), new ProjectileSettings { MaxPerOwner = 8, MaxActive = 1, EventRepeatTicks = 3 });
        var owner = fixture.Add(0, 0);
        fixture.Add(0, 500);
        var arrow = fixture.Fire(owner, 1);
        fixture.System.Step(1);
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);

        fixture.System.ReleaseEnded(1);
        fixture.System.ReleaseEnded(2);
        Assert.Single(fixture.System.Active); // still owes its despawn to the players that saw it
        Assert.Null(fixture.System.Spawn(owner, Shot, North, 2));
        fixture.System.ReleaseEnded(3);
        Assert.Empty(fixture.System.Active);

        var reused = fixture.Fire(owner, 4, facing: 32768);
        Assert.Same(arrow, reused);
        Assert.Equal(2u, reused.Id); // ids are never reused
        Assert.True(reused.IsLive);
        Assert.Empty(reused.HitTargets);
        Assert.Empty(reused.Observers);
        Assert.Equal(0, reused.Hits);
    }

    [Fact]
    public void Projectiles_resolve_in_id_order()
    {
        var fixture = new Fixture(Arrow(speed: 24_000, maxHits: 1));
        var southShooter = fixture.Add(0, -300); // fires first: projectile 1
        var northShooter = fixture.Add(0, 300);
        var target = fixture.Add(0, 0);
        var first = fixture.Fire(southShooter, 1, facing: North);
        var second = fixture.Fire(northShooter, 1, facing: 32768);
        fixture.System.Step(1);

        Assert.Equal(new[] { southShooter, northShooter }, fixture.Resolver.Requests.Select(r => r.Attacker));
        Assert.True(first.Id < second.Id);
        Assert.All(fixture.Resolver.Requests, r => Assert.Same(target, r.Target));
    }

    [Fact]
    public void The_spawn_message_carries_the_simulated_path()
    {
        var fixture = new Fixture();
        var owner = fixture.Add(-3_000, 15);
        owner.HeightCm = 120;
        var arrow = fixture.Fire(owner, 77, facing: 12_345);
        var spawn = arrow.ToSpawn();

        Assert.Equal((arrow.Id, (ushort)1, -3_000, 15, 120, 2_400, 77u),
            (spawn.ProjectileId, spawn.DefId, spawn.XCm, spawn.YCm, spawn.HeightCm, spawn.SpeedCmPerSec, spawn.StartTick));
        Assert.Equal(Quantize.RoundTripAngle(12_345, ProjectileMessages.FacingBits), spawn.Facing);
        var rebuilt = ProjectilePath.Create(FixVec2.FromCm(spawn.XCm, spawn.YCm), spawn.Facing, spawn.SpeedCmPerSec, 2_000,
            spawn.StartTick, 20, TestMaps.Open().Grid);
        for (int step = 0; step < 20; step++)
            Assert.Equal(arrow.Path.PositionAt(step), rebuilt.PositionAt(step));
    }

    [Fact]
    public void Phase_4_does_not_allocate()
    {
        var fixture = new Fixture(Arrow(speed: 400, range: 100_000, maxHits: 32),
            new ProjectileSettings { MaxPerOwner = 16, MaxActive = 16, EventRepeatTicks = 1 });
        var owner = fixture.Add(0, 0);
        for (int i = 0; i < 8; i++)
            fixture.Add(i * 30 - 100, 300 + i * 50);
        fixture.Resolver.Outcome = _ => HitOutcome.Ignored; // keep resolving every tick
        for (int i = 0; i < 8; i++)
            fixture.Fire(owner, 1, facing: (ushort)(i * 64));
        fixture.Resolver.Requests.Capacity = 100_000;
        fixture.Run(1, 20); // warm up: lists reach their size

        long before = GC.GetAllocatedBytesForCurrentThread();
        fixture.Run(21, 60);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(8, fixture.System.LiveCount);
    }
}
