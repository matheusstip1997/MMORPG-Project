using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Persistence;

/// <summary>Where the scheduler reads finished saves from (the worker's completion queue, spec 1.6).</summary>
public interface ISaveCompletionSource
{
    bool TryDequeue(out SaveCompletion completion);
}

/// <summary>
/// Decides when each online character is saved (R-7.1-01, R-7.1-02, R-7.1-05). Runs on the owning world's
/// thread and never waits on the database: it captures state, hands a <see cref="SaveJob"/> to the worker,
/// and reads the result back from the completion queue on a later <see cref="Tick"/>.
/// <list type="bullet">
/// <item>Class 2 changes are saved in a batch every <c>SaveClass2IntervalMs</c>; volatile state every
/// <c>VolatileSaveIntervalMs</c>. Characters are processed in ID order (rule 6).</item>
/// <item>Logout, zone change, death and level-up ask for an immediate save (<see cref="RequestSave"/>).</item>
/// <item>Before a class 1 operation, <see cref="RequestBarrier"/> flushes pending class 2 changes first.</item>
/// <item>At most one save per character is in flight, so versions advance in order. A failed save is
/// retried as the identical job (same save ID, so a save that did commit is recognised). A version conflict,
/// a missing or a deleted row means another writer exists: the character is marked faulted, never overwritten.</item>
/// </list>
/// Inventory changes (class 2) ride on these saves: each issued save takes the pending batch of
/// <see cref="IInventoryBatchSource"/> and reports its commit back. Progression changes (WP-M2-3) ride the same way through
/// <see cref="IProgressionBatchSource"/>, and the Bystial book (WP-M2-8) through <see cref="IBystialBatchSource"/>, and run changes (WP-M2-6) through
/// <see cref="IQuestBatchSource"/>, and job changes (WP-M3-1) through <see cref="IJobBatchSource"/>. Class 1 transactions themselves are not scheduled
/// here: they belong to their services (InventoryService, ...).
/// </summary>
public sealed class SaveScheduler
{
    private readonly int _class2IntervalMs;
    private readonly int _volatileIntervalMs;
    private readonly IClock _clock;
    private readonly ICharacterStateSource _states;
    private readonly ISaveJobSink _jobs;
    private readonly ISaveCompletionSource _completions;
    private readonly Action<long, SaveOutcome>? _onFault;
    private readonly IInventoryBatchSource? _inventory;
    private readonly IProgressionBatchSource? _progression; // WP-M2-3
    private readonly ISkillBatchSource? _skills; // WP-M2-5
    private readonly IBystialBatchSource? _bystial; // WP-M2-8
    private readonly IQuestBatchSource? _quests; // WP-M2-6
    private readonly IJobBatchSource? _jobsSource; // WP-M3-1
    private readonly ICraftBatchSource? _craftSource; // WP-M3-6
    private readonly IEscrowBatchSource? _escrowSource; // WP-M4-4

    private readonly Dictionary<long, Entry> _byId = new();
    private readonly List<Entry> _ordered = new(); // sorted by character ID
    private long _nextClass2DueMs;
    private long _nextVolatileDueMs;

    public SaveScheduler(StartingValuesM1 values, IClock clock, ICharacterStateSource states, ISaveJobSink jobs,
        ISaveCompletionSource completions, Action<long, SaveOutcome>? onFault = null, IInventoryBatchSource? inventory = null,
        IProgressionBatchSource? progression = null, ISkillBatchSource? skills = null, IBystialBatchSource? bystial = null,
        IQuestBatchSource? quests = null, IJobBatchSource? jobBatches = null, ICraftBatchSource? craftBatches = null,
        IEscrowBatchSource? escrowBatches = null)
    {
        _class2IntervalMs = values.SaveClass2IntervalMs;
        _volatileIntervalMs = values.VolatileSaveIntervalMs;
        _clock = clock;
        _states = states;
        _jobs = jobs;
        _completions = completions;
        _onFault = onFault;
        _inventory = inventory;
        _progression = progression;
        _skills = skills;
        _bystial = bystial;
        _quests = quests;
        _jobsSource = jobBatches;
        _craftSource = craftBatches;
        _escrowSource = escrowBatches;

        var now = clock.UtcNowMs;
        _nextClass2DueMs = now + _class2IntervalMs;
        _nextVolatileDueMs = now + _volatileIntervalMs;
    }

    /// <summary>
    /// Mail writes (WP-M4-3): the sends a character's save carries and its claims, taken after the inventory batch and told of
    /// the commit after the inventory (a claim's answer needs its items committed). Set once at start.
    /// </summary>
    public IMailBatchSource? MailBatches { get; set; }

    /// <summary>A save was issued for a character (it carries every change made before): the dev console binds its waiting answers to it (WP-M3-13).</summary>
    public Action<long, Guid>? SaveIssued { get; set; }

