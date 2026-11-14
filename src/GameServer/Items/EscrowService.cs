using System.Collections.Concurrent;
using GameServer.Events;
using GameServer.Persistence;
using Shared.Util;

namespace GameServer.Items;

/// <summary>Why an escrow request was refused (nothing changed); <see cref="None"/> when it was applied.</summary>
public enum EscrowRefusal : byte
{
    None = 0,
    /// <summary>No such escrow, or not held (still being held, being released, or released).</summary>
    NotHeld = 1,
    /// <summary>The character is not online in this world (a release to a character needs it; else release to mail).</summary>
    NotOnline = 2,
    /// <summary>The items or gold are not there (or reserved by something else), or nothing to hold.</summary>
    Changed = 3,
    /// <summary>The receiver's containers cannot take the items.</summary>
    BagFull = 4,
    /// <summary>The receiver would pass the gold cap.</summary>
    GoldCap = 5,
    /// <summary>The escrow id (the hold's operation key) is in use.</summary>
    Duplicate = 6,
    /// <summary>A release to mail needs the mail service (WP-M4-3).</summary>
    NoMail = 7,
}

/// <summary>The outcome of a hold or release: the operation key to wait on with <c>InventoryService.IsCommitted</c>.</summary>
public readonly record struct EscrowResult(EscrowRefusal Refusal, string? WaitKey)
{
    public bool Applied => Refusal == EscrowRefusal.None;
}

/// <summary>
/// EscrowService (spec 4.5, 5.4, 8.2; WP-M4-4): <b>the only holder of assets outside a character</b>. Listings (WP-M4-7, -8)
/// and contracts (WP-M4-10) put items and gold here; the trade does not need it (both sides online, one transaction).
/// Runs on the world thread; items and gold still change only through <see cref="InventoryService"/> (rule 4).
/// <list type="bullet">
/// <item><see cref="Hold"/>: the units and gold leave the owner (reason <c>EscrowHold</c>, a move) and the <c>escrow</c> and
/// <c>escrow_items</c> rows are written <b>in the owner's character save</b>, asked at once (<c>SaveReason.Escrow</c>), so both
/// commit together (rule 5). The caller answers after the commit (<c>InventoryService.IsCommitted(owner, WaitKey)</c>).</item>
/// <item><see cref="ReleaseToCharacter"/>: an online character receives them (reason <c>EscrowRelease</c>) and the escrow row
/// flips to released in that character's save; the database guard (the row must still be held) is the backstop.</item>
/// <item><see cref="ReleaseToMail"/>: one standalone transaction off the world thread flips the row and writes the mail
/// (<see cref="IEscrowMailSink"/>, WP-M4-3's <c>MailService.Send</c> when merged); a retry after an unknown outcome is
/// recognised by its release key.</item>
/// </list>
/// Open escrows are loaded at world start (<see cref="Load"/>) and kept in memory, so a second release is refused before the
/// database. One world in M4; TODO(M7): only this world's holders when worlds shard.
/// </summary>
public sealed class EscrowService : IEscrowBatchSource
{
    private readonly InventoryService _inventory;
    private readonly IClock _clock;
    private readonly EventBus? _events;
    private readonly Dictionary<string, Escrow> _escrows = new(StringComparer.Ordinal);
    private readonly Dictionary<long, Pending> _pending = new();
    private readonly Dictionary<long, InFlight> _inFlight = new();
    private readonly ConcurrentQueue<(string EscrowId, EscrowReleaseStatus? Status)> _mailDone = new();
    private readonly List<MailRelease> _mailWaiting = new();
    private readonly Queue<MailRelease> _mailQueue = new();
    private Task? _mailTask;
    private bool _stopping;

    public EscrowService(InventoryService inventory, IClock clock, EventBus? events)
    {
        _inventory = inventory;
        _clock = clock;
        _events = events;
    }

    /// <summary>Asks a character's save at once (the scheduler's <c>RequestSave</c>; set by the SessionManager).</summary>
    public Action<long, SaveReason>? RequestSave { get; set; }

    /// <summary>
    /// Releases to mail off the world thread (<see cref="EscrowRepository"/> with WP-M4-3's mail writer as its
    /// <see cref="IEscrowMailSink"/>); null until the mail service is merged: <see cref="ReleaseToMail"/> is then refused.
    /// </summary>
    public IEscrowMailReleaser? MailReleaser { get; set; }

    /// <summary>A release to mail committed for this character (set by the world: an online recipient's mailbox reads again; WP-M4-3).</summary>
    public Action<long>? ReleasedToMail { get; set; }

    /// <summary>Wait before retrying a release to mail whose outcome is unknown (the class 2 save interval; set by the host).</summary>
    public int RetryMs { get; set; }

