using GameServer.Persistence;
using GameServer.Persistence.Migrations;
using GameServer.Tests.Support;

namespace GameServer.Tests.Persistence;

/// <summary>Accounts in the global database (R-2.1-01, R-2.1-03), against a real PostgreSQL.</summary>
public sealed class AccountRepositoryTests : IAsyncLifetime
{
    private readonly TestDatabases _databases = new();
    private readonly FakeClock _clock = new();
    private AccountRepository _repository = null!;

    public async Task InitializeAsync() =>
        _repository = new AccountRepository(await _databases.CreateMigratedAsync(DatabaseKind.Global), _clock);

    public async Task DisposeAsync() => await _databases.DisposeAsync();

    [Fact]
    public async Task FirstLoginCreatesTheAccountAndLaterLoginsReturnIt()
    {
        var created = await _repository.GetOrCreateBySteamIdAsync(76561198000000001, ownerSteamId: null);
        _clock.Advance(1_000);
        var again = await _repository.GetOrCreateBySteamIdAsync(76561198000000001, ownerSteamId: null);

        Assert.Equal(created, again);
        Assert.Equal(_clock.UtcNowMs - 1_000, created.CreatedAtUtcMs);
        Assert.Equal(created, await _repository.FindAsync(created.AccountId));
    }

    [Fact]
    public async Task ConcurrentFirstLoginsCreateOneAccount()
    {
        var accounts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => _repository.GetOrCreateBySteamIdAsync(76561198000000002, 76561198000000003)));

        Assert.Single(accounts.Select(a => a.AccountId).Distinct());
        Assert.Equal(76561198000000003, accounts[0].OwnerSteamId);
    }
}
