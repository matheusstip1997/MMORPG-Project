using System.Globalization;
using GameServer.Events;
using GameServer.Persistence;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Simulation.Items;

namespace GameServer.Items.Mail;

/// <summary>How a <see cref="MailService.Send"/> went.</summary>
public enum MailSendStatus : byte
{
    /// <summary>The mail rides the carrier's next save.</summary>
    Queued,
    /// <summary>The operation key was sent before on this world: nothing new (rule 5).</summary>
    Repeated,
    /// <summary>The draft breaks a rule (<see cref="MailRules.Check"/>): a caller bug, nothing queued.</summary>
    Invalid,
    /// <summary>The carrier is not on this world, so no save of it carries the mail.</summary>
    CarrierNotAttached,
}

/// <summary>The outcome of a send; <see cref="Error"/> says why a draft is invalid.</summary>
public readonly record struct MailSendResult(MailSendStatus Status, string? Error = null)
{
    public bool Accepted => Status is MailSendStatus.Queued or MailSendStatus.Repeated;
}

/// <summary>
/// One claim request as the world reads it in phase 1: the mail (0: claim all), <see cref="Blocked"/> in combat or dead, the
/// connection that asked and the operation key (<c>mail:&lt;run&gt;:&lt;entity&gt;:&lt;connection&gt;:&lt;request&gt;</c>);
/// <see cref="AtMailbox"/>: the mailbox the request names passed the NPC gate (seen, offers mail, in reach; WP-M4-14, R-4.5-03).
/// </summary>
public readonly record struct MailClaimAsk(uint RequestId, long MailId, bool Blocked, int Connection, string OpKey, bool AtMailbox);

/// <summary>A claim whose save committed: the answer, the connection that asked and one event per mail it touched.</summary>
public sealed record CommittedMailClaim(MailResult Answer, int Connection, IReadOnlyList<MailClaimed> Claims);

/// <summary>
/// <b>The only writer of mailboxes</b> (spec 4.5 M4 mail, M4 decision 4; WP-M4-3), on the world thread. It keeps every attached
/// character's open mails in memory (loaded at login, the oldest <c>MailSettings.maxListed</c>; refreshed off the tick by the
/// world's <see cref="MailWorker"/>) and the mail writes waiting for a save.
/// <para>
/// <b>Send</b> (<see cref="Send"/>): another system writes a mail inside its own operation. The mail rides the save of the
/// <b>carrier</b>, the character whose operation produced the attachments (the buyer of a listing, the winner of a roll, the
/// crafter of a contract), in the same transaction as that operation's item and gold changes; the caller asks for that save
/// (class 1: <c>InventoryService.RequestClass1Save</c>). The recipient's character row is never written (one writer per
/// character, R-7.1-02), whether the recipient is online or not. Once committed, <c>MailSent</c> goes out and an online
/// recipient's mailbox and badge change. A stand-alone writer (the admin compensation, a world job) uses
/// <see cref="MailWriter"/> in its own transaction instead and posts <c>WorldCommand.MailArrived</c>.
/// </para>
/// <para>
/// <b>Claim</b> (<see cref="Claim"/>): one class 1 operation through <see cref="InventoryService"/> (the only item and gold
/// writer, rule 4; reason <c>MailClaim</c>, an admin compensation <c>Admin</c>): the gold first when it fits, then each
/// attachment as much as fits (a stack in part), the rest staying in the mail; nothing fits: <c>BagFull</c>. The mail rows
/// change in the same save as the bag, guarded by the values the world saw, and the player is answered only after the commit
/// (rule 5). A claim is refused away from a mailbox (<c>MailboxNotInReach</c>, R-4.5-03, M4 decision 6a, WP-M4-14: the list and
/// a mail are read anywhere, the attachments are taken at a mailbox), at the mail's expiry (<c>MailExpired</c>) and in combat.
/// </para>
/// </summary>
public sealed class MailService : IMailBatchSource
{
    // TODO(fixer round): SeenAnswers, RememberedClosed and RememberedSends are memory sizes, not balance; move them to
    // MailSettings as [Optional] fields if a play test needs them changed (review of PR #171).
    /// <summary>Answers remembered per character, so a repeated request gets its first answer.</summary>
    private const int SeenAnswers = 16;

