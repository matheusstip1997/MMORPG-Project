using System.Data.Common;
using GameServer.Persistence;
using Shared.Protocol;

namespace GameServer.Items.Mail;

/// <summary>
/// The mail side of an escrow release (spec 4.5 M4 mail; WP-M4-4's <see cref="IEscrowMailSink"/>, wired by WP-M4-3): the
/// released items and gold become one system mail through <see cref="MailWriter"/>, <b>inside the escrow's own transaction</b>
/// (<see cref="EscrowRepository.ReleaseToMailAsync"/>): the escrow row is marked released and the mail is written together,
/// keyed by the release's operation key, so a release becomes a mail exactly once. The kind is the caller's
/// (<see cref="MailKind"/>: a listing's return or proceeds, a contract's return); the subject is the kind's. A draft the tables
/// refuse throws, the transaction rolls back and the escrow stays held (nothing is lost).
/// </summary>
public sealed class EscrowMailSink(MailRules rules) : IEscrowMailSink
{
    public async Task WriteAsync(DbConnection connection, DbTransaction transaction, long toCharacterId, short mailKind,
        IReadOnlyList<EscrowItemRow> items, long gold, string opKey, DateTime now, CancellationToken ct)
    {
        var kind = (MailKind)mailKind;
        if (kind == MailKind.None || kind > ReliableMessages.LastMailKind)
            throw new ArgumentOutOfRangeException(nameof(mailKind), $"mail kind {mailKind} is not a MailKind");
        var draft = new MailDraft(toCharacterId, kind, MailSubjects.For(kind), Array.Empty<MailArg>(),
            items.Select(i => new MailAttachment(i.DefId, (byte)i.Rarity, i.Count, i.Stolen, i.NonTradable, i.InstanceId, i.PropertiesJson,
                i.StolenFrom, i.StolenAtMs))
                .ToList(),
            gold, opKey, "escrow:" + opKey);
        if (rules.Check(draft) is { } error)
            throw new InvalidOperationException($"escrow release {opKey} makes no valid mail: {error}");
        long createdUtcMs = DbTime.ToUtcMs(now);
        await MailWriter.SendAsync(connection, transaction, draft, createdUtcMs, rules.ExpiresAt(createdUtcMs), carrierId: null,
            saveId: null, skipDuplicate: false, ct);
    }
}
