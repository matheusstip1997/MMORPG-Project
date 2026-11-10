using GameServer.Entities;
using GameServer.Items;
using GameServer.Items.Mail;
using GameServer.Persistence;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Net;

/// <summary>
/// Phase 10 (spec 4.5, 9.3; WP-M4-3): a player's mail answers in the order they were written (the list, a mail's content, claim
/// results: a claim only once its save committed, before the <c>InventoryContents</c> that shows it), then the open mail count
/// for the M badge when it changed. Reliable channel, MemoryPack.
/// </summary>
internal sealed class MailSender
{
    private readonly ITransport _transport;

    public MailSender(ITransport transport)
    {
        _transport = transport;
    }

    public void Send(Player player, MailService? mail)
    {
        foreach (var message in player.MailMessages)
            _transport.Send(player.PeerId, Channel.ReliableOrdered, message);
        player.MailMessages.Clear();
        if (!player.MailCountDirty || mail == null || !mail.IsAttached(player.CharacterId))
            return;
        player.MailCountDirty = false;
        var count = new MailCount
        {
            Open = (ushort)Math.Min(ushort.MaxValue, mail.OpenCount(player.CharacterId)),
            Cap = (ushort)Math.Min(ushort.MaxValue, mail.Rules.Settings.MailboxCap),
        };
        _transport.Send(player.PeerId, Channel.ReliableOrdered, ReliableMessages.Write(ReliableMessageId.MailCount, count));
    }
}

/// <summary>The wire views of the mailbox (spec 4.5, WP-M4-3): headers for the list, slot entries for a mail's content.</summary>
internal static class MailViews
{
    public static MailList List(MailService mail, long characterId, uint requestId)
    {
        var rows = mail.Mailbox(characterId);
        int listed = Math.Min(rows.Count, mail.Rules.Settings.MaxListed);
        var headers = new MailHeader[listed];
        for (int i = 0; i < listed; i++)
            headers[i] = Header(mail.Rules.Items, rows[i]);
        return new MailList
        {
            RequestId = requestId,
            Total = (ushort)Math.Min(ushort.MaxValue, Math.Max(listed, mail.OpenCount(characterId))),
            Mails = headers,
        };
    }

    public static MailContent Content(MailService mail, long characterId, uint requestId, long mailId, long nowMs)
    {
        var row = mail.Find(characterId, mailId);
        if (row == null)
            return new MailContent { RequestId = requestId, Reason = MailReason.MailNotFound };
        if (nowMs >= row.ExpiresUtcMs)
            return new MailContent { RequestId = requestId, Reason = MailReason.MailExpired };
        var items = mail.Rules.Items;
        var slots = new List<InventorySlotEntry>(row.Items.Count);
        foreach (var item in row.Items)
        {
            if (!items.TryGetItem(item.DefId, out var def))
                continue;
            var entry = new InventorySlotEntry
            {
                Slot = (byte)item.Index,
                ItemNetId = (ushort)def.NetId,
                Rarity = item.Rarity,
                Stolen = item.Stolen,
                Count = (ushort)Math.Min(ushort.MaxValue, item.Count),
                InstanceId = item.InstanceId,
                NonTradable = item.NonTradable,
            };
            if (item.PropertiesJson != null && ItemProperties.TryParse(item.PropertiesJson, out var properties, out _)
                && items.Represents(properties))
                InventorySender.Fill(entry, properties, items);
            slots.Add(entry);
        }
        return new MailContent { RequestId = requestId, Header = Header(items, row), Items = slots.ToArray() };
    }

    private static MailHeader Header(ItemRules items, MailRow row)
    {
        var attachments = new List<MailAttachmentEntry>(row.Items.Count);
        foreach (var item in row.Items)
        {
            if (items.TryGetItem(item.DefId, out var def))
                attachments.Add(new MailAttachmentEntry
                {
                    ItemNetId = (ushort)def.NetId, Rarity = item.Rarity, Stolen = item.Stolen,
                    Count = (ushort)Math.Min(ushort.MaxValue, item.Count),
                });
        }
        return new MailHeader
        {
            MailId = row.MailId,
            Kind = row.Kind,
            SubjectId = row.SubjectId,
            Args = row.Args.Select(a => new MailArgEntry { Name = a.Name, Kind = a.Kind, Value = a.Value }).ToArray(),
            Gold = row.Gold,
            CreatedUtcMs = row.CreatedUtcMs,
            ExpiresUtcMs = row.ExpiresUtcMs,
            Items = attachments.ToArray(),
        };
    }
}
