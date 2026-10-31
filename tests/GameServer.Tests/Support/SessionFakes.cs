using System.Collections.Concurrent;
using GameServer.Persistence;
using GameServer.Sessions;
using Shared.Protocol;
using Shared.Data.Tables;
using Shared.Util;

namespace GameServer.Tests.Support;

public static class TestSessions
{
    /// <summary>The session values a test cares about; linger defaults to none so a leave removes the character at once.</summary>
    public static StartingValuesM1 Values(int lingerOutOfCombatMs = 0, int lingerInCombatMs = 0, int combatStateMs = 10_000,
        int class2Ms = 1_000, int volatileMs = 10_000) => new()
    {
        LingerOutOfCombatMs = lingerOutOfCombatMs,
        LingerInCombatMs = lingerInCombatMs,
        CombatStateMs = combatStateMs,
        SaveClass2IntervalMs = class2Ms,
        VolatileSaveIntervalMs = volatileMs,
    };

    public static SessionSettings Settings(int lockTtlMs = 30_000, int heartbeatMs = 10_000, int shutdownSaveTimeoutMs = 2_000) => new()
    {
        OwnershipLockTtlMs = lockTtlMs,
        HeartbeatIntervalMs = heartbeatMs,
        ShutdownSaveTimeoutMs = shutdownSaveTimeoutMs,
    };
}

/// <summary>
/// Characters in memory. Unknown (account, slot) pairs get a fresh character with no saved world (so it spawns at the
/// fallback spawn) unless <see cref="AutoCreate"/> is off. Completed tasks, unless a test holds a call with a gate.
/// </summary>
public sealed class FakeCharacterSource : ICharacterSource
{
    private readonly Dictionary<(long Account, int Slot), CharacterRecord> _bySlot = new();
    private readonly Dictionary<long, CharacterRecord> _byId = new();
    private readonly Lock _lock = new();
    private long _nextId = 1;

    public bool AutoCreate { get; set; } = true;

    /// <summary>The class of the characters <see cref="AutoCreate"/> makes (its ActionBindings row is the kit it plays).</summary>
    public string AutoCreateClassId { get; set; } = "warrior";

    /// <summary>When set, <see cref="LoadAsync"/> throws this (a crash while loading).</summary>
    public Exception? LoadFailure { get; set; }

    /// <summary>When set, <see cref="LoadAsync"/> waits for it (a load in flight).</summary>
    public TaskCompletionSource? LoadGate { get; set; }

    /// <summary>
    /// Completes the held load so its continuations run inline, as they would on the thread pool: under xUnit's
    /// synchronization context .NET would queue them instead and the test would race the next tick (on a busy CI runner
    /// for over a second).
    /// </summary>
    public void OpenLoadGate()
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            LoadGate!.SetResult();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    public int Loads { get; private set; }

    public CharacterRecord Add(long accountId, int slot, CharacterState state, long version = 1, string classId = "warrior")
    {
        lock (_lock)
        {
            var record = new CharacterRecord(_nextId++, 1, accountId, slot, $"c{accountId}_{slot}", classId, state, version, 0, 0, null, null);
            _bySlot[(accountId, slot)] = record;
            _byId[record.CharacterId] = record;
            return record;
        }
    }

    /// <summary>What a batch wrote to the gold column (WP-M2-4), as a later login reads it.</summary>
    public void StoreGold(long characterId, long gold)
    {
        lock (_lock)
        {
            var record = _byId[characterId] with { Gold = gold };
            _byId[characterId] = record;
            _bySlot[(record.AccountId, record.Slot)] = record;
        }
    }

    /// <summary>What a save wrote, as a later login reads it.</summary>
    public void Store(long characterId, CharacterState state, long version)
    {
        lock (_lock)
        {
            var record = _byId[characterId] with { State = state, Version = version };
            _byId[characterId] = record;
            _bySlot[(record.AccountId, record.Slot)] = record;
        }
    }

    public void MarkDeleted(long characterId)
    {
        lock (_lock)
        {
            var record = _byId[characterId] with { DeletedAtUtcMs = 1, PurgeAfterUtcMs = 2 };
            _byId[characterId] = record;
            _bySlot[(record.AccountId, record.Slot)] = record;
        }
    }

    public CharacterRecord Get(long characterId)
    {
        lock (_lock)
            return _byId[characterId];
    }

    /// <summary>The managed thread of the last <see cref="FindBySlotAsync"/> call.</summary>
    public int FindThreadId { get; private set; }

