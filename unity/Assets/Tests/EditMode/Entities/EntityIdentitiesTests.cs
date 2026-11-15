#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.Tests.EditMode.Prediction;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// The client's name lookups (<see cref="EntityIdentities"/>) follow the server's (fixer backlog M4 wave 0, item 11): the
    /// server's whisper, party and trade lookups by name (<c>GameWorld.FindPlayerByName</c>, <c>PartyService</c>'s kick and lead by
    /// name) ignore case and take the lowest entity id, so <see cref="EntityIdentities.TryFind"/> and
    /// <see cref="EntityIdentities.FindInView"/> do too and agree with each other.
    /// </summary>
    public class EntityIdentitiesTests
    {
        private static EntityRegistry InView(params uint[] ids)
        {
            var registry = new EntityRegistry(PredictionTestKit.Settings());
            var buffer = new byte[512];
            var writer = new SnapshotWriter(buffer, new SnapshotHeader { ServerTick = 100 });
            foreach (uint id in ids) // the spawns section, then the entities section
                Assert.That(writer.TryAddSpawn(new SpawnEvent { EntityId = id, Kind = EntityKind.Player, DefId = 1 }), Is.True);
            foreach (uint id in ids)
                Assert.That(writer.TryAddEntity(new EntitySnapshot { EntityId = id, HpBucket = Snapshot.TopHpBucket }), Is.True);
            int size = writer.Finish();
            Assert.That(registry.ApplySnapshot(buffer.AsSpan(0, size), out _, out _, out _), Is.True);
            return registry;
        }

        [Test]
        public void Both_lookups_ignore_case_and_take_the_lowest_entity_id_as_the_server_does()
        {
            var identities = new EntityIdentities();
            identities.Set(5, "Ana");
            identities.Set(3, "ANA");
            identities.Set(8, "Bo");
            var registry = InView(1, 3, 5, 8); // the first entity of a snapshot is the own one, never in Entities

            Assert.That(identities.TryFind("ana", out uint found), Is.True);
            Assert.That(found, Is.EqualTo(3u));
            Assert.That(identities.TryFind("Ana", out found), Is.True); // the exact spelling of 5 still finds the lowest, 3
            Assert.That(found, Is.EqualTo(3u));
            Assert.That(identities.FindInView("aNa", registry.Entities, self: 0), Is.EqualTo(found)); // the two agree
            Assert.That(identities.FindInView("ana", registry.Entities, self: 3), Is.EqualTo(5u)); // the own entity left out
            Assert.That(identities.TryFind("bO", out found) && found == 8u, Is.True);
            Assert.That(identities.TryFind("Cy", out _), Is.False);
        }
    }
}
