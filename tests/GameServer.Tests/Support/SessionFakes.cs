using System.Collections.Concurrent;
using GameServer.Persistence;
using GameServer.Sessions;
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

    public Task<CharacterRecord?> FindBySlotAsync(long accountId, int slot)
    {
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

    /// <summary>Item stacks a login reads (backlog G), by character.</summary>
    public Dictionary<long, IReadOnlyList<ItemStackRow>> Items { get; } = new();

    public Task<IReadOnlyList<ItemStackRow>> LoadItemsAsync(long characterId)
    {
        lock (_lock)
        {
            return Task.FromResult(Items.TryGetValue(characterId, out var rows) ? rows : Array.Empty<ItemStackRow>());
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
                Store?.StoreItems(job.CharacterId, batch.Slots);
        }
        _completions.Enqueue(new SaveCompletion(job.CharacterId, job.SaveId, Outcome, job.ExpectedVersion + 1));
    }
}