    /// <summary>Closed mails remembered per character, so a late claim hears why (claimed or expired).</summary>
    private const int RememberedClosed = 64;

    /// <summary>Send keys remembered per carrier, so a repeated send queues nothing.</summary>
    private const int RememberedSends = 256;

    private readonly MailRules _rules;
    private readonly InventoryService _inventory;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<long, Box> _boxes = new();
    private readonly List<RecentSend> _recent = new();

    public MailService(MailRules rules, InventoryService inventory, Func<long> nowMs)
    {
        _rules = rules;
        _inventory = inventory;
        _nowMs = nowMs;
    }

    public MailRules Rules => _rules;

    /// <summary>Marks a character class 2 dirty with its save scheduler (set by the SessionManager).</summary>
    public Action<long>? MarkClass2 { get; set; }

    /// <summary>Asks for a save at the next tick (a claim, class 1; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>A save committed a claim of this character (set by the world: the events and the answer).</summary>
    public Action<long, CommittedMailClaim>? ClaimCommitted { get; set; }

    /// <summary>A save committed a send (set by the world: the event).</summary>
    public Action<MailSent>? SendCommitted { get; set; }

    /// <summary>An attached character's open mail count changed (set by the world: the M badge).</summary>
    public Action<long>? CountChanged { get; set; }

    /// <summary>An attached character's mailbox needs a fresh read (set by the world: the mail worker).</summary>
    public Action<long>? ReloadWanted { get; set; }

    /// <summary>A save of this character carrying mail writes committed (set by the world: a refused reload is asked again).</summary>
    public Action<long>? Settled { get; set; }

    public bool IsAttached(long characterId) => _boxes.ContainsKey(characterId);

    /// <summary>Starts holding a character's mailbox as read at login, with the sends committed for it while it was loading.</summary>
    public void Attach(long characterId, MailRecord record)
    {
        var box = new Box();
        box.Load(record);
        PruneRecent();
        if (record.Open.Count == record.OpenCount)
        {
            // A complete read: a committed send it does not hold was written after the read.
            foreach (var recent in _recent)
            {
                if (recent.Recipient == characterId && box.IndexOf(recent.Row.MailId) < 0)
                {
                    box.Insert(recent.Row);
                    box.OpenCount++;
                }
            }
        }
        _recent.RemoveAll(r => r.Recipient == characterId);
        _boxes[characterId] = box;
    }

    public void Detach(long characterId) => _boxes.Remove(characterId);

    /// <summary>The open mails the badge counts: every open mail but those past their expiry (the job closes them later).</summary>
    public int OpenCount(long characterId)
    {
        if (!_boxes.TryGetValue(characterId, out var box))
            return 0;
        long now = _nowMs();
        int expired = 0;
        foreach (var mail in box.Open)
        {
            if (now >= mail.ExpiresUtcMs)
                expired++;
        }
        return Math.Max(0, box.OpenCount - expired);
    }

    /// <summary>
    /// The mailbox is below <c>MailSettings.mailboxCap</c>: new mail sources may start (a listing, a stall, a contract proposal;
    /// user answer 2026-11-09). Settlement mails never ask: they always arrive. False for a character not on this world (ask
    /// <see cref="MailRepository.CountOpenAsync"/>).
    /// </summary>
    public bool HasRoom(long characterId) => _boxes.ContainsKey(characterId) && OpenCount(characterId) < _rules.Settings.MailboxCap;

    /// <summary>The loaded open mails, oldest first, without those past their expiry.</summary>
    public IReadOnlyList<MailRow> Mailbox(long characterId)
    {
        if (!_boxes.TryGetValue(characterId, out var box))
            return Array.Empty<MailRow>();
        long now = _nowMs();
        return box.Open.Where(m => now < m.ExpiresUtcMs).ToList();
    }

