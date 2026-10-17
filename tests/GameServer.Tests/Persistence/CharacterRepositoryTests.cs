using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;
using Npgsql;

namespace GameServer.Tests.Persistence;

/// <summary>
/// Character persistence against a real PostgreSQL (R-2.1-05, R-7.1-04): create, load, optimistic concurrency,
/// double submit and retry, soft delete.
/// </summary>
public sealed class CharacterRepositoryTests : IAsyncLifetime
{
    private const int Realm = 1;
    private const long OneDayMs = 86_400_000;

    private static readonly CharacterState Spawn = new(1, 0, "main", 1_000, 2_000, 0, 0, 100, 50, 100);

    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private NpgsqlDataSource _realmDb = null!;
    private CharacterRepository _repository = null!;
    private long _nextAccount = 1;

    public async Task InitializeAsync()
    {
        _realmDb = await _databases.CreateMigratedAsync(DatabaseKind.Realm);
        _repository = new CharacterRepository(_realmDb, Realm, _clock);
    }

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    private NewCharacter NewRequest(long accountId, int slot = 0, string name = "Hero") =>
        new(accountId, slot, name, "warrior", Spawn, Guid.NewGuid());

    private async Task<CharacterRecord> CreateAsync(string name = "Hero", int slot = 0)
    {
        var result = await _repository.CreateAsync(NewRequest(_nextAccount++, slot, name));
        Assert.Equal(CreateCharacterStatus.Created, result.Status);
        return result.Character!;
    }

    [Fact]
    public async Task CreateThenLoadRoundTrips()
    {
        var created = await CreateAsync("Aria", slot: 2);

        var loaded = await _repository.LoadAsync(created.CharacterId);

        Assert.NotNull(loaded);
        Assert.Equal(created, loaded);
        Assert.Equal(Realm, loaded.RealmId);
        Assert.Equal(2, loaded.Slot);
        Assert.Equal("Aria", loaded.Name);
        Assert.Equal(Spawn, loaded.State);
        Assert.Equal(1, loaded.Version);
        Assert.Equal(_clock.UtcNowMs, loaded.CreatedAtUtcMs);
        Assert.False(loaded.IsPendingDeletion);
    }

    [Fact]
    public async Task LoadIsScopedToTheRealm()
    {
        var created = await CreateAsync();
        var otherRealm = new CharacterRepository(_realmDb, Realm + 1, _clock);

        Assert.Null(await otherRealm.LoadAsync(created.CharacterId));
        Assert.Null(await _repository.LoadAsync(created.CharacterId + 1000));
    }

    [Fact]
    public async Task NamesAreUniquePerRealmIgnoringCase()
    {
        await CreateAsync("Kael");

        var clash = await _repository.CreateAsync(NewRequest(_nextAccount++, name: "kAEL"));
        Assert.Equal(CreateCharacterStatus.NameTaken, clash.Status);
        Assert.Null(clash.Character);

        var otherRealm = new CharacterRepository(_realmDb, Realm + 1, _clock);
        Assert.Equal(CreateCharacterStatus.Created, (await otherRealm.CreateAsync(NewRequest(_nextAccount++, name: "Kael"))).Status);
    }

    [Fact]
    public async Task SlotsAreUniquePerAccount()
    {
        const long account = 500;
        Assert.Equal(CreateCharacterStatus.Created, (await _repository.CreateAsync(NewRequest(account, 0, "First"))).Status);
        Assert.Equal(CreateCharacterStatus.SlotTaken, (await _repository.CreateAsync(NewRequest(account, 0, "Second"))).Status);
        Assert.Equal(CreateCharacterStatus.Created, (await _repository.CreateAsync(NewRequest(account, 1, "Second"))).Status);

        var list = await _repository.ListByAccountAsync(account);
        Assert.Equal(["First", "Second"], list.Select(c => c.Name));
    }