    /// <summary>A save committed, after every batch source heard of it (the dev console answers then, rule 5; WP-M3-13).</summary>
    public Action<long, Guid>? SaveCommitted { get; set; }

    public int TrackedCount => _ordered.Count;

    /// <summary>Starts tracking a character that was just loaded at <paramref name="version"/>.</summary>
    public void Track(long characterId, long version)
    {
        if (_byId.ContainsKey(characterId))
            throw new InvalidOperationException($"character {characterId} is already tracked");

        var entry = new Entry(characterId, version);
        _byId.Add(characterId, entry);
        var index = _ordered.Count;
        while (index > 0 && _ordered[index - 1].CharacterId > characterId)
            index--;
        _ordered.Insert(index, entry);
    }

    /// <summary>Stops tracking once nothing is pending or in flight (after the logout save). Returns false otherwise.</summary>
    public bool TryUntrack(long characterId)
    {
        var entry = Get(characterId);
        if (entry.InFlight || (!entry.Faulted && !entry.IsIdle))
            return false;
        _byId.Remove(characterId);
        _ordered.Remove(entry);
        return true;
    }

    /// <summary>
    /// Stops tracking at once, dropping anything unsaved: for a character this world no longer owns (ownership lock
    /// lost, or a save fault showed another writer, R-2.1-06). A completion still in flight is then ignored.
    /// </summary>
    public void Forget(long characterId)
    {
        if (!_byId.Remove(characterId, out var entry))
            return;
        _ordered.Remove(entry);
        AbortLink(entry); // WP-M4-4: a partner left holding half a transfer that was never sent is faulted too
    }

    /// <summary>
    /// WP-M4-4 (spec 4.5, 7.1 R-7.1-02 over two characters): <paramref name="transfer"/> was applied to both characters in
    /// memory; save both in <b>one</b> job. From now on neither is saved alone: the pair job is issued once neither has a save
    /// in flight, and it carries every pending batch of both (the barrier by inclusion). False, and nothing linked, when either
    /// is untracked, faulted or already linked to another transfer (the caller must not apply a second one).
    /// </summary>
    public bool RequestTransferSave(TransferRecord transfer)
    {
        if (!_byId.TryGetValue(transfer.CharacterA, out var a) || !_byId.TryGetValue(transfer.CharacterB, out var b)
            || a.Faulted || b.Faulted || a.Link != null || b.Link != null)
            return false;
        var link = new TransferLink(transfer);
        foreach (var entry in new[] { a, b })
        {
            entry.Link = link;
            entry.Wanted = true;
            entry.Reason = SaveReason.Transfer;
        }
        return true;
    }

    /// <summary>
    /// Undoes <see cref="RequestTransferSave"/> before its pair job was sent (the caller applied nothing after all): both
    /// characters save alone again. False when the link is unknown or already sent.
    /// </summary>
    public bool AbortTransfer(TransferRecord transfer)
    {
        if (!_byId.TryGetValue(transfer.CharacterA, out var a) || a.Link is not { } link || link.Issued
            || link.Transfer.OpKey != transfer.OpKey)
            return false;
        a.Link = null;
        if (_byId.TryGetValue(transfer.CharacterB, out var b) && b.Link == link)
            b.Link = null;
        return true;
    }

    /// <summary>A transfer is linked to this character and not yet committed (it is saved only with its partner).</summary>
    public bool IsLinked(long characterId) => Get(characterId).Link != null;

    public bool IsTracked(long characterId) => _byId.ContainsKey(characterId);

    public void MarkDirty(long characterId, DirtyFlags flags) => Get(characterId).Dirty |= flags;

    /// <summary>Saves at the next <see cref="Tick"/>, outside the periodic cadence (logout, zone change, death, level-up).</summary>
    public void RequestSave(long characterId, SaveReason reason)
    {
        var entry = Get(characterId);
        entry.Wanted = true;
        entry.Reason = reason;
    }

    /// <summary>
    /// Ordering barrier (R-7.1-02). Returns true when the character has no class 2 change that is not yet in the
    /// database, so a class 1 operation may start. Otherwise starts the flush (if possible) and returns false:
    /// call again after a later tick.
    /// </summary>
    public bool RequestBarrier(long characterId)
    {
        var entry = Get(characterId);
        if (entry.Faulted || entry.Link != null) // a linked character saves only with its transfer partner (WP-M4-4)
            return false;

        if ((entry.Dirty & DirtyFlags.Class2) == 0 && !entry.InFlight && !entry.PendingRetry)
            return true;

        if ((entry.Dirty & DirtyFlags.Class2) != 0)
        {
            entry.Wanted = true;
            entry.Reason = SaveReason.Barrier;
            if (!entry.InFlight && !entry.PendingRetry)
                Issue(entry);
        }

        return false;
    }