    /// <summary>One loaded open mail, or null.</summary>
    public MailRow? Find(long characterId, long mailId) =>
        _boxes.TryGetValue(characterId, out var box) && box.IndexOf(mailId) is var at and >= 0 ? box.Open[at] : null;

    /// <summary>Every open mail is loaded (a list can be answered from memory).</summary>
    public bool IsComplete(long characterId) => _boxes.TryGetValue(characterId, out var box) && box.Open.Count >= box.OpenCount;

    /// <summary>The mailbox's change counter: a reload read under one version applies only if nothing changed since.</summary>
    public int Version(long characterId) => _boxes.TryGetValue(characterId, out var box) ? box.Version : -1;

    /// <summary>
    /// Mail writes of the character wait for a save or ride one in flight: a reload read now may predate them, so
    /// <see cref="ApplyReload"/> refuses it (the world asks again after <see cref="Settled"/>).
    /// </summary>
    public bool IsSaving(long characterId) =>
        _boxes.TryGetValue(characterId, out var box) && (box.Claims.Count > 0 || box.Sends.Count > 0 || box.InFlight != null);

    /// <summary>
    /// Queues a system mail on the save of <paramref name="carrierCharacterId"/> (see the class remarks): the mail is written in
    /// the same transaction as the carrier's pending item and gold changes, so the caller makes the attachments leave their
    /// owner (through <see cref="InventoryService"/> or escrow) in the same operation and then asks for the carrier's save. A
    /// repeated operation key queues nothing (<see cref="MailSendStatus.Repeated"/>); the database's unique key is the backstop.
    /// Settlement mails are never refused for a full mailbox (<see cref="HasRoom"/> gates new sources instead).
    /// </summary>
    /// <summary>
    /// The draft would be queued by <see cref="Send"/> on <paramref name="carrierCharacterId"/>'s save (the rules, an attached
    /// carrier, a key not sent before); nothing changes. A caller that must not change items for a mail that is refused asks
    /// first (a loot bag take, WP-M4-9).
    /// </summary>
    public bool CanSend(long carrierCharacterId, MailDraft draft) =>
        _rules.Check(draft) == null && _boxes.TryGetValue(carrierCharacterId, out var carrier) && !carrier.SentKeys.Contains(draft.OpKey);

    public MailSendResult Send(long carrierCharacterId, MailDraft draft)
    {
        if (_rules.Check(draft) is { } error)
            return new MailSendResult(MailSendStatus.Invalid, error);
        if (!_boxes.TryGetValue(carrierCharacterId, out var carrier))
            return new MailSendResult(MailSendStatus.CarrierNotAttached);
        if (carrier.SentKeys.Contains(draft.OpKey))
            return new MailSendResult(MailSendStatus.Repeated);
        long now = _nowMs();
        carrier.Sends.Add(new MailSendWrite(draft, now, _rules.ExpiresAt(now)));
        carrier.RememberSend(draft.OpKey);
        MarkClass2?.Invoke(carrierCharacterId);
        return new MailSendResult(MailSendStatus.Queued);
    }