    [Fact]
    public async Task CreateDoubleSubmitReturnsTheSameCharacter()
    {
        var request = NewRequest(_nextAccount++, name: "Twice");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _repository.CreateAsync(request)));

        Assert.All(results, r => Assert.Equal(CreateCharacterStatus.Created, r.Status));
        Assert.Single(results.Select(r => r.Character!.CharacterId).Distinct());
        Assert.Single(await _repository.ListByAccountAsync(request.AccountId));
    }

    [Fact]
    public async Task CreateRequestIdOfAnotherAccountIsRefused()
    {
        var request = NewRequest(_nextAccount++, name: "Owner");
        await _repository.CreateAsync(request);

        var reused = await _repository.CreateAsync(request with { AccountId = _nextAccount++, Name = "Thief" });

        Assert.Equal(CreateCharacterStatus.RequestIdReused, reused.Status);
        Assert.Null(reused.Character);
    }

    [Fact]
    public async Task SaveWritesStateAndBumpsTheVersion()
    {
        var created = await CreateAsync();
        var moved = created.State with { PosXCm = 5_000, Exp = 120, Hp = 80 };
        _clock.Advance(1_000);

        var result = await _repository.SaveAsync(created.CharacterId, moved, created.Version, Guid.NewGuid());

        Assert.Equal(new SaveResult(SaveStatus.Saved, 2), result);
        var loaded = await _repository.LoadAsync(created.CharacterId);
        Assert.Equal(moved, loaded!.State);
        Assert.Equal(2, loaded.Version);
        Assert.Equal(_clock.UtcNowMs, loaded.UpdatedAtUtcMs);
    }

    [Fact]
    public async Task StaleVersionIsAConflictAndWritesNothing()
    {
        var created = await CreateAsync();
        await _repository.SaveAsync(created.CharacterId, created.State with { Exp = 10 }, 1, Guid.NewGuid());

        var stale = await _repository.SaveAsync(created.CharacterId, created.State with { Exp = 999 }, 1, Guid.NewGuid());

        Assert.Equal(new SaveResult(SaveStatus.VersionConflict, 2), stale);
        Assert.Equal(10, (await _repository.LoadAsync(created.CharacterId))!.State.Exp);
    }

    [Fact]
    public async Task ConcurrentSavesAtTheSameVersionHaveExactlyOneWinner()
    {
        var created = await CreateAsync();

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i =>
            _repository.SaveAsync(created.CharacterId, created.State with { Exp = i }, created.Version, Guid.NewGuid())));

        var winner = Assert.Single(results, r => r.Status == SaveStatus.Saved);
        Assert.Equal(2, winner.Version);
        Assert.All(results.Where(r => r.Status != SaveStatus.Saved), r => Assert.Equal(SaveStatus.VersionConflict, r.Status));
        var loaded = await _repository.LoadAsync(created.CharacterId);
        Assert.Equal(2, loaded!.Version);
    }

    [Fact]
    public async Task SaveDoubleSubmitIsRecognisedNotAConflict()
    {
        var created = await CreateAsync();
        var saveId = Guid.NewGuid();
        var state = created.State with { Exp = 42 };

        var first = await _repository.SaveAsync(created.CharacterId, state, created.Version, saveId);
        var retry = await _repository.SaveAsync(created.CharacterId, state, created.Version, saveId);

        Assert.Equal(new SaveResult(SaveStatus.Saved, 2), first);
        Assert.Equal(first, retry);
        Assert.Equal(2, (await _repository.LoadAsync(created.CharacterId))!.Version);
    }

    [Fact]
    public async Task SaveRetryAfterANewerWriteIsAConflict()
    {
        var created = await CreateAsync();
        var saveId = Guid.NewGuid();
        await _repository.SaveAsync(created.CharacterId, created.State, 1, saveId);
        await _repository.SaveAsync(created.CharacterId, created.State, 2, Guid.NewGuid());

        var lateRetry = await _repository.SaveAsync(created.CharacterId, created.State, 1, saveId);

        Assert.Equal(new SaveResult(SaveStatus.VersionConflict, 3), lateRetry);
    }

    [Fact]
    public async Task SaveOfAMissingCharacterIsNotFound()
    {
        var result = await _repository.SaveAsync(123_456, Spawn, 1, Guid.NewGuid());
        Assert.Equal(SaveStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task SoftDeleteKeepsTheRowAndBlocksSaves()
    {
        var created = await CreateAsync("Gone", slot: 3);
        var purgeAfter = _clock.UtcNowMs + 7 * OneDayMs;

        Assert.Equal(DeleteStatus.Deleted, await _repository.SoftDeleteAsync(created.CharacterId, created.AccountId, purgeAfter));

        var loaded = await _repository.LoadAsync(created.CharacterId);
        Assert.True(loaded!.IsPendingDeletion);
        Assert.Equal(_clock.UtcNowMs, loaded.DeletedAtUtcMs);
        Assert.Equal(purgeAfter, loaded.PurgeAfterUtcMs);
        Assert.Equal(2, loaded.Version);
        Assert.Single(await _repository.ListByAccountAsync(created.AccountId)); // still shown as a pending card

        Assert.Equal(new SaveResult(SaveStatus.Deleted, 2), await _repository.SaveAsync(created.CharacterId, Spawn, 2, Guid.NewGuid()));
        Assert.Equal(SaveStatus.Deleted, (await _repository.SaveAsync(created.CharacterId, Spawn, 1, Guid.NewGuid())).Status);
        Assert.Equal(DeleteStatus.AlreadyDeleted, await _repository.SoftDeleteAsync(created.CharacterId, created.AccountId, purgeAfter));

        // Name and slot stay reserved during the grace period.
        Assert.Equal(CreateCharacterStatus.NameTaken, (await _repository.CreateAsync(NewRequest(_nextAccount++, name: "Gone"))).Status);
        Assert.Equal(CreateCharacterStatus.SlotTaken,
            (await _repository.CreateAsync(NewRequest(created.AccountId, slot: 3, name: "Other"))).Status);
    }

    [Fact]
    public async Task SoftDeleteChecksOwnership()
    {
        var created = await CreateAsync();

        var status = await _repository.SoftDeleteAsync(created.CharacterId, created.AccountId + 1, _clock.UtcNowMs + OneDayMs);

        Assert.Equal(DeleteStatus.NotFound, status);
        Assert.False((await _repository.LoadAsync(created.CharacterId))!.IsPendingDeletion);
    }

    [Fact]
    public async Task WorkerSavesAndReportsThroughTheCompletionQueue()
    {
        var created = await CreateAsync();
        var state = created.State with { Exp = 77 };
        await using var worker = new PersistenceWorker(_repository);

        worker.Enqueue(new SaveJob(created.CharacterId, state, created.Version, Guid.NewGuid(), SaveReason.Logout));

        SaveCompletion completion;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!worker.TryDequeue(out completion))
        {
            Assert.True(DateTime.UtcNow < deadline, "save did not complete");
            await Task.Delay(10);
        }

        Assert.Equal(SaveOutcome.Saved, completion.Outcome);
        Assert.Equal(2, completion.Version);
        Assert.Equal(77, (await _repository.LoadAsync(created.CharacterId))!.State.Exp);
    }
}