    public Task<CharacterRecord?> FindBySlotAsync(long accountId, int slot)
    {
        FindThreadId = Environment.CurrentManagedThreadId;
        lock (_lock)
        {
            if (_bySlot.TryGetValue((accountId, slot), out var record))
                return Task.FromResult<CharacterRecord?>(record);
        }
        if (!AutoCreate)
            return Task.FromResult<CharacterRecord?>(null);
        return Task.FromResult<CharacterRecord?>(Add(accountId, slot, new CharacterState(1, 0, "", 0, 0, 0, 0, 100, 50, 100), classId: AutoCreateClassId));
    }

    /// <summary>What an inventory batch wrote (its final slot states), as a later login reads it.</summary>
    public void StoreItems(long characterId, IReadOnlyList<SlotState> slots)
    {
        lock (_lock)
        {
            var rows = Items.TryGetValue(characterId, out var existing) ? existing.ToList() : new List<ItemStackRow>();
            foreach (var slot in slots)
            {
                rows.RemoveAll(r => r.Container == slot.Container && r.Slot == slot.Slot);
                if (!slot.IsEmpty)
                    rows.Add(new ItemStackRow(slot.Container, slot.Slot, slot.DefId!, slot.Rarity, slot.Stolen, slot.Count));
            }
            Items[characterId] = rows.OrderBy(r => r.Container).ThenBy(r => r.Slot).ToList();
        }
    }

    /// <summary>Progression a login reads (WP-M2-3), by character: the counters JSON and the kill counts.</summary>
    public Dictionary<long, ProgressionRecord> Progression { get; } = new();

    /// <summary>Rank-ups the fake saves wrote (WP-M2-3), in order.</summary>
    public List<RankUpRecord> RankUps { get; } = new();