    /// <summary>
    /// One claim request (phase 1). Returns the answer to send now (a refusal, or the first answer of a repeated request), or
    /// null when the claim was applied and waits for its save (answered through <see cref="ClaimCommitted"/>).
    /// </summary>
    public MailResult? Claim(long characterId, in MailClaimAsk ask)
    {
        if (!_boxes.TryGetValue(characterId, out var box) || !_inventory.IsAttached(characterId))
            return Refusal(ask, MailReason.InventoryUnavailable);
        if (box.Seen(ask.Connection, ask.RequestId) is { } seen)
            return seen;
        if (box.Waiting(ask.Connection, ask.RequestId))
            return null; // the same request while its save runs changes nothing; the answer comes after the commit
        if (box.ClaimKeys.Contains(ask.OpKey))
            return Refusal(ask, MailReason.MailAlreadyClaimed); // a request id reused past the remembered answers
        if (!ask.AtMailbox)
            return Refusal(ask, MailReason.MailboxNotInReach); // R-4.5-03 (M4 decision 6a): taken at a mailbox, read anywhere
        if (ask.Blocked)
            return Refusal(ask, MailReason.InCombat);

        long now = _nowMs();
        var mails = new List<MailRow>();
        if (ask.MailId == 0)
        {
            foreach (var mail in box.Open)
            {
                if (mails.Count == _rules.Settings.ClaimAllMax)
                    break;
                if (now < mail.ExpiresUtcMs)
                    mails.Add(mail);
            }
            if (mails.Count == 0)
                return Refusal(ask, MailReason.MailNotFound);
        }
        else
        {
            int at = box.IndexOf(ask.MailId);
            if (at < 0)
                return Refusal(ask, box.ClosedReason(ask.MailId));
            if (now >= box.Open[at].ExpiresUtcMs)
                return Refusal(ask, MailReason.MailExpired);
            mails.Add(box.Open[at]);
        }

        var claim = new PendingClaim(ask.Connection, new MailResult { RequestId = ask.RequestId, MailId = ask.MailId });
        var taken = new List<(ushort NetId, byte Rarity, bool Stolen, int Count)>();
        bool goldBlocked = false;
        foreach (var mail in mails)
            ClaimOne(characterId, box, ask.OpKey, mail, now, claim, taken, ref goldBlocked);

        if (claim.Writes.Count == 0)
        {
            bool onlyGold = mails.Count == 1 && mails[0].Items.Count == 0;
            return Refusal(ask, onlyGold && goldBlocked ? MailReason.GoldCapReached : MailReason.BagFull);
        }
        claim.Answer.Taken = taken.Take(ReliableMessages.MaxMailTaken)
            .Select(t => new MailAttachmentEntry { ItemNetId = t.NetId, Rarity = t.Rarity, Stolen = t.Stolen, Count = (ushort)Math.Min(t.Count, ushort.MaxValue) })
            .ToArray();
        box.ClaimKeys.Add(ask.OpKey);
        box.Claims.Add(claim);
        box.Version++;
        if (claim.Answer.MailsEmptied > 0)
            CountChanged?.Invoke(characterId);
        RequestSave?.Invoke(characterId, SaveReason.MailClaim);
        return null;
    }

    /// <summary>
    /// A fresh read of the mailbox (the mail worker, off the tick) issued at <paramref name="version"/>: applied when nothing
    /// changed since and no claim waits for its save (otherwise the read may predate it). False when not applied.
    /// </summary>
    public bool ApplyReload(long characterId, MailRecord record, int version)
    {
        if (!_boxes.TryGetValue(characterId, out var box) || box.Version != version || box.Claims.Count > 0 || box.InFlight != null)
            return false;
        int before = OpenCount(characterId);
        box.Load(record);
        box.Version++;
        if (OpenCount(characterId) != before)
            CountChanged?.Invoke(characterId);
        return true;
    }

    /// <summary>The expiry job closed a mail (its attachments are destroyed): an attached recipient's mailbox drops it.</summary>
    public void ApplyExpired(MailExpiredRow row)
    {
        if (!_boxes.TryGetValue(row.RecipientId, out var box))
            return;
        int at = box.IndexOf(row.MailId);
        if (at >= 0)
            box.Open.RemoveAt(at);
        box.OpenCount = Math.Max(box.Open.Count, box.OpenCount - 1);
        box.Close(row.MailId, MailReason.MailExpired);
        box.Version++;
        CountChanged?.Invoke(row.RecipientId);
    }

    /// <summary>The units of the attachments as event counts (item net ids; an item the tables lost is left out).</summary>
    public IReadOnlyList<MailItemCount> Counts(IEnumerable<MailItemRow> items)
    {
        var counts = new List<MailItemCount>();
        foreach (var item in items)
        {
            if (_rules.Items.TryGetItem(item.DefId, out var def))
                counts.Add(new MailItemCount((ushort)def.NetId, item.Rarity, item.Count));
        }
        return counts;
    }

