using GameServer.Economy;
using GameServer.Entities;
using GameServer.Events;
using GameServer.Persistence;
using GameServer.Tests.Economy;
using GameServer.Tests.Support;
using GameServer.World.Map;
using GameServer.World.Nodes;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation;
using Shared.World;

namespace GameServer.Tests.World;

/// <summary>
/// Resource nodes in the world (spec 5.2 R-5.2-03, WP-M3-4; user decision 5): one node per <see cref="GatherNodes"/> row,
/// placed after the NPCs in marker id order, in every observer's snapshots as kind 6 with its <c>NodeDef.netId</c> and
/// never a combatant; one harvest (the <see cref="GameServer.World.GameWorld.TryDepleteNode"/> hook) depletes it and the next
/// snapshots carry <see cref="EntityFlags.Depleted"/>; it respawns at its delay on the world clock, scaled by the demand
/// loop's harvest counters, with the floor kept. The numbers are the placeholder rows of data/tables.
/// </summary>
public class ResourceNodeWorldTests
{
    private const string Near = "test.node.a.near";
    private const string Second = "test.node.b.second";
    private const string Far = "test.node.c.far";
    private const string OreT1 = "node.ore.t1";
    private const string OreT3 = "node.ore.t3";

    private sealed class Fixture
    {
        public Fixture(Action<ConfigSet>? edit = null, bool economy = true)
        {
            var tables = TestCombat.Tables();
            edit?.Invoke(tables);
            Tables = tables;
            Nodes = new NodeRules(Rows<NodeDef>(tables),
            [
                new GatherNodes { MarkerId = Far, NodeId = OreT3 }, // listed first: placement follows the marker ids
                new GatherNodes { MarkerId = Near, NodeId = OreT1 },
                new GatherNodes { MarkerId = Second, NodeId = OreT1 },
            ], tables.Single<GatherSettings>());
            Bus = new EventBus(1, new FakeClock(), TestPipelineSettings.BusLimits());
            var map = TestMaps.Map(TestMaps.Open().Grid,
            [
                TestMaps.SpawnMarker(0, 0), Node(Near, 300, 0), Node(Second, 0, 400), Node(Far, 9_000, 9_000),
                EconomyFixture.Npc("test.npc.outpost", -200, 0, "m1.outpost"),
            ]);
            Harness = new WorldHarness(map: map, events: new EventDispatchHook(Bus), bus: Bus, combat: TestCombat.Rules(tables),
                economy: economy ? EconomyRules.From(tables) : null, nodes: Nodes);
            Harness.Characters.Add(1, 0, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100));
            Player = Harness.Join(1);
            Harness.Ticks(1);
        }

        public ConfigSet Tables { get; }
        public NodeRules Nodes { get; }
        public EventBus Bus { get; }
        public WorldHarness Harness { get; }
        public Player Player { get; }
        public GameServer.World.GameWorld World => Harness.World;

        public ResourceNode NodeAt(string markerId) => World.Entities.OfType<ResourceNode>().Single(n => n.MarkerId == markerId);

        public EntitySnapshot LastState(ResourceNode node) =>
            Harness.LastSnapshot(1).Entities.Single(e => e.EntityId == node.Id);