    public void StoreProgression(long characterId, ProgressionBatch batch)
    {
        lock (_lock)
        {
            var current = Progression.TryGetValue(characterId, out var existing) ? existing : ProgressionRecord.Empty;
            var kills = current.KillCounts.ToDictionary(k => k.MonsterDefId, k => k.Count);
            foreach (var kill in batch.KillCounts)
                kills[kill.MonsterDefId] = kill.Count;
            Progression[characterId] = new ProgressionRecord(batch.AchievementCountersJson ?? current.AchievementCountersJson,
                kills.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new MonsterKillCount(k.Key, k.Value)).ToList());
            RankUps.AddRange(batch.RankUps);
        }
    }

    /// <summary>Runs the fake saves wrote (WP-M2-6), by run id: the latest whole row.</summary>
    public Dictionary<Guid, RunRecord> Runs { get; } = new();

    /// <summary>Run audit rows the fake saves wrote (WP-M2-6), in order.</summary>
    public List<RunAuditRecord> RunAudit { get; } = new();

    public void StoreRuns(long characterId, QuestBatch batch)
    {
        lock (_lock)
        {
            foreach (var run in batch.Runs)
                Runs[run.RunId] = run;
            RunAudit.AddRange(batch.Audit);
        }
    }

    public Task<IReadOnlyList<RunRecord>> LoadRunsAsync(long characterId)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<RunRecord>>(Runs.Values
                .Where(r => r.OwnerCharacterId == characterId && r.State is RunState.Active or RunState.Completed).ToList());
        }
    }

    public Task<ProgressionRecord> LoadProgressionAsync(long characterId)
    {
        lock (_lock)
        {
            return Task.FromResult(Progression.TryGetValue(characterId, out var record) ? record : ProgressionRecord.Empty);
        }
    }

    /// <summary>Learned skills and hotbars a login reads (WP-M2-5), by character; none by default (a new character).</summary>
    public Dictionary<long, SkillRecord> Skills { get; } = new();

    /// <summary>Skill operations the fake saves wrote (WP-M2-5), in order.</summary>
    public List<SkillOpRecord> SkillOps { get; } = new();

    /// <summary>What a skill batch wrote: the learned set, the hotbar and the class (on the character row).</summary>
    public void StoreSkills(long characterId, SkillBatch batch)
    {
        lock (_lock)
        {
            Skills[characterId] = new SkillRecord(batch.Learned.ToList(), batch.HotbarJson);
            SkillOps.AddRange(batch.Ops);
            if (batch.ClassId is { } classId)
            {
                var record = _byId[characterId] with { ClassId = classId };
                _byId[characterId] = record;
                _bySlot[(record.AccountId, record.Slot)] = record;
            }
        }
    }

    public Task<SkillRecord> LoadSkillsAsync(long characterId)
    {
        lock (_lock)
            return Task.FromResult(Skills.TryGetValue(characterId, out var record) ? record : SkillRecord.Empty);
    }

    /// <summary>Job records a login reads (WP-M3-1), by character; none by default.</summary>
    public Dictionary<long, JobRecord> JobRecords { get; } = new();

    /// <summary>Job operations the fake saves wrote (WP-M3-1), in order.</summary>
    public List<JobOpRecord> JobOps { get; } = new();

    /// <summary>What a job batch wrote: every record as it is now (an upsert by job).</summary>
    public void StoreJobs(long characterId, JobBatch batch)
    {
        lock (_lock)
        {
            var current = JobRecords.TryGetValue(characterId, out var existing) ? existing.Jobs.ToDictionary(j => j.JobId) : new();
            foreach (var job in batch.Jobs)
                current[job.JobId] = job;
            JobRecords[characterId] = new JobRecord(current.Values.OrderBy(j => j.Slot).ToList());
            JobOps.AddRange(batch.Ops);
        }
    }

    public Task<JobRecord> LoadJobsAsync(long characterId)
    {
        lock (_lock)
            return Task.FromResult(JobRecords.TryGetValue(characterId, out var record) ? record : JobRecord.Empty);
    }

    /// <summary>The recent conversions a login reads (WP-M3-6), by character; none by default.</summary>
    public Dictionary<long, CraftRecord> CraftRecords { get; } = new();

    /// <summary>Forge operations the fake saves wrote (WP-M3-6), in order.</summary>
    public List<CraftOpRecord> CraftOps { get; } = new();

    /// <summary>What a forge batch wrote: its operations (a conversion also counts for the next login's daily cap).</summary>
    public void StoreCraft(long characterId, CraftBatch batch)
    {
        lock (_lock)
        {
            CraftOps.AddRange(batch.Ops);
            var conversions = CraftRecords.TryGetValue(characterId, out var existing) ? existing.Conversions.ToList() : new();
            conversions.AddRange(batch.Ops.Where(o => o.Kind == CraftOpKind.Convert).Select(o => new CraftConversion(o.Tier + 1, o.AtUtcMs)));
            CraftRecords[characterId] = new CraftRecord(conversions);
        }
    }

    public Task<CraftRecord> LoadCraftAsync(long characterId)
    {
        lock (_lock)
            return Task.FromResult(CraftRecords.TryGetValue(characterId, out var record) ? record : CraftRecord.Empty);
    }

    /// <summary>The Bystial book a login reads (WP-M2-8), by character.</summary>
    public Dictionary<long, BystialRecord> Books { get; } = new();

    /// <summary>Book operations the fake saves wrote (WP-M2-8), in order.</summary>
    public List<BystialOpRecord> BookOps { get; } = new();

    public void StoreBook(long characterId, BystialBatch batch)
    {
        lock (_lock)
        {
            var current = Books.TryGetValue(characterId, out var existing) ? existing : BystialRecord.Empty;
            var cards = current.Cards.ToDictionary(c => c.MonsterDefId);
            foreach (var card in batch.Cards)
                cards[card.MonsterDefId] = card;
            Books[characterId] = new BystialRecord(cards.Values.OrderBy(c => c.MonsterDefId, StringComparer.Ordinal).ToList());
            BookOps.AddRange(batch.Ops);
        }
    }

    public Task<BystialRecord> LoadBystialAsync(long characterId)
    {
        lock (_lock)
        {
            return Task.FromResult(Books.TryGetValue(characterId, out var record) ? record : BystialRecord.Empty);
        }
    }

    /// <summary>Item stacks a login reads (backlog G), by character.</summary>
    public Dictionary<long, IReadOnlyList<ItemStackRow>> Items { get; } = new();

    /// <summary>Instances a login reads (WP-M2-1), by character; the starter counts as granted unless listed in <see cref="NoStarterYet"/>.</summary>
    public Dictionary<long, IReadOnlyList<ItemInstanceRow>> Instances { get; } = new();

    public HashSet<long> NoStarterYet { get; } = new();

    public Task<InventoryLoad> LoadItemsAsync(long characterId)
    {
        lock (_lock)
        {
            return Task.FromResult(new InventoryLoad(
                Items.TryGetValue(characterId, out var rows) ? rows : Array.Empty<ItemStackRow>(),
                Instances.TryGetValue(characterId, out var instances) ? instances : Array.Empty<ItemInstanceRow>(),
                0, !NoStarterYet.Contains(characterId)));
        }
    }

    public async Task<CharacterRecord?> LoadAsync(long characterId)
    {
        Loads++;
        if (LoadGate is { } gate)
            await gate.Task.ConfigureAwait(false);
        if (LoadFailure is { } failure)
            throw failure;
        lock (_lock)
            return _byId.GetValueOrDefault(characterId);
    }
}