    private void RequestSaveOf(long characterId) => RequestSave?.Invoke(characterId, SaveReason.Escrow);

    /// <summary>Escrows held now (committed and not released or being released).</summary>
    public int HeldCount => _escrows.Values.Count(e => e.State == State.Held);

    /// <summary>Starts with the escrows still held in the database (world start).</summary>
    public void Load(IEnumerable<EscrowRecord> held)
    {
        foreach (var record in held)
            _escrows[record.EscrowId] = new Escrow(record.OwnerCharacterId, record.HolderKind, record.HolderId, record.Gold,
                record.Items.ToList()) { State = State.Held };
    }

    /// <summary>True once the escrow's hold committed and it was not released (or is being released).</summary>
    public bool IsHeld(string escrowId) => _escrows.TryGetValue(escrowId, out var escrow) && escrow.State == State.Held;

    /// <summary>What an escrow holds (read-only; null when unknown).</summary>
    public EscrowRecord? Find(string escrowId) => _escrows.TryGetValue(escrowId, out var e)
        ? new EscrowRecord(escrowId, e.Owner, e.HolderKind, e.HolderId, e.Gold, e.Items)
        : null;

    /// <summary>
    /// Holds <paramref name="items"/> and <paramref name="gold"/> of an online character in escrow <paramref name="opKey"/> (the
    /// escrow id) for <paramref name="holderKind"/>/<paramref name="holderId"/>. Nothing changes when refused. A repeated key
    /// returns the first wait key. The id must be unique for ever, as every operation key: callers build it with the world's
    /// run id (as <c>trade:{run}:...</c>, <c>sell:{run}:...</c>), so an id never repeats across restarts; within a run a repeat
    /// is refused here or by the owner's operation keys, never at the save.
    /// </summary>
    public EscrowResult Hold(long characterId, IReadOnlyList<ReserveItem> items, long gold, EscrowHolderKind holderKind,
        string holderId, string opKey)
    {
        if (_escrows.TryGetValue(opKey, out var existing))
            return existing.Owner == characterId && existing.HoldKey != null
                ? new EscrowResult(EscrowRefusal.None, existing.HoldKey)
                : new EscrowResult(EscrowRefusal.Duplicate, null);
        if (!_inventory.IsAttached(characterId) || !_inventory.HasGold(characterId))
            return new EscrowResult(EscrowRefusal.NotOnline, null);
        var bags = _inventory.CurrentInventory(characterId);
        foreach (var item in items)
        {
            // R-4.1-04: stolen units never enter a listing, stall or contract; a marked stack (WP-M4-9) would also lose its mark
            // here (escrow_items keeps the flag only), so the backstop refuses it whatever the caller checked.
            if (bags.TryIndex(item.At.Container, out int index) && item.At.Slot >= 0 && item.At.Slot < bags.BagAt(index).SlotCount
                && bags.BagAt(index)[item.At.Slot].StolenFrom != 0)
                return new EscrowResult(EscrowRefusal.Changed, null);
        }
        var held = new List<EscrowItemRow>(items.Count);
        var waitKey = _inventory.TryHoldOut(characterId, items, gold, opKey, InventoryReason.EscrowHold,
            $"escrow:{(short)holderKind}:{holderId}", held, out var refusal);
        if (waitKey == null)
            return new EscrowResult(refusal == TransferRefusal.None ? EscrowRefusal.Duplicate : EscrowRefusal.Changed, null);

        var escrow = new Escrow(characterId, holderKind, holderId, gold, held) { State = State.Holding, HoldKey = waitKey };
        _escrows.Add(opKey, escrow);
        PendingOf(characterId).Holds.Add(new EscrowHoldRow(opKey, characterId, holderKind, holderId, gold, _clock.UtcNowMs, held));
        RequestSaveOf(characterId);
        return new EscrowResult(EscrowRefusal.None, waitKey);
    }