    public bool IsIdle(long characterId) => Get(characterId).IsIdle;

    public bool IsFaulted(long characterId) => Get(characterId).Faulted;

    /// <summary>The last version known to be in the database.</summary>
    public long VersionOf(long characterId) => Get(characterId).Version;

    /// <summary>Called once per world tick: applies finished saves, then issues the saves that are due.</summary>
    public void Tick()
    {
        while (_completions.TryDequeue(out var completion))
            OnCompleted(completion);

        var now = _clock.UtcNowMs;
        var class2Due = now >= _nextClass2DueMs;
        if (class2Due)
            _nextClass2DueMs = now + _class2IntervalMs;
        var volatileDue = now >= _nextVolatileDueMs;
        if (volatileDue)
            _nextVolatileDueMs = now + _volatileIntervalMs;

        foreach (var entry in _ordered)
        {
            if (entry.Faulted)
                continue;

            if (class2Due && (entry.Dirty & DirtyFlags.Class2) != 0)
                Want(entry, SaveReason.Class2Batch);
            if (volatileDue && (entry.Dirty & DirtyFlags.Volatile) != 0)
                Want(entry, SaveReason.VolatilePeriodic);

            if (entry.InFlight)
                continue;

            if (entry.PendingRetry)
            {
                // Retry on the class 2 cadence, so a database outage is not hammered every tick. A pair is resent whole, by
                // its lower tracked side (WP-M4-4).
                if (class2Due)
                {
                    if (entry.LastPair is { } pair)
                        RetryPair(entry, pair);
                    else
                        Send(entry, entry.LastJob);
                }
                continue;
            }

            if (entry.Link is { } link)
            {
                // WP-M4-4: never alone; the lower side issues the pair once neither side has a save in flight.
                if (entry.CharacterId == link.Transfer.CharacterA && _byId.TryGetValue(link.Transfer.CharacterB, out var partner)
                    && !partner.Faulted && !partner.InFlight && !partner.PendingRetry)
                    IssuePair(entry, partner, link);
                continue;
            }

            if (entry.Wanted)
                Issue(entry);
        }
    }

    private void OnCompleted(in SaveCompletion completion)
    {
        // Idempotent: unknown characters, stale or repeated completions are ignored.
        if (!_byId.TryGetValue(completion.CharacterId, out var entry) || !entry.InFlight || entry.LastJob.SaveId != completion.SaveId)
            return;

        entry.InFlight = false;
        switch (completion.Outcome)
        {
            case SaveOutcome.Saved:
                entry.Version = completion.Version;
                _inventory?.Committed(entry.CharacterId, completion.SaveId);
                _progression?.Committed(entry.CharacterId, completion.SaveId);
                _skills?.Committed(entry.CharacterId, completion.SaveId);
                _bystial?.Committed(entry.CharacterId, completion.SaveId);
                _quests?.Committed(entry.CharacterId, completion.SaveId);
                _jobsSource?.Committed(entry.CharacterId, completion.SaveId);
                _craftSource?.Committed(entry.CharacterId, completion.SaveId); // after the inventory: its answers need the items committed
                _escrowSource?.Committed(entry.CharacterId, completion.SaveId); // after the inventory, like the forge (WP-M4-4)
                MailBatches?.Committed(entry.CharacterId, completion.SaveId); // WP-M4-3, after the inventory as well
                if (entry.LastPair != null)
                {
                    entry.Link = null;
                    entry.LastPair = null;
                }
                SaveCommitted?.Invoke(entry.CharacterId, completion.SaveId);
                break;
            case SaveOutcome.Failed:
                entry.PendingRetry = true;
                break;
            default:
                Fault(entry, completion.Outcome);
                break;
        }
    }

    private void Fault(Entry entry, SaveOutcome outcome)
    {
        if (entry.Faulted)
            return;
        entry.Faulted = true;
        _onFault?.Invoke(entry.CharacterId, outcome);
        AbortLink(entry);
    }

    /// <summary>
    /// A linked character was faulted or forgotten (WP-M4-4). If its pair job was never sent, the partner's memory holds a half
    /// that can never commit alone: fault it too. Once the pair was sent, the partner hears the pair's own outcome.
    /// </summary>
    private void AbortLink(Entry entry)
    {
        if (entry.Link is not { } link || link.Issued)
            return;
        long partnerId = entry.CharacterId == link.Transfer.CharacterA ? link.Transfer.CharacterB : link.Transfer.CharacterA;
        if (_byId.TryGetValue(partnerId, out var partner) && partner.Link == link)
            Fault(partner, SaveOutcome.TransferAborted);
    }

