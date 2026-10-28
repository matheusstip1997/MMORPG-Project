using GameServer.Host.Config;
using GameServer.Tests.Support;
using GameServer.World.Map;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>
/// The first field zone in the committed world (WP-M2-7, spec 12.8 and 2.3; data/world/m1, scene M1Sample.unity with the
/// FieldZoneGreybox): open world rings around the M1 combat area, monster levels rising ring by ring up to the field boss
/// at the far end, both outposts out of every leash, enough spawn points per type for five bots leveling together, and
/// every marker reachable on foot from the city spawn.
/// </summary>
public class FieldZoneWorldTests
{
    private static readonly string[] Rings = ["field1.ring1", "field1.ring2", "field1.ring3", "field1.ring4"];

    private static readonly Lazy<(WorldMap Map, ConfigSet Set)> World = new(() =>
    {
        var set = ConfigLoader.LoadAndValidate(RepositoryPaths.Tables(), ConfigTables.All).Set;
        var world = WorldLoader.Load(RepositoryPaths.M1World(), set);
        Assert.True(world.Ok, string.Join("\n", world.Errors));
        return (world.Map!, set);
    });

    private static WorldMap Map => World.Value.Map;

    private static List<T> Rows<T>() =>
        World.Value.Set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : [];

    private static MonsterDef Monster(string id) => Rows<MonsterDef>().Single(m => m.Id == id);

    private static FixVec2 PlayerSpawn()
    {
        Assert.True(Map.TryGetMarker(Rows<ServerLoop>().Single().PlayerSpawnMarkerId, out var spawn));
        return spawn.Position;
    }

    /// <summary>Ring 1..4 of a position, with the M1 combat area counted as ring 1 (the tutorial ring wraps around it).</summary>
    private static int RingOf(FixVec2 position)
    {
        var region = Map.RegionAt(position);
        Assert.NotNull(region);
        return region!.Id == "m1.combat" ? 1 : Array.IndexOf(Rings, region.Id) + 1;
    }

    [Fact]
    public void The_rings_are_open_world_regions_next_to_the_city()
    {
        foreach (var ring in Rings)
        {
            var region = Map.Regions.Single(r => r.Id == ring);
            Assert.Equal("openWorld", region.AreaType);
            Assert.Equal(PvpMode.ArmedOnly, region.Flags.PvpMode); // spec 2.3 open world flags
        }
        Assert.Equal("normalCity", Map.RegionAt(PlayerSpawn())!.AreaType);
        // About 400 x 400 m (spec 12.8): the zone spans X -10..390 m, Z -200..200 m.
        var bounds = Map.Manifest.Bounds;
        Assert.Equal((-5_000, -20_000, 39_000, 20_000), (bounds.MinXCm, bounds.MinYCm, bounds.MaxXCm, bounds.MaxYCm));
    }

    [Fact]
    public void Monster_levels_rise_ring_by_ring_and_the_field_boss_stands_at_the_far_end()
    {
        var byRing = new Dictionary<int, List<int>>();
        string? farthest = null;
        int farthestX = int.MinValue;
        foreach (var point in Rows<SpawnPoints>())
        {
            Assert.True(Map.TryGetMarker(point.MarkerId, out var marker), point.MarkerId);
            var monster = Monster(point.MonsterId);
            int ring = RingOf(marker.Position);
            Assert.InRange(ring, 1, 4);
            (byRing.TryGetValue(ring, out var levels) ? levels : byRing[ring] = []).Add(monster.Level);
            if (marker.Position.X > farthestX) // the far end: the zone runs east from the city gate
            {
                farthestX = marker.Position.X;
                farthest = point.MonsterId;
            }
        }
        for (int ring = 2; ring <= 4; ring++)
            Assert.True(byRing[ring].Min() >= byRing[ring - 1].Max(), $"ring {ring} has a monster below ring {ring - 1}'s levels");
        Assert.Equal("m2.warlord", farthest);
        Assert.True(Monster("m2.warlord").Boss);
    }

    [Fact]
    public void Both_outposts_have_an_outpost_and_a_vendor_npc_out_of_every_leash()
    {
        var npcs = Map.Markers.Where(m => m.Kind == MarkerKind.Npc).ToList();
        var kinds = Rows<NpcDef>().ToDictionary(n => n.Id, n => n.Kind);
        Assert.Equal(2, npcs.Count(n => kinds[n.DefId] == NpcKind.Outpost));
        Assert.Equal(2, npcs.Count(n => kinds[n.DefId] == NpcKind.Vendor));
        Assert.True(Map.TryGetMarker("field1.npc.outpost.mid", out var mid));
        Assert.Equal(2, RingOf(mid.Position)); // mid-zone
        Assert.Empty(WorldLoader.CheckNpcsOutsideLeashes(Map, Rows<SpawnPoints>(), Rows<MonsterDef>()));
    }

    [Fact]
    public void Each_type_of_bands_2_to_4_has_twelve_spawn_points_and_band_1_eight()
    {
        // The spawn arithmetic of the PR (five bots leveling together, the demand loop at its 200 % respawn cap).
        var counts = Rows<SpawnPoints>().GroupBy(p => p.MonsterId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var id in new[] { "m2.imp", "m2.spitter", "m2.wolf", "m2.lurker", "m2.brute", "m2.hellhound" })
            Assert.Equal(12, counts[id]);
        Assert.Equal(8, counts["m1.demon"]);
        Assert.Equal(8, counts["m1.alien"]);
        Assert.Equal(1, counts["m2.warlord"]);
    }

    [Fact]
    public void Every_spawn_npc_and_resource_marker_is_reachable_on_foot_from_the_city_spawn()
    {
        var grid = Map.Grid;
        var reached = new bool[grid.Width * grid.Height];
        var queue = new Queue<int>();
        int start = Cell(grid, PlayerSpawn());
        reached[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int cell = queue.Dequeue();
            int column = cell % grid.Width, row = cell / grid.Width;
            foreach (var (dc, dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int c = column + dc, r = row + dr;
                if (!grid.IsWalkableCell(c, r) || reached[r * grid.Width + c])
                    continue;
                reached[r * grid.Width + c] = true;
                queue.Enqueue(r * grid.Width + c);
            }
        }
        foreach (var marker in Map.Markers)
            Assert.True(reached[Cell(grid, marker.Position)], $"{marker.Id} cannot be reached from the city spawn");
        Assert.Contains(Map.Markers, m => m.Kind == MarkerKind.ResourceNode); // data only until M3
    }

    private static int Cell(WorldGrid grid, FixVec2 position) => grid.RowOf(position.Y) * grid.Width + grid.ColumnOf(position.X);
}