    /// <summary>
    /// Releases a held escrow to an online character (the owner taking back a cancelled listing, a buyer, a contract's
    /// customer): its units and gold are added (all or nothing) and the release rides that character's save.
    /// </summary>
    public EscrowResult ReleaseToCharacter(string escrowId, long toCharacterId, string opKey, int ownerHistoryMax)
    {
        if (!_escrows.TryGetValue(escrowId, out var escrow))
            return new EscrowResult(EscrowRefusal.NotHeld, null);
        if (escrow.ReleaseKey == opKey && escrow.ReleaseWait != null)
            return new EscrowResult(EscrowRefusal.None, escrow.ReleaseWait); // a repeated release: the first answer
        if (escrow.State != State.Held)
            return new EscrowResult(EscrowRefusal.NotHeld, null);
        if (!_inventory.IsAttached(toCharacterId) || !_inventory.HasGold(toCharacterId))
            return new EscrowResult(EscrowRefusal.NotOnline, null);
        var waitKey = _inventory.TryReceive(toCharacterId, escrow.Items, escrow.Gold, opKey, InventoryReason.EscrowRelease,
            $"escrow:{escrowId}", escrow.Owner, ownerHistoryMax, out var refusal);
        if (waitKey == null)
        {
            return new EscrowResult(refusal switch
            {
                TransferRefusal.BagFull => EscrowRefusal.BagFull,
                TransferRefusal.GoldCap => EscrowRefusal.GoldCap,
                TransferRefusal.None => EscrowRefusal.Duplicate,
                _ => EscrowRefusal.Changed,
            }, null);
        }
        escrow.State = State.Releasing;
        escrow.ReleaseKey = opKey;
        escrow.ReleaseWait = waitKey;
        escrow.ReleasedTo = toCharacterId;
        PendingOf(toCharacterId).Releases.Add(new EscrowReleaseRow(escrowId, opKey, EscrowTargetKind.Character, toCharacterId, _clock.UtcNowMs));
        RequestSaveOf(toCharacterId);
        return new EscrowResult(EscrowRefusal.None, waitKey);
    }

    /// <summary>
    /// Releases a held escrow to a mail for <paramref name="toCharacterId"/> (online or not), in one transaction off the world
    /// thread. <see cref="EscrowReleased"/> is published once it committed; <see cref="IsReleased"/> says so.
    /// </summary>
    public EscrowRefusal ReleaseToMail(string escrowId, long toCharacterId, short mailKind, string opKey)
    {
        if (MailReleaser == null)
            return EscrowRefusal.NoMail;
        if (!_escrows.TryGetValue(escrowId, out var escrow) || escrow.State != State.Held)
            return escrow?.ReleaseKey == opKey ? EscrowRefusal.None : EscrowRefusal.NotHeld;
        escrow.State = State.Releasing;
        escrow.ReleaseKey = opKey;
        escrow.ReleasedTo = toCharacterId;
        escrow.ToMail = true;
        escrow.MailKind = mailKind;
        escrow.ReleaseAtMs = _clock.UtcNowMs;
        StartMail(new MailRelease(escrowId, toCharacterId, mailKind, opKey, escrow.ReleaseAtMs, 0));
        return EscrowRefusal.None;
    }

    /// <summary>
    /// True once the escrow is no longer held by this world: its release committed (released escrows are dropped from memory
    /// then, so the bookkeeping stays bounded), or this world never knew the id. False while held, being held or released, or
    /// unknown after a lost save.
    /// </summary>
    public bool IsReleased(string escrowId) => !_escrows.ContainsKey(escrowId);

    /// <summary>Applies finished releases to mail and retries those whose outcome was unknown (world thread, once per tick).</summary>
    public void Tick()
    {
        while (_mailDone.TryDequeue(out var done))
        {
            if (!_escrows.TryGetValue(done.EscrowId, out var escrow) || escrow.State != State.Releasing)
                continue;
            switch (done.Status)
            {
                case EscrowReleaseStatus.Released:
                    _escrows.Remove(done.EscrowId);
                    _events?.Publish(new EscrowReleased(done.EscrowId, escrow.Owner, true, escrow.ReleasedTo), escrow.Owner);
                    ReleasedToMail?.Invoke(escrow.ReleasedTo);
                    break;
                case EscrowReleaseStatus.Conflict:
                    escrow.State = State.Unknown; // released by someone else: never released twice; the database knows
                    break;
                default:
                    _mailWaiting.Add(new MailRelease(done.EscrowId, escrow.ReleasedTo, escrow.MailKind, escrow.ReleaseKey!, escrow.ReleaseAtMs,
                        _clock.UtcNowMs + RetryMs));
                    break;
            }
        }
        long now = _clock.UtcNowMs;
        for (int i = _mailWaiting.Count - 1; i >= 0; i--)
        {
            if (_mailWaiting[i].RetryAtMs > now)
                continue;
            var retry = _mailWaiting[i];
            _mailWaiting.RemoveAt(i);
            StartMail(retry);
        }
        PumpMail();
    }

    /// <summary>
    /// The character's session ended without its save (a fault or lost ownership): holds and releases not yet taken by a save
    /// are undone in memory (the database never saw them); those already in a save in flight become unknown until a restart.
    /// </summary>
    public void Forget(long characterId)
    {
        if (_pending.Remove(characterId, out var pending))
        {
            foreach (var hold in pending.Holds)
                _escrows.Remove(hold.EscrowId);
            foreach (var release in pending.Releases)
            {
                if (_escrows.TryGetValue(release.EscrowId, out var escrow))
                {
                    escrow.State = State.Held;
                    escrow.ReleaseKey = escrow.ReleaseWait = null;
                }
            }
        }
        if (_inFlight.Remove(characterId, out var inFlight))
        {
            foreach (var hold in inFlight.Batch.Holds)
                if (_escrows.TryGetValue(hold.EscrowId, out var escrow))
                    escrow.State = State.Unknown;
            foreach (var release in inFlight.Batch.Releases)
                if (_escrows.TryGetValue(release.EscrowId, out var escrow))
                    escrow.State = State.Unknown;
        }
    }

