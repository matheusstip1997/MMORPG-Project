#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Gameplay.Entities.Visuals;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// Backlog G on the client: the MemoryPack reliable messages work in Unity's build of Game.Shared (the source generator
    /// runs there too), the bag text comes from the tables, a pickup press names the nearest drop in range, and ground
    /// drops get their item's visual.
    /// </summary>
    public class ClientInventoryTests
    {
        private static ConfigSet Config()
        {
            var set = new ConfigSet();
            set.Add(new ConfigTable(typeof(UiStrings), 1, new object[]
            {
                new UiStrings { Id = "item.horn", Text = "Demon Horn" },
                new UiStrings { Id = "dev.inventory", Text = "Bag: {items}" },
                new UiStrings { Id = "dev.inventory_empty", Text = "Bag: empty" },
            }));
            set.Add(new ConfigTable(typeof(ItemDef), 1, new object[]
            {
                new ItemDef { Id = "m1.demonHorn", NetId = 1, Category = ItemCategory.SellOnly, MaxStack = 99, VisualId = "drop.demonHorn", NameId = "item.horn" },
            }));
            set.Add(new ConfigTable(typeof(ReasonCodeStrings), 1, new object[]
            {
                new ReasonCodeStrings { Code = "BagFull", Text = "Your bag is full." },
                new ReasonCodeStrings { Code = ReasonCodeStrings.UnknownCode, Text = "Something went wrong ({code})." },
            }));
            set.Add(new ConfigTable(typeof(LootSettings), 1, new object[]
            {
                new LootSettings { PickupRangeCm = 300, DropScatterCm = 60, MaxQueuedPickupRequests = 4, MaxDropsPerWorld = 10 },
            }));
            return set;
        }

        private static byte[] Contents(params InventorySlotEntry[] slots) =>
            ReliableMessages.Write(ReliableMessageId.InventoryContents, new InventoryContents { Revision = 1, SlotCount = 30, Slots = slots });

        [Test]
        public void MemoryPack_messages_round_trip_in_Unity()
        {
            var bytes = ReliableMessages.Write(ReliableMessageId.PickupRequest, new PickupRequest { DropEntityId = 77 });

            Assert.IsTrue(ReliableMessages.TryRead(bytes, out PickupRequest request));
            Assert.AreEqual(77u, request.DropEntityId);
        }

        [Test]
        public void The_bag_text_names_items_from_the_tables()
        {
            var inventory = new ClientInventory(Config());
            Assert.AreEqual("Bag: empty", inventory.Text());

            Assert.IsTrue(inventory.Apply(Contents(new InventorySlotEntry { Slot = 0, ItemNetId = 1, Count = 2 }), out string log));

            Assert.AreEqual("Bag: Demon Horn x2", inventory.Text());
            Assert.AreEqual(inventory.Text(), log);
        }

        [Test]
        public void Pickup_answers_are_kept_and_refusals_use_the_reason_text()
        {
            var inventory = new ClientInventory(Config());
            var refused = ReliableMessages.Write(ReliableMessageId.PickupResult, new PickupResult { DropEntityId = 5, Reason = PickupReason.BagFull });

            Assert.IsTrue(inventory.Apply(refused, out string log));

            Assert.AreEqual("Your bag is full.", log);
            Assert.AreEqual(PickupReason.BagFull, inventory.LastResult!.Reason);
            Assert.AreEqual("Something went wrong (LootLocked).", inventory.ReasonText(PickupReason.LootLocked));
            Assert.IsFalse(inventory.Apply(new byte[] { (byte)ReliableMessageId.ActionResult, 0, 0, 0, 0, 0, 1 }, out _));
        }

        [Test]
        public void A_pickup_press_names_the_nearest_drop_in_range()
        {
            var registry = new EntityRegistry(PredictionTestKit.Settings());
            var buffer = new byte[2048];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10 });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 2, Kind = EntityKind.LootDrop, DefId = 1 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 3, Kind = EntityKind.LootDrop, DefId = 1 }));
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 4, Kind = EntityKind.LootDrop, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, HpBucket = 63 })); // the observer
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 2, XCm = 250, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 3, XCm = 0, ZCm = 120, HpBucket = 63 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 4, XCm = 900, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _));
            var inventory = new ClientInventory(Config());

            Assert.AreEqual(3u, inventory.NearestDrop(registry, 0, 0));
            Assert.AreEqual(0u, inventory.NearestDrop(registry, 5_000, 5_000));
        }

        [Test]
        public void A_ground_drop_shows_its_items_visual()
        {
            var ids = DefinitionVisualIds.From(Config());

            Assert.IsTrue(ids.TryGet(EntityKind.LootDrop, 1, out string visualId));
            Assert.AreEqual("drop.demonHorn", visualId);
            Assert.IsFalse(ids.TryGet(EntityKind.Monster, 1, out _));
        }
    }
}
