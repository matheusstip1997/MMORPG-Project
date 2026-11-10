#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M4-2 on the client: the party messages read in Unity's build of Game.Shared (MemoryPack); <see cref="ClientParty"/>
    /// keeps the newest state, the waiting invite with its local deadline and the last answer, numbers its requests per
    /// connection, and leaves other messages to the other readers. <see cref="ChatCommandParser"/> reads the player commands
    /// and leaves every other line, dev console lines included, to be sent as typed.
    /// </summary>
    public class ClientPartyTests
    {
        private static byte[] State(uint version, long partyId, params uint[] members)
        {
            var entries = new PartyMemberState[members.Length];
            for (int i = 0; i < members.Length; i++)
                entries[i] = new PartyMemberState { EntityId = members[i], Name = "m" + members[i], Level = 3, Online = true, HpPercent = 50 };
            return ReliableMessages.Write(ReliableMessageId.PartyState, new PartyState
            {
                Version = version, PartyId = partyId, LeaderEntityId = members.Length > 0 ? members[0] : 0, Members = entries,
            });
        }

        [Test]
        public void The_newest_state_wins_and_members_are_keyed_by_entity()
        {
            var party = new ClientParty();
            Assert.That(party.Apply(State(2, 7, 10, 11), 0, out _), Is.True);
            Assert.That(party.Apply(State(1, 0), 0, out _), Is.True); // an older version is ignored

            Assert.That(party.InParty, Is.True);
            Assert.That(party.Member(11)!.Name, Is.EqualTo("m11"));
            Assert.That(party.Member(99), Is.Null);

            Assert.That(party.Apply(State(3, 0), 0, out _), Is.True);
            Assert.That(party.InParty, Is.False);
        }

        [Test]
        public void An_invite_lapses_on_the_local_clock_and_an_answer_closes_it()
        {
            var party = new ClientParty();
            var invite = ReliableMessages.Write(ReliableMessageId.PartyInvite,
                new PartyInvite { InviteId = 4, InviterName = "Aria", InviterEntityId = 3, TimeoutMs = 1_000 });
            Assert.That(party.Apply(invite, 500, out _), Is.True);
            Assert.That(party.Invite(1_499)!.InviteId, Is.EqualTo(4u));
            Assert.That(party.Invite(1_500), Is.Null);

            Assert.That(party.Apply(invite, 2_000, out _), Is.True);
            var accept = party.Request(PartyAction.Accept, out uint id, inviteId: 4);
            Assert.That(party.Invite(2_100), Is.Null);
            Assert.That(ReliableMessages.TryRead(accept, out PartyRequest request), Is.True);
            Assert.That((request.RequestId, request.Action, request.InviteId), Is.EqualTo((id, PartyAction.Accept, 4u)));
        }

        [Test]
        public void Requests_count_per_connection_and_other_messages_are_left_alone()
        {
            var party = new ClientParty();
            party.Request(PartyAction.Invite, out uint first, targetName: "Bo");
            party.Request(PartyAction.Leave, out uint second);
            Assert.That((first, second), Is.EqualTo((1u, 2u)));
            party.Reset();
            party.Request(PartyAction.Leave, out uint again);
            Assert.That(again, Is.EqualTo(1u));

            var result = ReliableMessages.Write(ReliableMessageId.PartyResult,
                new PartyResult { RequestId = 1, Action = PartyAction.Leave, Reason = PartyReason.PartyNotFound });
            Assert.That(party.Apply(result, 0, out _), Is.True);
            Assert.That(party.LastResult!.Reason, Is.EqualTo(PartyReason.PartyNotFound));
            var job = ReliableMessages.Write(ReliableMessageId.JobResult, new JobResult());
            Assert.That(party.Apply(job, 0, out _), Is.False);
        }

        [TestCase("/invite Bo", ChatCommandKind.Invite, "Bo", "")]
        [TestCase("/INVITE Bo", ChatCommandKind.Invite, "Bo", "")]
        [TestCase("/leave", ChatCommandKind.Leave, "", "")]
        [TestCase("/kick Bo", ChatCommandKind.Kick, "Bo", "")]
        [TestCase("/lead Bo", ChatCommandKind.Lead, "Bo", "")]
        [TestCase("/w Bo hello there", ChatCommandKind.Whisper, "Bo", "hello there")]
        [TestCase("/p on my way", ChatCommandKind.Party, "", "on my way")]
        public void The_player_commands_parse(string line, ChatCommandKind kind, string name, string text)
        {
            Assert.That(ChatCommandParser.TryParse(line, out var command), Is.EqualTo(ChatCommandParse.Command));
            Assert.That((command.Kind, command.Name, command.Text), Is.EqualTo((kind, name, text)));
        }

        [TestCase("/invite")]
        [TestCase("/invite Bo extra")]
        [TestCase("/leave now")]
        [TestCase("/w Bo")]
        [TestCase("/w")]
        [TestCase("/p")]
        public void A_command_with_the_wrong_arguments_is_usage(string line)
        {
            Assert.That(ChatCommandParser.TryParse(line, out _), Is.EqualTo(ChatCommandParse.Usage));
        }

        [TestCase("hello")]
        [TestCase("/help")]
        [TestCase("/give m1.demonAsh 2")]
        [TestCase("/whisper Bo hi")]
        [TestCase("/")]
        public void Other_lines_are_sent_as_typed(string line)
        {
            Assert.That(ChatCommandParser.TryParse(line, out _), Is.EqualTo(ChatCommandParse.NotACommand));
        }
    }
}
