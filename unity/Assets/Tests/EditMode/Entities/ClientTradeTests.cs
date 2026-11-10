#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M4-4 on the client: the reliable <see cref="TradeState"/> and <see cref="TradeResult"/> read in Unity's build of
    /// Game.Shared (MemoryPack); <see cref="ClientTrade"/> keeps the newest state and the last answer, numbers its requests per
    /// connection and stamps each with the revision it saw, and leaves other messages to the other readers.
    /// </summary>
    public class ClientTradeTests
    {
        private static byte[] State(uint revision, TradePhase phase) =>
            ReliableMessages.Write(ReliableMessageId.TradeState, new TradeState
            {
                TradeId = 4, Revision = revision, Phase = phase, PartnerEntityId = 9, PartnerName = "Mira",
                OwnOffer = new[] { new TradeOffer { ItemNetId = 2, Count = 4, Container = 1, Slot = 3 } },
                PartnerGold = 50,
            });

        [Test]
        public void States_and_answers_are_kept_and_other_messages_are_left_alone()
        {
            var trade = new ClientTrade();

            Assert.That(trade.Apply(State(1, TradePhase.Invited), out var log), Is.True);
            Assert.That(trade.IsInvited, Is.True);
            Assert.That(log, Does.Contain("Mira"));
            Assert.That(trade.Apply(State(2, TradePhase.Open), out _), Is.True);
            Assert.That((trade.IsOpen, trade.State!.Revision, ClientTrade.Units(trade.State.OwnOffer, 2)), Is.EqualTo((true, 2u, 4)));

            var result = ReliableMessages.Write(ReliableMessageId.TradeResult,
                new TradeResult { RequestId = 7, Action = TradeAction.Lock, Reason = TradeReason.TradeChanged });
            Assert.That(trade.Apply(result, out _), Is.True);
            Assert.That(trade.ResultFor(7)!.Reason, Is.EqualTo(TradeReason.TradeChanged));
            Assert.That(trade.ResultFor(8), Is.Null);

            var other = ReliableMessages.Write(ReliableMessageId.GemResult, new GemResult { RequestId = 1 });
            Assert.That(trade.Apply(other, out _), Is.False);
        }

        [Test]
        public void Requests_are_numbered_per_connection_and_carry_the_revision_seen()
        {
            var trade = new ClientTrade();
            trade.Apply(State(5, TradePhase.Open), out _);

            var bytes = trade.Lock(out uint first);
            Assert.That(ReliableMessages.TryRead(bytes, out TradeRequest request), Is.True);
            Assert.That((first, request.Action, request.Revision), Is.EqualTo((1u, TradeAction.Lock, 5u)));
            trade.Add(1, 3, 4, out uint second);
            Assert.That(second, Is.EqualTo(2u));

            trade.Reset();
            Assert.That(trade.State, Is.Null);
            trade.Invite(9, out uint again);
            Assert.That(again, Is.EqualTo(1u));
        }
    }
}
