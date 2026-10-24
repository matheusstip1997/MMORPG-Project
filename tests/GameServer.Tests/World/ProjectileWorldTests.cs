using GameServer.Combat;
using GameServer.Entities;
using GameServer.Net;
using GameServer.Tests.Support;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.Simulation.Actions;
using Shared.Simulation.Combat;
using static GameServer.Tests.Support.TestWorlds;

namespace GameServer.Tests.World;

/// <summary>
/// Projectiles in the world tick by tick (spec 3.2, backlog D3) with the committed tables: the archer's Shot (left click)
/// fires in phase 3, flies in phase 4, and its spawn and despawn reach the players its path concerns in phase 10,
/// repeated against loss; then it goes back to the pool.
/// </summary>
public class ProjectileWorldTests
{
    /// <summary>The players are archers: their normal attack, Shot, fires one arrow (backlog E).</summary>
    private static WorldHarness Archers(WorldHarness harness)
    {
        harness.Characters.AutoCreateClassId = "archer";
        return harness;
    }

    private const ushort East = 16_384;

    private static WorldHarness Harness()
    {
        var set = TestCombat.Tables();
        return Archers(new WorldHarness(actions: ActionCatalog.From(set), combat: TestCombat.Rules(set, []),
            projectiles: ProjectileRules.From(set)));
    }

    private static List<ProjectileSpawn> SpawnsFor(WorldHarness harness, int peerId)
    {
        var spawns = new List<ProjectileSpawn>();
        foreach (var (peer, channel, data) in harness.Transport.Sent)
        {
            if (peer != peerId || data[0] != (byte)HotMessageId.ProjectileSpawn)
                continue;
            Assert.Equal(Channel.UnreliableSequenced, channel);
            Assert.True(ProjectileSpawnReader.TryBegin(data, out var reader));
            while (reader.TryRead(out var spawn))
                spawns.Add(spawn);
        }
        return spawns;
    }

    private static List<ProjectileDespawn> DespawnsFor(WorldHarness harness, int peerId)
    {
        var despawns = new List<ProjectileDespawn>();
        foreach (var (peer, _, data) in harness.Transport.Sent)
        {
            if (peer != peerId || data[0] != (byte)HotMessageId.ProjectileDespawn)
                continue;
            Assert.True(ProjectileDespawnReader.TryBegin(data, out var reader));
            while (reader.TryRead(out var despawn))
                despawns.Add(despawn);
        }
        return despawns;
    }

    private static void Teleport(WorldHarness harness, Player player, int xCm, int yCm)
    {
        player.Movement = new MovementState(FixVec2.FromCm(xCm, yCm), player.Movement.Facing);
        player.SpeedCheckedPosition = player.Movement.Position;
        harness.World.Grid.Update(player);
    }

    /// <summary>Presses Shot (the attack) facing east and runs ticks until its effect fires; returns the tick it fired in.</summary>
    private static uint Shoot(WorldHarness harness, int peerId, ushort sequence = 1)
    {
        harness.SendInputs(peerId, Frame(sequence, facing: East, actions: InputActions.Attack));
        var projectiles = harness.World.Projectiles!;
        for (int i = 0; i < 40; i++)
        {
            uint tick = harness.World.ServerTick;
            int before = projectiles.LiveCount;
            harness.World.Tick();
            if (projectiles.LiveCount > before || projectiles.Active.Count > 0)
                return tick;
        }
        throw new InvalidOperationException("Shot never fired.");
    }

