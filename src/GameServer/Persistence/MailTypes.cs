using Shared.Protocol;

namespace GameServer.Persistence;

/// <summary>A mail's state (<c>mail.state</c>). Stored as a smallint: append only.</summary>
public enum MailState : short
{
    Open = 0,
    /// <summary>Emptied by its recipient's claim.</summary>
    Claimed = 1,
    /// <summary>Passed its expiry; its attachments were destroyed (a sink).</summary>
    Expired = 2,
}

/// <summary>What a <c>mail_ops</c> row records. Stored as a smallint: append only.</summary>
public enum MailOpKind : short
{
    Send = 1,
    Claim = 2,
    Expire = 3,
}

/// <summary>A typed subject argument (never free text): the client fills <c>{Name}</c> of the subject's <c>UiStrings</c> text.</summary>
public sealed record MailArg(string Name, MailArgKind Kind, long Value);

/// <summary>
/// One attachment of a mail being sent: a stack (<paramref name="InstanceId"/> 0, any count up to the item's stack) or one
/// instance. An instance snapshot that left a bag keeps its id, its flags and its properties (<paramref name="PropertiesJson"/>,
/// <c>item_instances.properties</c>); an instance with id 0 is minted for the recipient at the claim (a ground drop that never
/// was an instance), with <paramref name="PropertiesJson"/> null for the defaults.
/// </summary>
public sealed record MailAttachment(string DefId, byte Rarity, int Count, bool Stolen = false, bool NonTradable = false,
    long InstanceId = 0, string? PropertiesJson = null, long StolenFrom = 0, long StolenAtMs = 0);

/// <summary>
/// A system mail to write (spec 4.5 M4 mail; WP-M4-3). <paramref name="OpKey"/> is the operation that produced the attachments
/// (one mail per key, the idempotency key of the send); <paramref name="SubjectId"/> a <c>UiStrings</c> id
/// (<c>Items/Mail/MailSubjects</c>). The caller is responsible for where the attachments came from (rule 4): removed from a
/// character through <c>InventoryService</c> or from escrow in the same transaction, or a support grant (the claim is then
/// the faucet, reason <c>Admin</c>).
/// </summary>
public sealed record MailDraft(long ToCharacterId, MailKind Kind, string SubjectId, IReadOnlyList<MailArg> Args,
    IReadOnlyList<MailAttachment> Items, long Gold, string OpKey, string Source);

/// <summary>One attachment of a stored mail (<c>mail_items</c>): what is still in it.</summary>
/// <remarks>A stolen stack keeps its mark (<paramref name="StolenFrom"/>, <paramref name="StolenAtMs"/>; WP-M4-9, 0 for none).</remarks>
public sealed record MailItemRow(int Index, string DefId, byte Rarity, int Count, long InstanceId, bool Stolen, bool NonTradable,
    string? PropertiesJson, long StolenFrom = 0, long StolenAtMs = 0);

/// <summary>An open mail as stored (<c>mail</c> and its <c>mail_items</c>), times in UTC milliseconds.</summary>
public sealed record MailRow(long MailId, long RecipientId, MailKind Kind, string SubjectId, IReadOnlyList<MailArg> Args, long Gold,
    long CreatedUtcMs, long ExpiresUtcMs, IReadOnlyList<MailItemRow> Items);

/// <summary>What a login (or a reload) reads of a mailbox: the open mails, oldest first, and how many are open.</summary>
public sealed record MailRecord(IReadOnlyList<MailRow> Open, int OpenCount)
{
    public static readonly MailRecord Empty = new(Array.Empty<MailRow>(), 0);
}

/// <summary>
/// A send carried by a character save (<see cref="MailBatch"/>): the draft and its times. <see cref="MailId"/> is set by the
/// write (the persistence worker), read by the world thread after the commit.
/// </summary>
public sealed class MailSendWrite(MailDraft draft, long createdUtcMs, long expiresUtcMs)
{
    public MailDraft Draft { get; } = draft;
    public long CreatedUtcMs { get; } = createdUtcMs;
    public long ExpiresUtcMs { get; } = expiresUtcMs;

    /// <summary>The written row's id; 0 until a write ran (a retry of a committed save does not run it again).</summary>
    public long MailId { get; set; }
}

/// <summary>One attachment a claim touched: its index, the count it held and the units taken.</summary>
public readonly record struct MailItemTake(int Index, int ExpectedCount, int Taken);

/// <summary>
/// A claim of one mail carried by its recipient's save (WP-M4-3): the gold taken out of the gold it held, the units taken of
/// each attachment, and whether it is now empty (then <c>Claimed</c>). The update is guarded by the expected values: a mail
/// changed elsewhere (an expiry past the grace) throws <see cref="MailConflictException"/> and the save writes nothing.
/// </summary>
public sealed record MailClaimWrite(string OpKey, long CharacterId, long MailId, long ExpectedGold, long GoldTaken,
    IReadOnlyList<MailItemTake> Items, IReadOnlyList<MailItemRow> Taken, bool Emptied, long AtUtcMs);

/// <summary>
/// A character's mail writes since its last save (WP-M4-3): sends it carries (to any recipient) and its own claims. Written in
/// the transaction of the character save, after the inventory batch (whose operation keys the claim's gold and audit rows
/// reference), so the mail rows and the bag commit together (rule 5).
/// </summary>
public sealed record MailBatch(IReadOnlyList<MailSendWrite> Sends, IReadOnlyList<MailClaimWrite> Claims)
{
    public bool IsEmpty => Sends.Count == 0 && Claims.Count == 0;
}

/// <summary>
/// Hands a character's pending mail writes to the save being issued and learns how that save ended. Implemented by
/// <c>Items/Mail/MailService</c>; called on the world thread by <see cref="SaveScheduler"/>, after the inventory source.
/// </summary>
public interface IMailBatchSource
{
    /// <summary>The writes not yet in a save, now part of save <paramref name="saveId"/>; null when there are none.</summary>
    MailBatch? TakeBatch(long characterId, Guid saveId);

    /// <summary>Save <paramref name="saveId"/> committed: its writes are in the database.</summary>
    void Committed(long characterId, Guid saveId);

}

/// <summary>A claim's guarded update found the mail changed (claimed or expired elsewhere): the save rolls back.</summary>
public sealed class MailConflictException(long mailId) : Exception($"mail {mailId} changed under a claim")
{
    public long MailId { get; } = mailId;
}

/// <summary>One mail the expiry job closed (<see cref="MailRepository.ExpireDueAsync"/>): what it destroyed.</summary>
public sealed record MailExpiredRow(long MailId, long RecipientId, MailKind Kind, long Gold, IReadOnlyList<MailItemRow> Items);