    public EscrowBatch? TakeBatch(long characterId, Guid saveId)
    {
        if (!_pending.Remove(characterId, out var pending))
            return null;
        var batch = new EscrowBatch(pending.Holds, pending.Releases);
        _inFlight[characterId] = new InFlight(saveId, batch);
        return batch;
    }

    public void Committed(long characterId, Guid saveId)
    {
        if (!_inFlight.TryGetValue(characterId, out var inFlight) || inFlight.SaveId != saveId)
            return;
        _inFlight.Remove(characterId);
        foreach (var hold in inFlight.Batch.Holds)
        {
            if (!_escrows.TryGetValue(hold.EscrowId, out var escrow) || escrow.State != State.Holding)
                continue;
            escrow.State = State.Held;
            _events?.Publish(new EscrowHeld(hold.EscrowId, hold.OwnerCharacterId, (short)hold.HolderKind, hold.HolderId, hold.Gold,
                hold.Items.Sum(i => i.Count)), hold.OwnerCharacterId);
        }
        foreach (var release in inFlight.Batch.Releases)
        {
            if (!_escrows.TryGetValue(release.EscrowId, out var escrow) || escrow.State != State.Releasing)
                continue;
            _escrows.Remove(release.EscrowId);
            _events?.Publish(new EscrowReleased(release.EscrowId, escrow.Owner, false, characterId), characterId);
        }
    }

    /// <summary>
    /// Queues a release to mail. One runs at a time off the world thread (the rest wait in order), so a burst of expiries never
    /// floods the database; <see cref="Tick"/> starts the next one.
    /// </summary>
    private void StartMail(MailRelease release)
    {
        _mailQueue.Enqueue(release);
        PumpMail();
    }

    private void PumpMail()
    {
        if (_stopping || _mailQueue.Count == 0 || _mailTask is { IsCompleted: false })
            return;
        var release = _mailQueue.Dequeue();
        var mail = MailReleaser!;
        _mailTask = Task.Run(async () =>
        {
            EscrowReleaseStatus? status;
            try
            {
                status = await mail.ReleaseToMailAsync(release.EscrowId, release.ToCharacterId, release.MailKind, release.OpKey, release.AtUtcMs);
            }
            catch (Exception)
            {
                status = null; // unknown: retried with the same release key (idempotent)
            }
            _mailDone.Enqueue((release.EscrowId, status));
        });
    }

    /// <summary>
    /// Shutdown: starts no further release to mail and returns the one in flight, so the host waits for it before the database
    /// goes away. A queued release not started stays held in the database and is released again by the next run's caller.
    /// </summary>
    public Task StopAsync()
    {
        _stopping = true;
        return _mailTask ?? Task.CompletedTask;
    }

    private Pending PendingOf(long characterId)
    {
        if (!_pending.TryGetValue(characterId, out var pending))
            _pending.Add(characterId, pending = new Pending());
        return pending;
    }

    private enum State : byte
    {
        /// <summary>Hold applied in memory, its save not committed yet.</summary>
        Holding,
        Held,
        Releasing,
        /// <summary>A save in flight was lost with its character, or another release won: never released again by this world.</summary>
        Unknown,
    }

    private sealed class Escrow(long owner, EscrowHolderKind holderKind, string holderId, long gold, List<EscrowItemRow> items)
    {
        public long Owner { get; } = owner;
        public EscrowHolderKind HolderKind { get; } = holderKind;
        public string HolderId { get; } = holderId;
        public long Gold { get; } = gold;
        public List<EscrowItemRow> Items { get; } = items;
        public State State { get; set; }
        public string? HoldKey { get; set; }
        public string? ReleaseKey { get; set; }
        public string? ReleaseWait { get; set; }
        public long ReleasedTo { get; set; }
        public bool ToMail { get; set; }
        public short MailKind { get; set; }
        public long ReleaseAtMs { get; set; }
    }

    private sealed class Pending
    {
        public List<EscrowHoldRow> Holds { get; } = new();
        public List<EscrowReleaseRow> Releases { get; } = new();
    }

    private sealed record InFlight(Guid SaveId, EscrowBatch Batch);

    private readonly record struct MailRelease(string EscrowId, long ToCharacterId, short MailKind, string OpKey, long AtUtcMs, long RetryAtMs);
}