    [Fact]
    public void Arrow_shot_spawns_an_arrow_that_flies_its_range_and_every_nearby_player_hears_of_it()
    {
        var harness = Harness();
        var shooter = harness.Join(1);
        harness.Join(2);
        uint fired = Shoot(harness, 1);
        var arrow = Assert.Single(harness.World.Projectiles!.Active);
        Assert.Same(shooter, arrow.Owner);
        Assert.Equal(fired, arrow.Path.StartTick);
        Assert.Equal(East, arrow.Path.Facing);

        var expected = arrow.ToSpawn();
        Assert.Equal(1, expected.DefId);
        Assert.Equal((shooter.XCm, shooter.YCm, 2_400), (expected.XCm, expected.YCm, expected.SpeedCmPerSec));
        // Spawn ticks: sent on the fire tick and repeated, 3 times in all (ProjectileSettings.eventRepeatTicks).
        foreach (int peer in new[] { 1, 2 })
        {
            var spawns = SpawnsFor(harness, peer);
            Assert.Single(spawns);
            Assert.Equal(expected, spawns[0]);
        }
        harness.Ticks(2);
        Assert.Equal(3, SpawnsFor(harness, 2).Count);
        harness.Ticks(1);
        Assert.Equal(3, SpawnsFor(harness, 2).Count);

        // The open test map has no walls: 2000 cm at 120 cm per tick ends on its 17th step.
        uint last = arrow.Path.LastTick;
        while (harness.World.ServerTick <= last)
            harness.World.Tick();
        Assert.Equal((ProjectileDespawnReason.Range, last), (arrow.Reason, arrow.EndTick));
        harness.Ticks(2);
        var despawns = DespawnsFor(harness, 2);
        Assert.Equal(3, despawns.Count);
        Assert.All(despawns, d => Assert.Equal(new ProjectileDespawn { ProjectileId = expected.ProjectileId, Tick = last,
            Reason = ProjectileDespawnReason.Range }, d));
        Assert.Empty(harness.World.Projectiles.Active); // back to the pool after its repeats
        harness.Ticks(3);
        Assert.Equal(3, DespawnsFor(harness, 2).Count);
    }

    [Fact]
    public void A_player_whose_interest_the_path_never_crosses_gets_nothing()
    {
        var harness = Harness();
        harness.Join(1);
        var far = harness.Join(2);
        Teleport(harness, far, 0, 6_000); // 60 m north: the eastward 20 m path stays out of its 30 m square
        Shoot(harness, 1);
        harness.Ticks(30);

        Assert.NotEmpty(SpawnsFor(harness, 1));
        Assert.Empty(SpawnsFor(harness, 2));
        Assert.Empty(DespawnsFor(harness, 2));
    }

    [Fact]
    public void A_player_the_path_reaches_later_gets_a_late_spawn_with_the_original_start_tick()
    {
        var harness = Harness();
        harness.Join(1);
        var late = harness.Join(2);
        Teleport(harness, late, 5_100, 0); // 51 m east: the path ends at 20 m, 1 m short of its 30 m square
        uint fired = Shoot(harness, 1);
        harness.Ticks(3);
        Assert.Empty(SpawnsFor(harness, 2));

        Teleport(harness, late, 4_000, 0); // now the square reaches back to 10 m: the rest of the path crosses it
        harness.Ticks(1);
        var spawn = Assert.Single(SpawnsFor(harness, 2));
        Assert.Equal(fired, spawn.StartTick);
    }

    [Fact]
    public void The_owner_leaving_the_world_despawns_its_arrow()
    {
        var harness = Harness();
        var shooter = harness.Join(1);
        harness.Join(2);
        Shoot(harness, 1);
        var arrow = Assert.Single(harness.World.Projectiles!.Active);
        harness.Leave(1); // no linger in the test sessions: removed once its logout save (stamina spent) is done
        for (int i = 0; i < 5 && harness.World.Entities.Contains(shooter); i++)
            harness.World.Tick();
        Assert.DoesNotContain(shooter, harness.World.Entities);

        Assert.Equal(ProjectileDespawnReason.OwnerGone, arrow.Reason);
        Assert.Contains(DespawnsFor(harness, 2), d => d.Reason == ProjectileDespawnReason.OwnerGone);
    }

