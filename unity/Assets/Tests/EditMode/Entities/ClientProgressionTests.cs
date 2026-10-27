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
    }
}
