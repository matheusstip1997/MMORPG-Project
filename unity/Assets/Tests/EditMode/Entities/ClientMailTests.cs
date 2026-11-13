#nullable enable
using Game.Gameplay.Entities;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode.Entities
{
    /// <summary>
    /// WP-M4-3 on the client: the mail messages read in Unity's build of Game.Shared (MemoryPack); <see cref="ClientMail"/> keeps
    /// the last list, content, claim answer and count, numbers its requests per connection, and leaves other messages to the
    /// other readers.
    /// </summary>
    public class ClientMailTests
    {
        private static MailHeader Header(long id) => new MailHeader
        {
            MailId = id, Kind = MailKind.ListingProceeds, SubjectId = "mail.subject.listingProceeds", Gold = 50,
            Items = new[] { new MailAttachmentEntry { ItemNetId = 2, Count = 3 } },
        };

        [Test]
        public void Each_mail_message_is_kept_and_answers_are_found_by_request_id()
        {
            var mail = new ClientMail();

            Assert.That(mail.Apply(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount { Open = 2, Cap = 50 }), out _), Is.True);
            Assert.That(mail.Apply(ReliableMessages.Write(ReliableMessageId.MailList,
                new MailList { RequestId = 1, Total = 1, Mails = new[] { Header(7) } }), out _), Is.True);
            Assert.That(mail.Apply(ReliableMessages.Write(ReliableMessageId.MailContent,
                new MailContent { RequestId = 2, Header = Header(7) }), out _), Is.True);
            Assert.That(mail.Apply(ReliableMessages.Write(ReliableMessageId.MailResult,
                new MailResult { RequestId = 3, MailId = 7, MailsEmptied = 1, GoldTaken = 50 }), out _), Is.True);

            Assert.That((mail.OpenCount, mail.IsFull), Is.EqualTo((2, false)));
            Assert.That(mail.ListFor(1)!.Mails[0].MailId, Is.EqualTo(7));
            Assert.That(mail.ContentFor(2)!.Header.Gold, Is.EqualTo(50));
            Assert.That(mail.ResultFor(3)!.GoldTaken, Is.EqualTo(50));
            Assert.That(mail.ResultFor(4), Is.Null);
            Assert.That(mail.Apply(ReliableMessages.Write(ReliableMessageId.GemResult, new GemResult()), out _), Is.False);
        }

        [Test]
        public void Requests_are_numbered_per_connection_and_a_reset_forgets_everything()
        {
            var mail = new ClientMail();
            Assert.That(ReliableMessages.TryRead(mail.ListRequest(out uint first), out MailOpenRequest list), Is.True);
            Assert.That(ReliableMessages.TryRead(mail.ClaimRequest(0, 9, out uint second), out MailClaimRequest claim), Is.True);
            Assert.That((first, list.MailId, second, claim.MailId, claim.NpcEntityId), Is.EqualTo((1u, 0L, 2u, 0L, 9u))); // WP-M4-14: at mailbox 9
            mail.Apply(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount { Open = 50, Cap = 50 }), out _);
            Assert.That(mail.IsFull, Is.True);

            mail.Reset();

            Assert.That((mail.RequestsSent, mail.Count, mail.OpenCount), Is.EqualTo((0u, (MailCount?)null, 0)));
        }
    }
}
