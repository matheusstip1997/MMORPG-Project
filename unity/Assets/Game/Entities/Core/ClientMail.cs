#nullable enable
using System;
using Shared.Protocol;

namespace Game.Gameplay.Entities
{
    /// <summary>
    /// The client's side of system mail (spec 4.5 M4 mail, 9.3, 9.13; WP-M4-3), engine-free so the bots use it too: the
    /// mail requests with their ids (one counter per connection), the last list (<see cref="MailList"/>), the last mail opened
    /// (<see cref="MailContent"/>), the last claim answer (<see cref="MailResult"/>) and the open mail count of the M badge
    /// (<see cref="MailCount"/>). The server owns every mailbox and decides every claim; the client only asks.
    /// </summary>
    public sealed class ClientMail
    {
        public MailList? List { get; private set; }

        public MailContent? Content { get; private set; }

        public MailResult? LastResult { get; private set; }

        /// <summary>The open mails and the mailbox cap; null until the server sent them (at enter world).</summary>
        public MailCount? Count { get; private set; }

        /// <summary>Mail requests sent on this connection; the next one gets this plus one as its id.</summary>
        public uint RequestsSent { get; private set; }

        /// <summary>Counts the messages taken and the resets (the UI redraws on a change).</summary>
        public int Revision { get; private set; }

        /// <summary>Open mails for the badge, 0 before the first count.</summary>
        public int OpenCount => Count?.Open ?? 0;

        /// <summary>At or over the cap: new mail sources are refused while settlements still arrive.</summary>
        public bool IsFull => Count != null && Count.Cap > 0 && Count.Open >= Count.Cap;

        /// <summary>Takes the mail messages of the reliable channel; false for any other message.</summary>
        public bool Apply(ReadOnlySpan<byte> message, out string log)
        {
            log = string.Empty;
            switch (ReliableMessages.PeekId(message))
            {
                case ReliableMessageId.MailList:
                    if (ReliableMessages.TryRead(message, out MailList list))
                    {
                        List = list;
                        Revision++;
                        log = $"mail list {list.RequestId}: {list.Reason}, {list.Mails.Length} of {list.Total}";
                    }
                    return true;
                case ReliableMessageId.MailContent:
                    if (ReliableMessages.TryRead(message, out MailContent content))
                    {
                        Content = content;
                        Revision++;
                        log = $"mail content {content.RequestId}: {content.Reason}, mail {content.Header?.MailId}, {content.Items.Length} items";
                    }
                    return true;
                case ReliableMessageId.MailResult:
                    if (ReliableMessages.TryRead(message, out MailResult result))
                    {
                        LastResult = result;
                        Revision++;
                        log = $"mail claim {result.RequestId}: {result.Reason}, mail {result.MailId}, emptied {result.MailsEmptied}, "
                              + $"gold {result.GoldTaken}, units left {result.ItemsLeft}";
                    }
                    return true;
                case ReliableMessageId.MailCount:
                    if (ReliableMessages.TryRead(message, out MailCount count))
                    {
                        Count = count;
                        Revision++;
                        log = $"mail count {count.Open} of {count.Cap}";
                    }
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Asks for the mailbox list.</summary>
        public byte[] ListRequest(out uint requestId) => OpenRequest(0, out requestId);

        /// <summary>Asks for one mail's content (<paramref name="mailId"/> 0: the list).</summary>
        public byte[] OpenRequest(long mailId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.MailOpenRequest, new MailOpenRequest { RequestId = requestId, MailId = mailId });
        }

        /// <summary>Claims one mail (<paramref name="mailId"/> 0: every open mail, oldest first).</summary>
        public byte[] ClaimRequest(long mailId, out uint requestId)
        {
            requestId = ++RequestsSent;
            return ReliableMessages.Write(ReliableMessageId.MailClaimRequest, new MailClaimRequest { RequestId = requestId, MailId = mailId });
        }

        /// <summary>The list answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public MailList? ListFor(uint requestId) => List != null && List.RequestId == requestId ? List : null;

        /// <summary>The content answering <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public MailContent? ContentFor(uint requestId) => Content != null && Content.RequestId == requestId ? Content : null;

        /// <summary>The claim answer for <paramref name="requestId"/>, or null when it has not come yet.</summary>
        public MailResult? ResultFor(uint requestId) => LastResult != null && LastResult.RequestId == requestId ? LastResult : null;

        /// <summary>A new connection: its request ids start again; the count comes with the enter world.</summary>
        public void Reset()
        {
            List = null;
            Content = null;
            LastResult = null;
            Count = null;
            RequestsSent = 0;
            Revision++;
        }
    }
}
