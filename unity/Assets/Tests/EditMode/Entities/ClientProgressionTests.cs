#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Simulation.Stats;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M2-3 on the client: the reliable <see cref="ProgressionState"/> reads in Unity's build of Game.Shared (MemoryPack),
    /// <see cref="ClientProgression"/> keeps the last one, and other messages are left to the other readers.
    /// </summary>
    public class ClientProgressionTests
    {
        [Test]
        public void A_progression_message_is_kept_and_others_are_not_taken()
        {
            var progression = new ClientProgression();
            var message = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
            {
                Level = 4, Exp = 120, ExpToNext = 520, Rank = Rank.E, SkillPoints = 4, BandPercent = 100, Cause = ProgressionCause.LevelUp,
            });

            Assert.That(progression.Apply(message, out var log), Is.True);
            Assert.That(progression.State, Is.Not.Null);
            Assert.That((progression.State!.Level, progression.State.Exp, progression.State.Rank, progression.State.Cause),
                Is.EqualTo((4, 120L, Rank.E, ProgressionCause.LevelUp)));
            Assert.That(log, Does.Contain("level 4"));

            var other = ReliableMessages.Write(ReliableMessageId.InventoryMoveResult,
                new InventoryMoveResult { RequestId = 1, Outcome = InventoryMoveOutcome.Moved });
            Assert.That(progression.Apply(other, out _), Is.False);

            progression.Reset();
            Assert.That(progression.State, Is.Null);
        }

        [Test]
        public void An_unreadable_progression_message_is_dropped()
        {
            var progression = new ClientProgression();
            var message = ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState { Level = 0 }); // no level 0

            Assert.That(progression.Apply(message, out var log), Is.True);
            Assert.That(progression.State, Is.Null);
            Assert.That(log, Does.Contain("unreadable"));
        }

        [Test]
        public void Attribute_points_are_kept_requested_and_answered()
        {
            // WP-M2-13 (R-3.4-08): the unspent and placed points in the state, a request with the next id, its answer.
            var progression = new ClientProgression();
            Assert.That(progression.Apply(ReliableMessages.Write(ReliableMessageId.ProgressionState, new ProgressionState
            {
                Level = 6, AttributePoints = 3, StrPlaced = 1, IntPlaced = 1, Cause = ProgressionCause.AttributePoints,
            }), out _), Is.True);
            Assert.That((progression.State!.AttributePoints, progression.State.Placed(Attributes.Str), progression.State.Placed(Attributes.Int)),
                Is.EqualTo((3, 1, 1)));

            var bytes = progression.Request(Attributes.Dex, out uint requestId);
            Assert.That(requestId, Is.EqualTo(1u));
            Assert.That(ReliableMessages.TryRead(bytes, out AttributeSpendRequest request), Is.True);
            Assert.That((request.RequestId, request.Attribute), Is.EqualTo((1u, (byte)Attributes.Dex)));

            var answer = ReliableMessages.Write(ReliableMessageId.AttributeSpendResult,
                new AttributeSpendResult { RequestId = 1, Attribute = Attributes.Dex, Reason = AttributeReason.NoAttributePoints });
            Assert.That(progression.Apply(answer, out var log), Is.True);
            Assert.That(progression.LastAttributeResult!.Reason, Is.EqualTo(AttributeReason.NoAttributePoints));
            Assert.That(log, Does.Contain("NoAttributePoints"));

            progression.Reset();
            Assert.That((progression.LastAttributeResult, progression.AttributeRequestsSent), Is.EqualTo(((AttributeSpendResult?)null, 0u)));
        }
    }
}