    [Fact]
    public void A_wall_in_front_of_the_shooter_stops_the_arrow()
    {
        // The test map with the cells from x = 500 cm east blocked.
        var grid = TestMaps.Grid(-10_000, -10_000, 100, 200, 200, 0, (column, _) => column < 105);
        var set = TestCombat.Tables();
        var harness = Archers(new WorldHarness(map: TestMaps.Map(grid, [TestMaps.SpawnMarker(0, 0)]), actions: ActionCatalog.From(set),
            combat: TestCombat.Rules(set, []), projectiles: ProjectileRules.From(set)));
        harness.Join(1);
        Shoot(harness, 1);
        var arrow = Assert.Single(harness.World.Projectiles!.Active);
        Assert.True(arrow.Path.StoppedByWorld);
        harness.Ticks(10);

        var despawn = Assert.Single(DespawnsFor(harness, 1).Distinct());
        Assert.Equal((ProjectileDespawnReason.World, arrow.Path.LastTick), (despawn.Reason, despawn.Tick));
        Assert.Equal(new FixVec2(FixedPoint.FromCm(500) - 1, 0), arrow.Path.End);
    }

    [Fact]
    public void Projectile_effects_reach_the_system_through_the_effect_router_only_with_projectile_tables()
    {
        var set = TestCombat.Tables();
        var without = Archers(new WorldHarness(actions: ActionCatalog.From(set), combat: TestCombat.Rules(set, [])));
        Assert.Null(without.World.Projectiles);
        Assert.Same(NoActionEffectSink.Instance, without.World.EffectRouter.Projectiles);
        without.Join(1);
        without.SendInputs(1, Frame(1, facing: East, actions: InputActions.Attack));
        without.Ticks(10); // fired into nothing

        var with = Harness();
        Assert.Same(with.World.Projectiles, with.World.EffectRouter.Projectiles);
        Assert.Same(with.World.Hits, with.World.Projectiles!.Resolver); // R-3.2-02: WP4's pipeline
    }

    /// <summary>The committed tables on the open test map with monsters standing east of the player spawn (0, 0).</summary>
    private static WorldHarness HarnessWithMonsters(params int[] monsterXCm)
    {
        var markers = monsterXCm.Select((x, i) => TestCombat.MonsterMarker(x, 0, id: $"test.monster.{i}")).ToArray();
        var points = markers.Select(m => TestCombat.Point(markerId: m.Id)).ToArray();
        var set = TestCombat.Tables();
        return Archers(new WorldHarness(map: TestCombat.OpenMap(markers), actions: ActionCatalog.From(set),
            combat: TestCombat.Rules(set, points), projectiles: ProjectileRules.From(set)));
    }

    [Fact]
    public void Arrow_shot_damages_the_standing_monster_through_the_hit_pipeline()
    {
        // R-3.2-02: the arrow hits through WP4's HitResolver: damage, the hit message (4), then a Hit despawn.
        var harness = HarnessWithMonsters(1_000);
        var shooter = harness.Join(1); // the join tick spawns the monster too
        var monster = Assert.Single(harness.World.Entities.OfType<Monster>());
        var combatant = monster.Combatant!;
        int fullHp = combatant.MaxHp(harness.World.NowMs);
        Shoot(harness, 1);
        var arrow = Assert.Single(harness.World.Projectiles!.Active);
        harness.Ticks(10); // 10 m at 1.2 m per tick

        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
        Assert.True(combatant.Hp(harness.World.NowMs) < fullHp, "the arrow dealt damage");
        var hit = Assert.Single(harness.Transport.HitsFor(1)).Hit;
        Assert.Equal((shooter.Id, monster.Id), (hit.AttackerId, hit.TargetId));
        Assert.Equal(fullHp - combatant.Hp(harness.World.NowMs), hit.Amount);
        var despawn = DespawnsFor(harness, 1).Distinct().Single();
        Assert.Equal((ProjectileDespawnReason.Hit, arrow.EndTick), (despawn.Reason, despawn.Tick));
    }