    private void IssuePair(Entry a, Entry b, TransferLink link)
    {
        var jobA = Capture(a);
        var jobB = Capture(b);
        var pair = new TransferSaveJob(jobA, jobB, link.Transfer);
        link.Issued = true;
        foreach (var (entry, job) in new[] { (a, jobA), (b, jobB) })
        {
            entry.LastPair = pair;
            entry.LastJob = job;
            entry.InFlight = true;
            entry.PendingRetry = false;
        }
        TransferSink.EnqueueTransfer(pair);
        SaveIssued?.Invoke(a.CharacterId, jobA.SaveId);
        SaveIssued?.Invoke(b.CharacterId, jobB.SaveId);
    }

    /// <summary>Resends the identical pair job (same save ids), by its lower side still tracked, once.</summary>
    private void RetryPair(Entry entry, in TransferSaveJob pair)
    {
        long other = entry.CharacterId == pair.A.CharacterId ? pair.B.CharacterId : pair.A.CharacterId;
        _byId.TryGetValue(other, out var partner);
        if (partner != null && partner.CharacterId < entry.CharacterId && partner.PendingRetry && !partner.Faulted)
            return; // the lower side sends it
        foreach (var side in new[] { entry, partner })
        {
            if (side == null || side.LastPair is not { } own || own.Transfer.OpKey != pair.Transfer.OpKey)
                continue;
            side.InFlight = true;
            side.PendingRetry = false;
        }
        TransferSink.EnqueueTransfer(pair);
    }

    private ITransferJobSink TransferSink => _jobs as ITransferJobSink
        ?? throw new InvalidOperationException("the save worker cannot write a transfer between two characters");

    private static void Want(Entry entry, SaveReason periodicReason)
    {
        if (!entry.Wanted)
            entry.Reason = periodicReason;
        entry.Wanted = true;
    }

    private void Issue(Entry entry)
    {
        var job = Capture(entry);
        Send(entry, job);
        SaveIssued?.Invoke(entry.CharacterId, job.SaveId);
    }

    /// <summary>A save of everything the character has pending (its state and every batch), clearing its dirty flags.</summary>
    private SaveJob Capture(Entry entry)
    {
        // v1 writes the whole row, so one save covers both kinds of change; it also carries the pending inventory batch.
        var saveId = Guid.NewGuid();
        var job = new SaveJob(entry.CharacterId, _states.Capture(entry.CharacterId), entry.Version, saveId, entry.Reason,
            _inventory?.TakeBatch(entry.CharacterId, saveId), _progression?.TakeBatch(entry.CharacterId, saveId),
            _skills?.TakeBatch(entry.CharacterId, saveId), _bystial?.TakeBatch(entry.CharacterId, saveId),
            _quests?.TakeBatch(entry.CharacterId, saveId), _jobsSource?.TakeBatch(entry.CharacterId, saveId),
            _craftSource?.TakeBatch(entry.CharacterId, saveId), _escrowSource?.TakeBatch(entry.CharacterId, saveId),
            MailBatches?.TakeBatch(entry.CharacterId, saveId));
        entry.Dirty = DirtyFlags.None;
        entry.Wanted = false;
        return job;
    }

    private void Send(Entry entry, in SaveJob job)
    {
        entry.LastJob = job;
        entry.InFlight = true;
        entry.PendingRetry = false;
        _jobs.Enqueue(job);
    }

    private Entry Get(long characterId) =>
        _byId.TryGetValue(characterId, out var entry) ? entry : throw new KeyNotFoundException($"character {characterId} is not tracked");

    /// <summary>A transfer both characters wait to save together (WP-M4-4); shared by their two entries.</summary>
    private sealed class TransferLink(TransferRecord transfer)
    {
        public TransferRecord Transfer { get; } = transfer;

        /// <summary>The pair job was sent: from now on both hear its own outcome.</summary>
        public bool Issued { get; set; }
    }

    private sealed class Entry(long characterId, long version)
    {
        public long CharacterId { get; } = characterId;
        public long Version { get; set; } = version;
        public DirtyFlags Dirty { get; set; }
        public bool Wanted { get; set; }
        public SaveReason Reason { get; set; }
        public bool InFlight { get; set; }
        public bool PendingRetry { get; set; }
        public bool Faulted { get; set; }
        public SaveJob LastJob { get; set; }

        /// <summary>The transfer this character waits to save with its partner (WP-M4-4), until the pair committed.</summary>
        public TransferLink? Link { get; set; }

        /// <summary>The pair job last sent for <see cref="Link"/>, resent identically on a retry.</summary>
        public TransferSaveJob? LastPair { get; set; }

        public bool IsIdle => Dirty == DirtyFlags.None && !Wanted && !InFlight && !PendingRetry && Link == null;
    }
}
