#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M4-8 on the client: the reliable <see cref="StallState"/> and <see cref="StallResult"/> read in Unity's build of
    /// Game.Shared (MemoryPack); <see cref="ClientStalls"/> keeps the stalls seen and the answers, numbers its requests per
    /// connection, remembers the own stall, and names the stall or the free place in reach by the server's rule.
    /// </summary>
    public class ClientStallsTests
    {
        /// <summary>Three places: 0 at the origin, 1 at 250 cm, 2 far away; a stall (entity 7) stands on place 1.</summary>
        private static (ClientStalls Stalls, EntityRegistry Registry) Setup()
        {
            var stalls = new ClientStalls();
            stalls.SetPlaces(new (int, int)[] { (0, 0), (250, 0), (5_000, 0) }, 300);
            var registry = new EntityRegistry(PredictionTestKit.Settings());
            var buffer = new byte[1024];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 10 });
            Assert.IsTrue(writer.TryAddSpawn(new SpawnEvent { EntityId = 7, Kind = EntityKind.ShopStall, DefId = 1 }));
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 1, HpBucket = 63 })); // the observer
            Assert.IsTrue(writer.TryAddEntity(new EntitySnapshot { EntityId = 7, XCm = 250, ZCm = 0, HpBucket = 63 }));
            Assert.IsTrue(registry.ApplySnapshot(buffer.AsSpan(0, writer.Finish()), out _, out _, out _));
            return (stalls, registry);
        }

        [Test]
        public void The_stall_and_the_free_place_in_reach_follow_the_servers_range()
        {
            var (stalls, registry) = Setup();

            Assert.That(stalls.NearestStall(registry, 100, 0, 100, 0), Is.EqualTo(7u)); // 150 cm from place 1
            Assert.That(stalls.NearestFreePlace(registry, 100, 0, 100, 0), Is.EqualTo(0)); // place 1 is taken
            Assert.That(stalls.NearestStall(registry, -200, 0, -200, 0), Is.EqualTo(0u)); // 450 cm: out of reach
            Assert.That(stalls.NearestFreePlace(registry, 100, 0, 400, 0), Is.EqualTo(-1)); // the confirmed position is out of reach of place 0
            Assert.That(ClientStalls.StallOn(registry, 1), Is.EqualTo(7u));
            Assert.That(ClientStalls.StallOn(registry, 0), Is.EqualTo(0u));
        }

        [Test]
        public void States_and_answers_are_kept_the_own_stall_remembered_and_other_messages_left_alone()
        {
            var (stalls, _) = Setup();
            stalls.OpenRequest(1, 1, 0, 4, 20, out uint open);
            Assert.That(open, Is.EqualTo(1u));

            var opened = ReliableMessages.Write(ReliableMessageId.StallResult,
                new StallResult { RequestId = open, Action = StallAction.Open, StallEntityId = 7, ListingId = 3, Fee = 1 });
            Assert.That(stalls.Apply(opened, out var log), Is.True);
            Assert.That(log, Does.Contain("Open"));
            Assert.That((stalls.OwnStallId, stalls.OwnPlace, stalls.ResultFor(open)!.Fee), Is.EqualTo((7u, 1, 1L)));

            var state = ReliableMessages.Write(ReliableMessageId.StallState, new StallState
            {
                StallEntityId = 7, Place = 1, Own = true, Open = true, ClosesUtcMs = 99, Cap = 8,
                Rows = new[] { new ListingEntry { ListingId = 3, UnitPrice = 20, Item = new InventorySlotEntry { ItemNetId = 2, Count = 4 } } },
            });
            Assert.That(stalls.Apply(state, out _), Is.True);
            Assert.That(stalls.StateOf(7)!.Rows.Length, Is.EqualTo(1));

            stalls.Reset(); // a new connection: entity ids are new, the own place stays
            Assert.That((stalls.OwnStallId, stalls.OwnPlace, stalls.StateOf(7)), Is.EqualTo((0u, 1, (StallState?)null)));
            stalls.BrowseRequest(7, out uint again);
            Assert.That(again, Is.EqualTo(1u));

            var closed = ReliableMessages.Write(ReliableMessageId.StallState, new StallState { StallEntityId = 7, Place = 1, Own = true, Open = false });
            stalls.Apply(closed, out _);
            Assert.That(stalls.OwnPlace, Is.EqualTo(-1));

            var other = ReliableMessages.Write(ReliableMessageId.ListingResult, new ListingResult { RequestId = 1 });
            Assert.That(stalls.Apply(other, out _), Is.False);
        }

        [Test]
        public void Requests_carry_their_action_and_fields()
        {
            var (stalls, _) = Setup();
            var bytes = stalls.BuyRequest(7, 3, 2, 20, out uint id);
            Assert.That(ReliableMessages.TryRead(bytes, out StallRequest request), Is.True);
            Assert.That((id, request.Action, request.StallEntityId, request.ListingId, request.Count, request.UnitPrice),
                Is.EqualTo((1u, StallAction.Buy, 7u, 3L, (ushort)2, 20L)));
            Assert.That(ReliableMessages.TryRead(stalls.CloseRequest(7, out _), out StallRequest close), Is.True);
            Assert.That(close.Action, Is.EqualTo(StallAction.Close));
        }
    }
}
