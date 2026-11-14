#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// The NPC in reach (WP-M2-4) and the mailbox in reach (WP-M4-14, R-4.5-03; fixer backlog M4 wave 0, item 13): the server
    /// takes a mail claim at any mailbox in reach, so the client names the nearest mailbox in reach even when another NPC
    /// (the one the interaction key talks to) stands nearer.
    /// </summary>
    public class ClientOutpostsNearestTests
    {
        private const ushort OutpostNetId = 1, MailboxNetId = 6;

        private static ConfigSet Config()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(NpcDef), 1, new object[]
            {
                new NpcDef { Id = "m1.outpost", NetId = OutpostNetId, Kind = NpcKind.Outpost, Interactions = new[] { NpcInteraction.Sell },
                    InteractRangeCm = 300 },
                new NpcDef { Id = "m1.mailbox", NetId = MailboxNetId, Kind = NpcKind.Mailbox, Interactions = new[] { NpcInteraction.Mail },
                    InteractRangeCm = 300 },
            }));
            return set;
        }

        /// <summary>The observer (1) at the origin, the outpost (2) 100 cm away, the mailbox (3) 250 cm away, a mailbox (4) out of reach.</summary>
        private static EntityRegistry Registry()
        {
            var registry = new EntityRegistry(PredictionTestKit.Settings());
            var buffer = new byte[1024];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10 });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 2, Kind = EntityKind.Npc, DefId = OutpostNetId }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.Npc, DefId = MailboxNetId }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 4, Kind = EntityKind.Npc, DefId = MailboxNetId }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, HpBucket = 63 })); // the observer
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 2, XCm = 100, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, XCm = 0, ZCm = 250, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 4, XCm = 2_000, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _));
            return registry;
        }

        [Test]
        public void The_mailbox_in_reach_is_found_beside_a_nearer_npc()
        {
            var outposts = new ClientOutposts(Config());
            var registry = Registry();

            Assert.AreEqual(2u, outposts.NearestNpc(registry, 0, 0, 0, 0)); // the interaction key talks to the outpost
            Assert.AreEqual(3u, outposts.NearestNpc(registry, 0, 0, 0, 0, NpcInteraction.Mail)); // the claims name the mailbox
            Assert.AreEqual(0u, outposts.NearestNpc(registry, 0, 0, 0, 0, NpcInteraction.Auction)); // nobody offers it
            Assert.AreEqual(0u, outposts.NearestNpc(registry, 0, -200, 0, -200, NpcInteraction.Mail)); // 450 cm: out of reach
        }
    }
}
