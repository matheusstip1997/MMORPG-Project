using Shared.Protocol;

namespace Shared.Tests.Protocol;

/// <summary>
/// The mail messages (spec 4.5, 9.3; WP-M4-3, protocol 42): each round trips through its checked reader (the layouts of
/// <see cref="ReliableMessageReader"/>, R-2.5-07), and a reader refuses what the protocol never sends: free text as a subject,
/// an unknown kind, more attachments or arguments than a mail carries, a negative mail id or gold.
/// </summary>
public class MailMessagesTests
{
    private static MailHeader Header(Action<MailHeader> change)
    {
        var header = Header();
        change(header);
        return header;
    }

    private static MailHeader Header() => new()
    {
        MailId = 7, Kind = MailKind.ListingProceeds, SubjectId = "mail.subject.listingProceeds",
        Args = [new MailArgEntry { Name = "item", Kind = MailArgKind.Item, Value = 113 }], Gold = 90, CreatedUtcMs = 1, ExpiresUtcMs = 2,
        Items = [new MailAttachmentEntry { ItemNetId = 2, Rarity = 1, Count = 5 }],
    };

    [Fact]
    public void Every_mail_message_round_trips_through_its_reader()
    {
        var list = ReliableMessages.Write(ReliableMessageId.MailList, new MailList { RequestId = 3, Total = 4, Mails = [Header()] });
        Assert.True(ReliableMessages.TryRead(list, out MailList readList));
        Assert.Equal(("mail.subject.listingProceeds", (ushort)4, 90L), (readList.Mails[0].SubjectId, readList.Total, readList.Mails[0].Gold));

        var content = ReliableMessages.Write(ReliableMessageId.MailContent, new MailContent
        {
            RequestId = 4, Header = Header(),
            Items = [new InventorySlotEntry { Slot = 0, ItemNetId = 101, Count = 1, InstanceId = 9, Enchant = 2 }],
        });
        Assert.True(ReliableMessages.TryRead(content, out MailContent readContent));
        Assert.Equal((byte)2, readContent.Items[0].Enchant);

        var result = ReliableMessages.Write(ReliableMessageId.MailResult, new MailResult
        {
            RequestId = 5, MailId = 7, MailsEmptied = 1, Taken = [new MailAttachmentEntry { ItemNetId = 2, Count = 5 }], GoldTaken = 90,
        });
        Assert.True(ReliableMessages.TryRead(result, out MailResult readResult));
        Assert.Equal((90L, (ushort)1), (readResult.GoldTaken, readResult.MailsEmptied));

        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailCount, new MailCount { Open = 3, Cap = 50 }),
            out MailCount count));
        Assert.Equal(((ushort)3, (ushort)50), (count.Open, count.Cap));
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailOpenRequest, new MailOpenRequest { RequestId = 1 }),
            out MailOpenRequest open));
        Assert.Equal(0, open.MailId);
        Assert.True(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailClaimRequest, new MailClaimRequest { RequestId = 1, MailId = 7, NpcEntityId = 12 }),
            out MailClaimRequest claim));
        Assert.Equal((7L, 12u), (claim.MailId, claim.NpcEntityId)); // WP-M4-14: the mailbox the claim is made at
    }

    [Fact]
    public void A_reader_refuses_what_the_protocol_never_sends()
    {
        Assert.False(Reads(Header(h => h.SubjectId = "Your sale earned 90 gold"))); // free text: spaces
        Assert.False(Reads(Header(h => h.SubjectId = "")));
        Assert.False(Reads(Header(h => h.Kind = MailKind.None)));
        Assert.False(Reads(Header(h => h.Kind = (MailKind)99)));
        Assert.False(Reads(Header(h => h.Gold = -1)));
        Assert.False(Reads(Header(h => h.MailId = 0)));
        Assert.False(Reads(Header(h => h.Args = Enumerable.Repeat(new MailArgEntry { Name = "n", Kind = MailArgKind.Number }, 4).ToArray())));
        Assert.False(Reads(Header(h => h.Args = [new MailArgEntry { Name = "n", Kind = MailArgKind.None }])));
        Assert.False(Reads(Header(h => h.Items = Enumerable.Repeat(new MailAttachmentEntry { ItemNetId = 1, Count = 1 }, 9).ToArray())));
        Assert.False(Reads(Header(h => h.Items = [new MailAttachmentEntry { ItemNetId = 0, Count = 1 }])));
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailClaimRequest,
            new MailClaimRequest { RequestId = 1, MailId = -1 }), out MailClaimRequest _));
        Assert.False(ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailList,
            new MailList { Total = 0, Mails = [Header()] }), out MailList _)); // more mails than the total
    }

    [Fact]
    public void The_client_requests_fit_their_caps_rows()
    {
        var specs = ReliableMessageReader.All;
        Assert.Equal(14, ReliableMessages.Write(ReliableMessageId.MailOpenRequest, new MailOpenRequest { RequestId = uint.MaxValue, MailId = long.MaxValue }).Length);
        Assert.Equal(18, ReliableMessages.Write(ReliableMessageId.MailClaimRequest, new MailClaimRequest { RequestId = uint.MaxValue, MailId = long.MaxValue, NpcEntityId = uint.MaxValue }).Length);
        Assert.Contains(specs, s => s.Id == ReliableMessageId.MailOpenRequest && s.Direction == ReliableDirection.ClientToServer);
        Assert.Contains(specs, s => s.Id == ReliableMessageId.MailCount && s.Direction == ReliableDirection.ServerToClient);
    }

    private static bool Reads(MailHeader header) =>
        ReliableMessages.TryRead(ReliableMessages.Write(ReliableMessageId.MailList, new MailList { Total = 1, Mails = [header] }), out MailList _);
}