    [Fact]
    public void A_non_piercing_arrow_stops_at_its_first_target()
    {
        // R-3.2-01: maxHits 1 (the committed arrow). Two monsters in line: the nearer one takes the hit, the one behind none.
        var harness = HarnessWithMonsters(600, 1_200);
        harness.Join(1);
        var monsters = harness.World.Entities.OfType<Monster>().OrderBy(m => m.XCm).ToArray();
        Assert.Equal(2, monsters.Length);
        long now = harness.World.NowMs;
        int nearFull = monsters[0].Combatant!.MaxHp(now), farFull = monsters[1].Combatant!.MaxHp(now);
        Shoot(harness, 1);
        var arrow = Assert.Single(harness.World.Projectiles!.Active);
        harness.Ticks(20);

        now = harness.World.NowMs;
        Assert.True(monsters[0].Combatant!.Hp(now) < nearFull);
        Assert.Equal(farFull, monsters[1].Combatant!.Hp(now));
        Assert.Equal(ProjectileDespawnReason.Hit, arrow.Reason);
        Assert.Equal(new[] { monsters[0].Id }, harness.Transport.HitsFor(1).Select(h => h.Hit.TargetId));
        // It ended on the segment that reached the near monster, well short of the one behind.
        Assert.True(arrow.Position.X < FixVec2.FromCm(1_200 - 50 - 15, 0).X);
    }

    [Fact]
    public void Ticks_that_fire_fly_and_send_arrows_do_not_allocate()
    {
        var set = TestCombat.Tables();
        // Saves far apart, so the fake save worker records nothing while measuring.
        var harness = Archers(new WorldHarness(actions: ActionCatalog.From(set), combat: TestCombat.Rules(set, []),
            projectiles: ProjectileRules.From(set), values: TestSessions.Values(class2Ms: 600_000, volatileMs: 600_000)));
        const int players = 3;
        for (int peer = 1; peer <= players; peer++)
            harness.Join(peer);
        harness.Transport.Record = false;
        var packet = new byte[InputPacket.MaxSize];
        var frames = new InputFrame[1];
        ushort sequence = 10;

        void TickWithInputs(int count)
        {
            for (int i = 0; i < count; i++)
            {
                sequence++;
                // The attack held for 25 ticks, then released: one Shot every 50 ticks.
                var held = sequence / 25 % 2 == 0 ? InputActions.Attack : InputActions.None;
                frames[0] = Frame(sequence, facing: (ushort)(sequence * 64), actions: held);
                int length = InputPacket.Write(packet, frames);
                for (int peer = 1; peer <= players; peer++)
                    harness.Router.OnDataReceived(peer, Channel.UnreliableSequenced, packet.AsSpan(0, length));
                harness.World.Tick();
            }
        }

        TickWithInputs(200);
        var projectiles = harness.World.Projectiles!;
        long spawnedBefore = projectiles.Spawned;
        long before = GC.GetAllocatedBytesForCurrentThread();
        TickWithInputs(200);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.True(projectiles.Spawned - spawnedBefore >= 2 * players, "arrows fired while measuring");
        Assert.Equal(0, projectiles.RefusedOwnerCap + projectiles.RefusedPoolFull);
        Assert.True(harness.Transport.SendCount > 0);
    }

    [Theory]
    [InlineData(0, 0, 100, 0, true)] // starts inside
    [InlineData(-100, 10, -20, 10, true)] // ends on the left edge
    [InlineData(-100, 10, -21, 10, false)]
    [InlineData(-100, -100, 100, 100, true)] // the diagonal
    [InlineData(-30, 0, 0, 30, true)] // clips the corner (-20, 20)
    [InlineData(-100, 50, -50, 100, false)] // passes outside the corner
    [InlineData(500, 500, 500, 500, false)] // a point outside
    public void A_segment_crosses_a_square_exactly(int ax, int ay, int bx, int by, bool crosses) =>
        Assert.Equal(crosses, ProjectileSender.SegmentCrossesSquare(ax, ay, bx, by, 0, 0, 20));
}
