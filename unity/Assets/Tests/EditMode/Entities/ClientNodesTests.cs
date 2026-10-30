#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Gameplay.Input;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// Resource nodes on the client (spec 5.2, WP-M3-4): the "in reach" hook names the nearest available node of an own job
    /// in its reach (the hook of WP-M3-5's prompt), a node's look follows the snapshot's Depleted bit, and nodes are never
    /// lock targets.
    /// </summary>
    public class ClientNodesTests
    {
        private sealed class OnlyJob : IOwnJobs
        {
            private readonly string _job;
            public OnlyJob(string job) => _job = job;
            public bool Has(string jobId) => jobId == _job;
            public int Version => 1;
        }

        private static ConfigSet Config()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(NodeDef), 1, new object[]
            {
                new NodeDef { Id = "node.ore.t1", NetId = 1, JobId = "blacksmith", ToolType = ToolType.Pickaxe, MinToolTier = 1, NodeTier = 1,
                    RespawnMs = 60_000, ReachCm = 250, VisualId = "node.ore.t1", DepletedVisualId = "node.depleted" },
                new NodeDef { Id = "node.herb", NetId = 2, JobId = "botanist", ToolType = ToolType.Pickaxe, MinToolTier = 1, NodeTier = 1,
                    RespawnMs = 60_000, ReachCm = 250, VisualId = "node.herb", DepletedVisualId = "node.depleted" },
            }));
            return set;
        }

        /// <summary>The observer at the origin, nodes 2 (ore, 120 cm), 3 (ore, depleted, 50 cm), 4 (herb, 30 cm), 5 (ore, 900 cm).</summary>
        private static EntityRegistry Registry()
        {
            var registry = new EntityRegistry(PredictionTestKit.Settings());
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10 });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 2, Kind = EntityKind.ResourceNode, DefId = 1 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.ResourceNode, DefId = 1 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 4, Kind = EntityKind.ResourceNode, DefId = 2 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 5, Kind = EntityKind.ResourceNode, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, HpBucket = 63 })); // the observer
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 2, XCm = 120, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, XCm = 0, ZCm = 50, HpBucket = 63, Flags = EntityFlags.Depleted }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 4, XCm = 0, ZCm = -30, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 5, XCm = 900, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _));
            return registry;
        }

        [Test]
        public void The_node_in_reach_is_the_nearest_available_one_of_an_own_job()
        {
            var nodes = new ClientNodes(Config());
            var registry = Registry();

            // The depleted ore (3) and the other job's herb (4) are nearer, but neither is harvestable by a Blacksmith.
            Assert.AreEqual(2u, nodes.NearestNode(registry, new OnlyJob("blacksmith"), 0, 0, 0, 0));
            Assert.AreEqual(4u, nodes.NearestNode(registry, new OnlyJob("botanist"), 0, 0, 0, 0));
            Assert.AreEqual(0u, nodes.NearestNode(registry, new OnlyJob("jeweler"), 0, 0, 0, 0));
            // Both the predicted and the confirmed position must be in reach (the server checks the confirmed one).
            Assert.AreEqual(0u, nodes.NearestNode(registry, new OnlyJob("blacksmith"), 0, 0, -400, 0));
            Assert.AreEqual(0u, nodes.NearestNode(registry, new OnlyJob("blacksmith"), 5_000, 5_000, 5_000, 5_000));
        }

        [Test]
        public void A_node_shows_its_type_and_its_depleted_look()
        {
            var ids = new DefinitionVisualIds(new ClassDef[0], new MonsterDef[0], nodes: Config().Rows<NodeDef>());

            Assert.IsTrue(ids.TryGet(EntityKind.ResourceNode, 1, out var visual));
            Assert.AreEqual("node.ore.t1", visual);
            Assert.IsTrue(ids.TryGetNode(1, depleted: true, out var depleted));
            Assert.AreEqual("node.depleted", depleted);
            Assert.IsFalse(ids.TryGetNode(9, depleted: false, out _));
            Assert.IsFalse(ids.TryGet(EntityKind.Npc, 1, out _));
        }

        [Test]
        public void The_committed_tables_give_every_node_type_a_look()
        {
            Assert.IsTrue(ClientConfig.TryLoad(out var config, out var error), error);
            var ids = DefinitionVisualIds.From(config);
            var catalog = EntityVisualCatalog.From(config);

            foreach (var node in config.Rows<NodeDef>())
            {
                Assert.IsTrue(ids.TryGetNode((ushort)node.NetId, false, out var available), node.Id);
                Assert.IsTrue(ids.TryGetNode((ushort)node.NetId, true, out var depleted), node.Id);
                Assert.IsTrue(catalog.TryGet(available, out _), available);
                Assert.IsTrue(catalog.TryGet(depleted, out _), depleted);
            }
        }

        [Test]
        public void A_node_is_never_a_lock_target()
        {
            Assert.IsFalse(TargetCandidates.IsLockable(EntityKind.ResourceNode, Shared.Simulation.Actions.ActionState.Idle, 63, false));
        }
    }

    internal static class ConfigSetRows
    {
        public static T[] Rows<T>(this ConfigSet set) where T : class
        {
            if (!set.TryGet(typeof(T), out var table))
                return Array.Empty<T>();
            var rows = new T[table.Rows.Count];
            for (int i = 0; i < rows.Length; i++)
                rows[i] = (T)table.Rows[i];
            return rows;
        }
    }
}