        /// <summary>Ticks until the next snapshot went out.</summary>
        public void NextSnapshot()
        {
            int before = Harness.Transport.SnapshotsFor(1).Count;
            while (Harness.Transport.SnapshotsFor(1).Count == before)
                Harness.Ticks(1);
        }
    }

    private static WorldMarker Node(string id, int xCm, int yCm) => new(id, MarkerKind.ResourceNode, FixVec2.FromCm(xCm, yCm), 0);

    private static List<T> Rows<T>(ConfigSet set) where T : class =>
        set.TryGet(typeof(T), out var table) ? table.Rows.Cast<T>().ToList() : [];

    [Fact]
    public void Nodes_are_placed_after_the_npcs_in_marker_order_and_reach_snapshots_without_being_combatants()
    {
        var f = new Fixture();

        var nodes = f.World.Entities.OfType<ResourceNode>().ToList();
        Assert.Equal([Near, Second, Far], nodes.Select(n => n.MarkerId));
        Assert.True(f.World.Entities.OfType<Npc>().Single().Id < nodes[0].Id);
        Assert.True(nodes.Max(n => n.Id) < f.Player.Id); // placed when the world is built
        Assert.All(nodes, n => Assert.Null(n.Combatant));
        Assert.All(nodes, n => Assert.True(n.IsAvailable));
        Assert.Equal((OreT1, (ushort)1), (nodes[0].Def.Id, nodes[0].DefId));

        // Visible to everyone (rule 9: not hidden): the near ones enter the player's interest, the far one does not.
        var spawns = f.Harness.Transport.SnapshotsFor(1).SelectMany(s => s.Spawns).ToList();
        var near = spawns.Single(s => s.EntityId == nodes[0].Id);
        Assert.Equal((EntityKind.ResourceNode, (ushort)nodes[0].Def.NetId), (near.Kind, near.DefId));
        Assert.Contains(spawns, s => s.EntityId == nodes[1].Id);
        Assert.DoesNotContain(spawns, s => s.EntityId == nodes[2].Id);
        Assert.Equal(EntityFlags.None, f.LastState(nodes[0]).Flags);
        Assert.Equal(Snapshot.TopHpBucket, f.LastState(nodes[0]).HpBucket);
    }

    [Fact]
    public void A_world_without_combat_rules_has_no_nodes()
    {
        var tables = TestCombat.Tables();
        var nodes = NodeRules.From(tables);
        var harness = new WorldHarness(nodes: nodes);

        Assert.Null(harness.World.Nodes);
        Assert.Empty(harness.World.Entities.OfType<ResourceNode>());
    }

    [Fact]
    public void One_harvest_depletes_a_node_and_it_respawns_at_its_delay_on_the_world_clock()
    {
        var f = new Fixture();
        var node = f.NodeAt(Near);

        long depletedAt = f.World.NowMs;
        Assert.True(f.World.TryDepleteNode(node.Id));
        Assert.False(f.World.TryDepleteNode(node.Id)); // already depleted: harvesting refuses with NodeDepleted (WP-M3-5)
        Assert.False(f.World.TryDepleteNode(f.Player.Id)); // not a node
        Assert.Equal(NodeState.Depleted, node.State);
        Assert.Equal(depletedAt + node.Def.RespawnMs, node.RespawnAtMs); // no demand yet: the base delay
        Assert.True(f.NodeAt(Second).IsAvailable);

        f.NextSnapshot();
        Assert.Equal(EntityFlags.Depleted, f.LastState(node).Flags);
        Assert.Equal(EntityFlags.None, f.LastState(f.NodeAt(Second)).Flags);

        // Still depleted one tick before its time, available in the phase 8 of the tick that reaches it.
        while (f.World.NowMs < node.RespawnAtMs - f.World.Combat!.MsAt(1))
            f.Harness.Ticks(1);
        Assert.Equal(NodeState.Depleted, node.State);
        while (f.World.NowMs <= node.RespawnAtMs && node.State == NodeState.Depleted)
            f.Harness.Ticks(1);
        Assert.True(node.IsAvailable);
        Assert.Equal(0, node.RespawnAtMs);
        f.NextSnapshot();
        Assert.Equal(EntityFlags.None, f.LastState(node).Flags);
        Assert.True(f.World.TryDepleteNode(node.Id)); // harvestable again
    }

    [Fact]
    public void A_heavily_harvested_node_type_respawns_slower_and_a_rarely_harvested_one_keeps_the_floor()
    {
        var f = new Fixture();
        var node = f.NodeAt(Near);
        // As if ore T1 had been mined hard in this region: spawn delay 250%.
        f.World.RestoreDemandState([new DemandStateRow(DemandKeyKind.Harvests, OreT1, TestMaps.OpenRegionId, 100_000, 250, 100)]);

        long now = f.World.NowMs;
        Assert.True(f.World.TryDepleteNode(node.Id));
        Assert.Equal(now + node.Def.RespawnMs * 250L / 100, node.RespawnAtMs);

        // A tiny clamp: the delay never drops below GatherSettings.minRespawnMs, and never to zero.
        var fast = new Fixture(edit: set =>
        {
            set.Single<DemandSettings>().SpawnDelayMinPercent = 1;
            set.Single<GatherSettings>().MinRespawnMs = 20_000;
        });
        fast.World.RestoreDemandState([new DemandStateRow(DemandKeyKind.Harvests, OreT1, TestMaps.OpenRegionId, 0, 1, 100)]);
        var quiet = fast.NodeAt(Near);
        long at = fast.World.NowMs;
        Assert.True(fast.World.TryDepleteNode(quiet.Id));
        Assert.Equal(at + 20_000, quiet.RespawnAtMs);
    }

    [Fact]
    public void Harvested_events_raise_the_node_counter_and_the_next_delay()
    {
        var f = new Fixture();
        var node = f.NodeAt(Near);
        var demand = f.World.Demand!;
        // A test publisher stands in for harvesting (WP-M3-5), which sets the node's region on the envelope.
        for (int i = 0; i < 40; i++)
            f.Bus.Publish(new Harvested(1, 1, node.Def.JobId, node.Def.NodeTier, node.Def.NetId, 0, 1, $"harvest:{i}"), 1, node.RegionIndex);
        f.Bus.DispatchQueued();
        Assert.Equal(40_000, demand.HarvestsMilli(node.Def.NetId, node.RegionIndex));

        demand.Step(1);
        int percent = demand.NodeSpawnDelayPercent(node.Def.NetId, node.RegionIndex);
        Assert.True(percent > 100, $"heavy harvesting slows the respawn ({percent} %)");
        Assert.Equal(100, demand.NodeSpawnDelayPercent(f.NodeAt(Far).Def.NetId, node.RegionIndex)); // another type untouched

        long now = f.World.NowMs;
        Assert.True(f.World.TryDepleteNode(node.Id));
        Assert.Equal(now + node.Def.RespawnMs * percent / 100, node.RespawnAtMs);
    }

    [Fact]
    public void Without_the_economy_nodes_respawn_at_their_base_delay()
    {
        var f = new Fixture(economy: false);
        var node = f.NodeAt(Near);

        long now = f.World.NowMs;
        Assert.True(f.World.TryDepleteNode(node.Id));
        Assert.Equal(now + node.Def.RespawnMs, node.RespawnAtMs);
    }

    [Fact]
    public void A_row_naming_a_missing_or_wrong_marker_and_a_marker_without_a_row_are_reported()
    {
        var tables = TestCombat.Tables();
        EconomyFixture.Replace(tables,
            new GatherNodes { MarkerId = Near, NodeId = OreT1 },
            new GatherNodes { MarkerId = "test.node.ghost", NodeId = OreT1 },
            new GatherNodes { MarkerId = TestMaps.SpawnId, NodeId = OreT1 });
        var map = TestMaps.Map(TestMaps.Open().Grid, [TestMaps.SpawnMarker(0, 0), Node(Near, 300, 0), Node(Far, 900, 0)]);

        var errors = WorldLoader.CheckConfigMarkers(map, tables);

        Assert.Contains(errors, e => e.Code == WorldLoader.Codes.MissingNode && e.Message.Contains("test.node.ghost"));
        Assert.Contains(errors, e => e.Code == WorldLoader.Codes.MissingNode && e.Message.Contains("not a resourceNode"));
        Assert.Contains(errors, e => e.Code == WorldLoader.Codes.UnusedNode && e.Message.Contains(Far));
        Assert.DoesNotContain(errors, e => e.Message.Contains($"'{Near}'"));
        Assert.Throws<ArgumentException>(() => new NodeController(NodeRules.From(tables), map, () => 1));
    }
}
