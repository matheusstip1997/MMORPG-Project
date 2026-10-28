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
/// <see cref="IQuestBatchSource"/>. Class 1 transactions themselves are not scheduled
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

    private readonly Dictionary<long, Entry> _byId = new();
    private readonly List<Entry> _ordered = new(); // sorted by character ID
    private long _nextClass2DueMs;
    private long _nextVolatileDueMs;

    public SaveScheduler(StartingValuesM1 values, IClock clock, ICharacterStateSource states, ISaveJobSink jobs,
        ISaveCompletionSource completions, Action<long, SaveOutcome>? onFault = null, IInventoryBatchSource? inventory = null,
        IProgressionBatchSource? progression = null, ISkillBatchSource? skills = null, IBystialBatchSource? bystial = null,
        IQuestBatchSource? quests = null)
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

        var now = clock.UtcNowMs;
        _nextClass2DueMs = now + _class2IntervalMs;
        _nextVolatileDueMs = now + _volatileIntervalMs;
    }

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
        if (_byId.Remove(characterId, out var entry))
            _ordered.Remove(entry);
    }

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
        if (entry.Faulted)
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
                // Retry on the class 2 cadence, so a database outage is not hammered every tick.
                if (class2Due)
                    Send(entry, entry.LastJob);
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
                break;
            case SaveOutcome.Failed:
                entry.PendingRetry = true;
                break;
            default:
                entry.Faulted = true;
                _onFault?.Invoke(entry.CharacterId, completion.Outcome);
                break;
        }
    }

    private static void Want(Entry entry, SaveReason periodicReason)
    {
        if (!entry.Wanted)
            entry.Reason = periodicReason;
        entry.Wanted = true;
    }

    private void Issue(Entry entry)
    {
        // v1 writes the whole row, so one save covers both kinds of change; it also carries the pending inventory batch.
        var saveId = Guid.NewGuid();
        var job = new SaveJob(entry.CharacterId, _states.Capture(entry.CharacterId), entry.Version, saveId, entry.Reason,
            _inventory?.TakeBatch(entry.CharacterId, saveId), _progression?.TakeBatch(entry.CharacterId, saveId),
            _skills?.TakeBatch(entry.CharacterId, saveId), _bystial?.TakeBatch(entry.CharacterId, saveId),
            _quests?.TakeBatch(entry.CharacterId, saveId));
        entry.Dirty = DirtyFlags.None;
        entry.Wanted = false;
        Send(entry, job);
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

        public bool IsIdle => Dirty == DirtyFlags.None && !Wanted && !InFlight && !PendingRetry;
    }
}