/// <summary>Ownership locks in memory with TTLs on a test clock.</summary>
public sealed class InMemoryOwnershipLocks(IClock clock, int ttlMs) : IOwnershipLocks
{
    private readonly Dictionary<long, (string Token, long ExpiresAtMs)> _locks = new();
    private readonly Lock _lock = new();

    /// <summary>When set, every call throws this (Redis unreachable).</summary>
    public Exception? Failure { get; set; }

    public int Refreshes { get; private set; }

    public string? HolderOf(long characterId)
    {
        lock (_lock)
            return _locks.TryGetValue(characterId, out var held) && held.ExpiresAtMs > clock.UtcNowMs ? held.Token : null;
    }

    /// <summary>Another server takes the lock (after this one's expired).</summary>
    public void Set(long characterId, string token)
    {
        lock (_lock)
            _locks[characterId] = (token, clock.UtcNowMs + ttlMs);
    }

    public void Remove(long characterId)
    {
        lock (_lock)
            _locks.Remove(characterId);
    }

    public Task<bool> TryAcquireAsync(long characterId, string token)
    {
        ThrowIfFailing();
        lock (_lock)
        {
            if (HolderOf(characterId) != null)
                return Task.FromResult(false);
            _locks[characterId] = (token, clock.UtcNowMs + ttlMs);
            return Task.FromResult(true);
        }
    }

    public Task<bool> RefreshAsync(long characterId, string token)
    {
        ThrowIfFailing();
        lock (_lock)
        {
            Refreshes++;
            if (HolderOf(characterId) != token)
                return Task.FromResult(false);
            _locks[characterId] = (token, clock.UtcNowMs + ttlMs);
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(long characterId, string token)
    {
        ThrowIfFailing();
        lock (_lock)
        {
            if (HolderOf(characterId) == token)
                _locks.Remove(characterId);
        }
        return Task.CompletedTask;
    }

    private void ThrowIfFailing()
    {
        if (Failure != null)
            throw Failure;
    }
}

/// <summary>
/// Persistence worker stand-in: with <see cref="AutoComplete"/> each job completes at once as saved (the scheduler reads
/// it at its next tick) and is written to <see cref="Store"/>; otherwise jobs wait for <see cref="CompleteAll"/>.
/// </summary>
public sealed class FakeSaveWorker : ISaveJobSink, ISaveCompletionSource
{
    private readonly ConcurrentQueue<SaveCompletion> _completions = new();
    private readonly List<SaveJob> _waiting = [];

    public bool AutoComplete { get; set; } = true;

    /// <summary>Outcome of the jobs completed from now on.</summary>
    public SaveOutcome Outcome { get; set; } = SaveOutcome.Saved;

    public FakeCharacterSource? Store { get; set; }

    public List<SaveJob> Jobs { get; } = [];

    public void Enqueue(in SaveJob job)
    {
        Jobs.Add(job);
        if (AutoComplete)
            Complete(job);
        else
            _waiting.Add(job);
    }

    public void CompleteAll()
    {
        foreach (var job in _waiting)
            Complete(job);
        _waiting.Clear();
    }

    public bool TryDequeue(out SaveCompletion completion) => _completions.TryDequeue(out completion);

    private void Complete(in SaveJob job)
    {
        if (Outcome == SaveOutcome.Saved)
        {
            Store?.Store(job.CharacterId, job.State, job.ExpectedVersion + 1);
            if (job.Inventory is { } batch)
            {
                Store?.StoreItems(job.CharacterId, batch.Slots);
                if (batch.Gold is { } gold)
                    Store?.StoreGold(job.CharacterId, gold.GoldAfter);
            }
            if (job.Progression is { } progression)
                Store?.StoreProgression(job.CharacterId, progression);
            if (job.Skills is { } skills)
                Store?.StoreSkills(job.CharacterId, skills);
            if (job.Bystial is { } book)
                Store?.StoreBook(job.CharacterId, book);
            if (job.Quests is { } quests)
                Store?.StoreRuns(job.CharacterId, quests);
            if (job.Jobs is { } jobs)
                Store?.StoreJobs(job.CharacterId, jobs);
            if (job.Craft is { } craft)
                Store?.StoreCraft(job.CharacterId, craft);
        }
        _completions.Enqueue(new SaveCompletion(job.CharacterId, job.SaveId, Outcome, job.ExpectedVersion + 1));
    }
}