    public MailBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_boxes.TryGetValue(characterId, out var box) || (box.Sends.Count == 0 && box.Claims.Count == 0))
            return null;
        if (box.InFlight != null)
            throw new InvalidOperationException($"character {characterId} already has a mail batch in flight");
        box.InFlight = new InFlightBatch(saveId, box.Sends.ToList(), box.Claims.ToList());
        box.Sends.Clear();
        box.Claims.Clear();
        return new MailBatch(box.InFlight.Sends, box.InFlight.Claims.SelectMany(c => c.Writes).ToList());
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_boxes.TryGetValue(characterId, out var box) || box.InFlight is not { } inFlight || inFlight.SaveId != saveId)
            return; // a save without a batch, or a stale completion: idempotent
        box.InFlight = null;
        foreach (var send in inFlight.Sends)
            OnSendCommitted(characterId, send);
        if (inFlight.Claims.Count > 0)
            box.Version++;
        foreach (var claim in inFlight.Claims)
        {
            box.Remember(claim.Connection, claim.Answer);
            ClaimCommitted?.Invoke(characterId, new CommittedMailClaim(claim.Answer, claim.Connection, claim.Events));
        }
        Settled?.Invoke(characterId);
    }

    /// <summary>The operation key of a claim request (one per run, entity, connection and request id).</summary>
    public static string OperationKey(long runId, uint entityId, int connection, uint requestId) =>
        string.Create(CultureInfo.InvariantCulture, $"mail:{runId}:{entityId}:{connection}:{requestId}");

    private void ClaimOne(long characterId, Box box, string opKey, MailRow mail, long now, PendingClaim claim,
        List<(ushort NetId, byte Rarity, bool Stolen, int Count)> taken, ref bool goldBlocked)
    {
        var reason = mail.Kind == MailKind.AdminCompensation ? InventoryReason.Admin : InventoryReason.MailClaim;
        string source = string.Create(CultureInfo.InvariantCulture, $"mail:{mail.MailId}");
        string key = string.Create(CultureInfo.InvariantCulture, $"{opKey}:{mail.MailId}");
        long goldTaken = 0;
        if (mail.Gold > 0)
        {
            if (_inventory.GoldFits(characterId, mail.Gold))
            {
                if (!_inventory.TryAddGold(characterId, mail.Gold, key + ":g", reason, source).Applied)
                    throw new InvalidOperationException("gold that fits was refused");
                goldTaken = mail.Gold;
            }
            else
                goldBlocked = true;
        }
        var takes = new List<MailItemTake>();
        var takenRows = new List<MailItemRow>();
        var left = new List<MailItemRow>();
        foreach (var item in mail.Items)
        {
            int got = TakeItem(characterId, item, $"{key}:i{item.Index}", reason, source);
            if (got > 0)
            {
                takes.Add(new MailItemTake(item.Index, item.Count, got));
                takenRows.Add(item with { Count = got });
                if (_rules.Items.TryGetItem(item.DefId, out var def))
                    AddTaken(taken, (ushort)def.NetId, item.Rarity, item.Stolen, got);
            }
            if (got < item.Count)
                left.Add(item with { Count = item.Count - got });
        }
        long goldLeft = mail.Gold - goldTaken;
        foreach (var item in left)
            claim.Answer.ItemsLeft = (ushort)Math.Min(ushort.MaxValue, claim.Answer.ItemsLeft + item.Count);
        claim.Answer.GoldLeft += goldLeft;
        if (goldTaken == 0 && takes.Count == 0)
            return;

        bool emptied = left.Count == 0 && goldLeft == 0;
        claim.Writes.Add(new MailClaimWrite(key, characterId, mail.MailId, mail.Gold, goldTaken, takes, takenRows, emptied, now));
        claim.Events.Add(new MailClaimed(characterId, mail.MailId, mail.Kind, Counts(takenRows), goldTaken, emptied, key));
        claim.Answer.GoldTaken += goldTaken;
        int at = box.IndexOf(mail.MailId);
        if (emptied)
        {
            box.Open.RemoveAt(at);
            box.OpenCount = Math.Max(box.Open.Count, box.OpenCount - 1);
            box.Close(mail.MailId, MailReason.MailAlreadyClaimed);
            claim.Answer.MailsEmptied++;
        }
        else
            box.Open[at] = mail with { Gold = goldLeft, Items = left };
    }

    /// <summary>Units of one attachment the bag takes now (a stack in part, an instance whole), applied under its own key.</summary>
    private int TakeItem(long characterId, MailItemRow item, string key, InventoryReason reason, string source)
    {
        if (!_rules.Items.TryGetItem(item.DefId, out var def))
            return 0; // an item the tables lost stays in the mail (it expires with it)
        if (ItemCategories.IsInstance(def.Category))
        {
            ItemProperties? properties = null;
            if (item.PropertiesJson != null
                && (!ItemProperties.TryParse(item.PropertiesJson, out properties, out _) || !_rules.Items.Represents(properties)))
                return 0;
            return _inventory.TryAddInstance(characterId, def, item.Rarity, item.InstanceId, item.Stolen, item.NonTradable,
                properties, key, reason, source).Added;
        }
        int space = Math.Min(item.Count, _inventory.SpaceFor(characterId, def, item.Rarity, item.Stolen, item.StolenFrom, item.StolenAtMs));
        if (space <= 0)
            return 0;
        int added = _inventory.TryAdd(characterId, def, item.Rarity, space, key, reason, source, null, item.Stolen, item.StolenFrom,
            item.StolenAtMs).Added;
        if (added != space)
            throw new InvalidOperationException($"{item.DefId}: {space} units fit but {added} were added");
        return added;
    }

    private static void AddTaken(List<(ushort NetId, byte Rarity, bool Stolen, int Count)> taken, ushort netId, byte rarity, bool stolen,
        int count)
    {
        for (int i = 0; i < taken.Count; i++)
        {
            if (taken[i].NetId == netId && taken[i].Rarity == rarity && taken[i].Stolen == stolen)
            {
                taken[i] = taken[i] with { Count = taken[i].Count + count };
                return;
            }
        }
        taken.Add((netId, rarity, stolen, count));
    }

    private void OnSendCommitted(long carrierId, MailSendWrite send)
    {
        var draft = send.Draft;
        var items = draft.Items.Select((a, i) => new MailItemRow(i, a.DefId, a.Rarity, a.Count, a.InstanceId, a.Stolen, a.NonTradable,
            a.PropertiesJson, a.StolenFrom, a.StolenAtMs)).ToList();
        SendCommitted?.Invoke(new MailSent(draft.ToCharacterId, send.MailId, draft.Kind, Counts(items), draft.Gold, carrierId, draft.OpKey));
        var row = new MailRow(send.MailId, draft.ToCharacterId, draft.Kind, draft.SubjectId, draft.Args, draft.Gold, send.CreatedUtcMs,
            send.ExpiresUtcMs, items);
        if (!_boxes.TryGetValue(draft.ToCharacterId, out var recipient))
        {
            if (send.MailId > 0)
            {
                PruneRecent();
                _recent.Add(new RecentSend(draft.ToCharacterId, row, _nowMs()));
            }
            return;
        }
        if (send.MailId <= 0 || recipient.Open.Count < recipient.OpenCount)
        {
            // The id is unknown (a retried save that had committed) or the box is not fully loaded: read it again.
            ReloadWanted?.Invoke(draft.ToCharacterId);
            return;
        }
        if (recipient.IndexOf(send.MailId) >= 0)
            return; // the login read it already
        recipient.Insert(row);
        recipient.OpenCount++;
        recipient.Version++;
        CountChanged?.Invoke(draft.ToCharacterId);
    }

    private void PruneRecent()
    {
        long oldest = _nowMs() - _rules.Settings.RecentSendsKeepMs;
        _recent.RemoveAll(r => r.AtMs < oldest);
    }

    private static MailResult Refusal(in MailClaimAsk ask, MailReason reason) =>
        new() { RequestId = ask.RequestId, MailId = ask.MailId, Reason = reason };

    private sealed record RecentSend(long Recipient, MailRow Row, long AtMs);

    private sealed class PendingClaim(int connection, MailResult answer)
    {
        public int Connection { get; } = connection;
        public MailResult Answer { get; } = answer;
        public List<MailClaimWrite> Writes { get; } = new();
        public List<MailClaimed> Events { get; } = new();
    }

    private sealed record InFlightBatch(Guid SaveId, List<MailSendWrite> Sends, List<PendingClaim> Claims);

    private sealed class Box
    {
        private readonly Queue<(int Connection, MailResult Answer)> _seen = new();
        private readonly Queue<long> _closedOrder = new();
        private readonly Dictionary<long, MailReason> _closed = new();
        private readonly Queue<string> _sentOrder = new();

        /// <summary>The loaded open mails, oldest first (created, then id).</summary>
        public List<MailRow> Open { get; } = new();

        /// <summary>Every open mail in the database (the loaded ones and those past the read's limit).</summary>
        public int OpenCount { get; set; }

        public int Version { get; set; }
        public List<MailSendWrite> Sends { get; } = new();
        public List<PendingClaim> Claims { get; } = new();
        public InFlightBatch? InFlight { get; set; }
        public HashSet<string> SentKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Claim keys applied since the character was attached (its connection and request id are in them).</summary>
        public HashSet<string> ClaimKeys { get; } = new(StringComparer.Ordinal);

        public void Load(MailRecord record)
        {
            Open.Clear();
            foreach (var mail in record.Open)
                Insert(mail);
            OpenCount = Math.Max(record.OpenCount, Open.Count);
        }

        public int IndexOf(long mailId)
        {
            for (int i = 0; i < Open.Count; i++)
            {
                if (Open[i].MailId == mailId)
                    return i;
            }
            return -1;
        }

        public void Insert(MailRow mail)
        {
            int at = Open.Count;
            while (at > 0 && (Open[at - 1].CreatedUtcMs > mail.CreatedUtcMs
                              || (Open[at - 1].CreatedUtcMs == mail.CreatedUtcMs && Open[at - 1].MailId > mail.MailId)))
                at--;
            Open.Insert(at, mail);
        }

        public void Close(long mailId, MailReason reason)
        {
            if (_closed.ContainsKey(mailId))
                return;
            if (_closedOrder.Count == RememberedClosed)
                _closed.Remove(_closedOrder.Dequeue());
            _closedOrder.Enqueue(mailId);
            _closed[mailId] = reason;
        }

        public MailReason ClosedReason(long mailId) => _closed.TryGetValue(mailId, out var reason) ? reason : MailReason.MailNotFound;

        public void RememberSend(string opKey)
        {
            if (_sentOrder.Count == RememberedSends)
                SentKeys.Remove(_sentOrder.Dequeue());
            _sentOrder.Enqueue(opKey);
            SentKeys.Add(opKey);
        }

        public bool Waiting(int connection, uint requestId)
        {
            foreach (var claim in Claims)
            {
                if (claim.Connection == connection && claim.Answer.RequestId == requestId)
                    return true;
            }
            if (InFlight != null)
            {
                foreach (var claim in InFlight.Claims)
                {
                    if (claim.Connection == connection && claim.Answer.RequestId == requestId)
                        return true;
                }
            }
            return false;
        }

        public MailResult? Seen(int connection, uint requestId)
        {
            foreach (var (seenConnection, answer) in _seen)
            {
                if (seenConnection == connection && answer.RequestId == requestId)
                    return answer;
            }
            return null;
        }

        public void Remember(int connection, MailResult answer)
        {
            if (_seen.Count == SeenAnswers)
                _seen.Dequeue();
            _seen.Enqueue((connection, answer));
        }
    }
}
