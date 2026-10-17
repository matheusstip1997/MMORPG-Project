using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AuthApi.Tests.Support;
using AuthApi.Tokens;
using GameServer.Net.Tickets;
using Microsoft.Extensions.DependencyInjection;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;

namespace AuthApi.Tests;

/// <summary>
/// The Auth API end to end against a real PostgreSQL (R-2.1-01, R-2.1-03, R-2.1-05, R-2.5-01, R-2.5-02, spec 9.10):
/// login, character list, create with every refusal, double submit, concurrent creates, soft delete, enter world.
/// </summary>
public sealed class AuthApiIntegrationTests : IAsyncLifetime
{
    private readonly AuthTestDatabases _databases = new();
    private AuthApiFactory _api = null!;
    private AuthConfig _config = null!;
    private long _nextSteamId = 76_561_198_000_000_000;

    public async Task InitializeAsync()
    {
        await _databases.CreateAsync();
        _api = new AuthApiFactory(_databases.GlobalConnectionString, _databases.RealmConnectionString);
        _config = _api.Services.GetRequiredService<AuthConfig>();
    }

    public async Task DisposeAsync()
    {
        await _api.DisposeAsync();
        await _databases.DisposeAsync();
    }

    private string Characters => $"/v1/realms/{AuthApiFactory.Realm}/characters";

    private Task<(HttpClient Client, LoginResponse Login)> LoginNewAsync() => _api.LoginAsync(_nextSteamId++);

    private static CreateCharacterRequest Request(string name, string initialClass = "warrior") =>
        new() { RequestId = Guid.NewGuid(), Name = name, InitialClass = initialClass };

    private async Task<(HttpStatusCode Status, CreateCharacterResult Result)> CreateAsync(HttpClient client, CreateCharacterRequest request)
    {
        var response = await client.PostAsJsonAsync(Characters, request, AuthApiFactory.Json);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<CreateCharacterResult>(AuthApiFactory.Json))!);
    }

    private async Task<CharacterCard> CreateOkAsync(HttpClient client, string name, string initialClass = "warrior")
    {
        var (status, result) = await CreateAsync(client, Request(name, initialClass));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result.Ok, result.ReasonCode.ToString());
        return result.Character!;
    }

    private static async Task<(HttpStatusCode Status, T Body)> SendAsync<T>(HttpClient client, HttpMethod method, string path)
    {
        var response = await client.SendAsync(new HttpRequestMessage(method, path));
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<T>(AuthApiFactory.Json))!);
    }

    private Task<(HttpStatusCode Status, CharacterListResponse Body)> ListAsync(HttpClient client) =>
        SendAsync<CharacterListResponse>(client, HttpMethod.Get, Characters);

    private Task<(HttpStatusCode Status, EnterWorldResult Body)> EnterAsync(HttpClient client, long characterId) =>
        SendAsync<EnterWorldResult>(client, HttpMethod.Post, $"{Characters}/{characterId}/enter");

    private Task<(HttpStatusCode Status, DeleteCharacterResult Body)> DeleteAsync(HttpClient client, long characterId) =>
        SendAsync<DeleteCharacterResult>(client, HttpMethod.Delete, $"{Characters}/{characterId}");

    // ---- Login (R-2.1-01)

    [Fact]
    public async Task LoginCreatesTheAccountOnceAndReturnsAnEmptyCharacterList()
    {
        var (_, first) = await _api.LoginAsync(42);
        var (_, second) = await _api.LoginAsync(42);
        var (_, other) = await _api.LoginAsync(43);

        Assert.True(first.Ok);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.NotEqual(first.AccountId, other.AccountId);
        Assert.False(string.IsNullOrEmpty(first.AccessToken));
        Assert.Equal(_api.Clock.UtcNowMs + _config.Limits.AccessTokenLifetimeMs, first.AccessTokenExpiresAtUtcMs);

        var characters = first.Characters!;
        Assert.True(characters.Ok);
        Assert.Equal(AuthApiFactory.Realm, characters.RealmId);
        Assert.Equal(_config.Slots.SlotsPerRealm, characters.SlotsPerRealm);
        Assert.Empty(characters.Characters);
    }

    [Fact]
    public async Task LoginStoresTheFamilySharingOwner()
    {
        var client = _api.CreateClient();
        var response = await client.PostAsJsonAsync("/v1/login", new LoginRequest { Ticket = "test:500:400" }, AuthApiFactory.Json);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(AuthApiFactory.Json))!;

        var account = await _api.Services.GetRequiredService<GameServer.Persistence.AccountRepository>().FindAsync(login.AccountId);
        Assert.Equal(500, account!.SteamId);
        Assert.Equal(400, account.OwnerSteamId);
    }

    [Theory]
    [InlineData("""{"ticket":"not-a-test-ticket"}""", HttpStatusCode.Unauthorized, AuthReasonCode.InvalidTicket)]
    [InlineData("""{"ticket":""}""", HttpStatusCode.BadRequest, AuthReasonCode.RequestInvalid)]
    [InlineData("""{}""", HttpStatusCode.BadRequest, AuthReasonCode.RequestInvalid)]
    [InlineData("""{"ticket":""", HttpStatusCode.BadRequest, AuthReasonCode.RequestInvalid)]
    [InlineData("""[1,2]""", HttpStatusCode.BadRequest, AuthReasonCode.RequestInvalid)]
    public async Task BadLoginsAreRefusedWithAReason(string body, HttpStatusCode status, AuthReasonCode reason)
    {
        var client = _api.CreateClient();
        var response = await client.PostAsync("/v1/login", new StringContent(body, Encoding.UTF8, "application/json"));
        var result = (await response.Content.ReadFromJsonAsync<LoginResponse>(AuthApiFactory.Json))!;

        Assert.Equal(status, response.StatusCode);
        Assert.False(result.Ok);
        Assert.Equal(reason, result.ReasonCode);
        Assert.Equal("", result.AccessToken);
    }

    // ---- Access token

    [Fact]
    public async Task CallsWithoutAValidTokenAreRefused()
    {
        var client = _api.CreateClient();
        var (status, body) = await ListAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, body.ReasonCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "garbage");
        (status, body) = await ListAsync(client);
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, body.ReasonCode);

        // A token from another Auth API (another secret) is not accepted.
        await using var otherApi = new AuthApiFactory(_databases.GlobalConnectionString, _databases.RealmConnectionString);
        var (otherClient, _) = await otherApi.LoginAsync(42);
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(otherClient)).Status);
        var stolen = _api.CreateClient();
        stolen.DefaultRequestHeaders.Authorization = otherClient.DefaultRequestHeaders.Authorization;
        (status, body) = await ListAsync(stolen);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, body.ReasonCode);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefused()
    {
        var (client, _) = await LoginNewAsync();
        Assert.Equal(HttpStatusCode.OK, (await ListAsync(client)).Status);

        _api.Clock.Advance(_config.Limits.AccessTokenLifetimeMs);

        var (status, body) = await ListAsync(client);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(AuthReasonCode.AuthTokenExpired, body.ReasonCode);
    }

    [Fact]
    public async Task AnotherRealmIsUnknown()
    {
        var (client, _) = await LoginNewAsync();

        var (status, body) = await SendAsync<CharacterListResponse>(client, HttpMethod.Get, "/v1/realms/2/characters");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal(AuthReasonCode.RealmUnknown, body.ReasonCode);
    }

    // ---- Create (spec 9.10)

    [Fact]
    public async Task CreateFillsSlotsInOrderAndListShowsTheCards()
    {
        var (client, _) = await LoginNewAsync();

        var aria = await CreateOkAsync(client, "Aria", "mage");
        var bran = await CreateOkAsync(client, "Bran", "archer");

        Assert.Equal(0, aria.Slot);
        Assert.Equal(1, bran.Slot);
        Assert.Equal("mage", aria.ClassId);
        Assert.Equal(_config.Creation.StartLevel, aria.Level);
        Assert.False(aria.PendingDeletion);

        var (status, list) = await ListAsync(client);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(_api.Clock.UtcNowMs, list.ServerTimeUtcMs);
        Assert.Equal(new[] { "Aria", "Bran" }, list.Characters.Select(c => c.Name));
        Assert.Equal(aria.CharacterId, list.Characters[0].CharacterId);

        // The character starts with the configured state.
        var record = await _api.Services.GetRequiredService<GameServer.Persistence.CharacterRepository>().LoadAsync(aria.CharacterId);
        Assert.Equal(_config.Creation.StartWorldId, record!.State.WorldId);
        Assert.Equal(_config.Creation.StartHp, record.State.Hp);
        Assert.Equal(_config.StartingValues.StaminaPool, record.State.Stamina);
    }

    [Theory]
    [InlineData("Al")]                  // too short
    [InlineData("Abcdefghijklmnopq")]   // 17, too long
    [InlineData("Ar ia")]               // space
    [InlineData(" Aria")]
    [InlineData("Aria1")]               // digit
    [InlineData("Ária")]                // not in the allowed characters
    [InlineData("TheAdminX")]           // blocked word, any case
    [InlineData("")]
    public async Task InvalidNamesAreRefused(string name)
    {
        var (client, _) = await LoginNewAsync();

        var (status, result) = await CreateAsync(client, Request(name));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(AuthReasonCode.NameInvalid, result.ReasonCode);
        Assert.Equal(0, result.CharacterId);
    }

    [Fact]
    public async Task NamesAtTheLengthLimitsAreAccepted()
    {
        var (client, _) = await LoginNewAsync();
        await CreateOkAsync(client, new string('a', _config.Creation.NameMinLength));
        await CreateOkAsync(client, new string('b', _config.Creation.NameMaxLength));
    }

    [Theory]
    [InlineData("paladin")]
    [InlineData("Warrior")]  // class IDs are exact
    [InlineData("")]
    public async Task ClassesNotInTheListAreRefused(string initialClass)
    {
        var (client, _) = await LoginNewAsync();

        var (status, result) = await CreateAsync(client, Request("Aria", initialClass));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(AuthReasonCode.ClassNotAllowed, result.ReasonCode);
    }

    [Fact]
    public async Task AppearanceOutsideTheConfigIsRefused()
    {
        var (client, _) = await LoginNewAsync();

        var tooBig = Request("Aria");
        tooBig.Appearance = new CharacterAppearance { Version = _config.Creation.AppearanceVersion, Data = new byte[_config.Creation.AppearanceMaxBytes + 1] };
        var wrongVersion = Request("Aria");
        wrongVersion.Appearance = new CharacterAppearance { Version = _config.Creation.AppearanceVersion + 1 };

        foreach (var request in new[] { tooBig, wrongVersion })
        {
            var (status, result) = await CreateAsync(client, request);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(AuthReasonCode.AppearanceInvalid, result.ReasonCode);
        }

        Assert.Empty((await ListAsync(client)).Body.Characters);
    }

    [Fact]
    public async Task NamesAreUniquePerRealmIgnoringCase()
    {
        var (first, _) = await LoginNewAsync();
        var (second, _) = await LoginNewAsync();
        await CreateOkAsync(first, "Aria");

        var (status, result) = await CreateAsync(second, Request("aRIA"));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(AuthReasonCode.NameTaken, result.ReasonCode);
    }

    [Fact]
    public async Task AFifthCharacterHasNoFreeSlot()
    {
        var (client, _) = await LoginNewAsync();
        var names = new[] { "Aria", "Bran", "Cora", "Dain", "Eira", "Finn" };
        for (var i = 0; i < _config.Slots.SlotsPerRealm; i++)
            await CreateOkAsync(client, names[i]);

        var (status, result) = await CreateAsync(client, Request(names[_config.Slots.SlotsPerRealm]));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(AuthReasonCode.NoFreeSlot, result.ReasonCode);
    }

    [Fact]
    public async Task MalformedCreateRequestsAreInvalid()
    {
        var (client, _) = await LoginNewAsync();

        var noRequestId = Request("Aria");
        noRequestId.RequestId = Guid.Empty;
        var (status, result) = await CreateAsync(client, noRequestId);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(AuthReasonCode.RequestInvalid, result.ReasonCode);

        var response = await client.PostAsync(Characters, new StringContent("""{"name":5}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(AuthReasonCode.RequestInvalid, (await response.Content.ReadFromJsonAsync<AuthResponse>(AuthApiFactory.Json))!.ReasonCode);

        var badId = await SendAsync<AuthResponse>(client, HttpMethod.Post, $"{Characters}/abc/enter");
        Assert.Equal(HttpStatusCode.BadRequest, badId.Status);
        Assert.Equal(AuthReasonCode.RequestInvalid, badId.Body.ReasonCode);
    }

    [Fact]
    public async Task ADatabaseOutageIsServerUnavailable()
    {
        await using var broken = new AuthApiFactory(_databases.GlobalConnectionString, _databases.MissingDatabaseConnectionString);
        var token = broken.Services.GetRequiredService<AccessTokenService>().Issue(accountId: 1).Token;
        var client = broken.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (status, result) = await CreateAsync(client, Request("Aria"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(AuthReasonCode.ServerUnavailable, result.ReasonCode);
    }

    // ---- Idempotency and concurrency (architecture rule 5, R-2.1-05)

    [Fact]
    public async Task ADoubleSubmitReturnsTheSameCharacter()
    {
        var (client, _) = await LoginNewAsync();
        var request = Request("Aria");

        var (_, first) = await CreateAsync(client, request);
        var (status, second) = await CreateAsync(client, request);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first.CharacterId, second.CharacterId);
        Assert.Single((await ListAsync(client)).Body.Characters);
    }

    [Fact]
    public async Task ARetryOfTheCreateThatFilledTheLastSlotStillReturnsItsCharacter()
    {
        var (client, _) = await LoginNewAsync();
        var names = new[] { "Aria", "Bran", "Cora" };
        foreach (var name in names.Take(_config.Slots.SlotsPerRealm - 1))
            await CreateOkAsync(client, name);
        var last = Request("Dain");
        var (_, first) = await CreateAsync(client, last);

        var (status, retry) = await CreateAsync(client, last);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first.CharacterId, retry.CharacterId);
    }

    [Fact]
    public async Task ARequestIdUsedByAnotherAccountIsRefused()
    {
        var (first, _) = await LoginNewAsync();
        var (second, _) = await LoginNewAsync();
        var request = Request("Aria");
        await CreateAsync(first, request);

        request.Name = "Bran";
        var (status, result) = await CreateAsync(second, request);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(AuthReasonCode.RequestInvalid, result.ReasonCode);
    }

    [Fact]
    public async Task ConcurrentCreatesOfOneNameLetExactlyOneWin()
    {
        var clients = new List<HttpClient>();
        for (var i = 0; i < 16; i++)
            clients.Add((await LoginNewAsync()).Client);

        var results = await Task.WhenAll(clients.Select(c => CreateAsync(c, Request("Aria"))));

        Assert.Single(results, r => r.Result.Ok);
        Assert.All(results.Where(r => !r.Result.Ok), r => Assert.Equal(AuthReasonCode.NameTaken, r.Result.ReasonCode));
    }

    [Fact]
    public async Task ConcurrentCreatesOnOneAccountGetDistinctSlots()
    {
        var (client, _) = await LoginNewAsync();
        var names = new[] { "Aria", "Bran", "Cora", "Dain", "Eira", "Finn" };

        var results = await Task.WhenAll(names.Select(n => CreateAsync(client, Request(n))));

        var created = results.Where(r => r.Result.Ok).Select(r => r.Result.Character!).ToList();
        Assert.Equal(_config.Slots.SlotsPerRealm, created.Count);
        Assert.Equal(Enumerable.Range(0, _config.Slots.SlotsPerRealm), created.Select(c => c.Slot).Order());
        Assert.All(results.Where(r => !r.Result.Ok), r => Assert.Equal(AuthReasonCode.NoFreeSlot, r.Result.ReasonCode));
    }

    // ---- Soft delete (R-2.1-05, R-9.10-04)

    [Fact]
    public async Task SoftDeleteKeepsTheCardSlotAndNameDuringTheGracePeriod()
    {
        var (client, _) = await LoginNewAsync();
        var aria = await CreateOkAsync(client, "Aria");
        var expectedPurge = _api.Clock.UtcNowMs + _config.Slots.DeleteGraceMs;

        var (status, deleted) = await DeleteAsync(client, aria.CharacterId);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expectedPurge, deleted.PurgeAfterUtcMs);

        // Deleting again is not an error and keeps the first grace period.
        _api.Clock.Advance(1_000);
        var (againStatus, again) = await DeleteAsync(client, aria.CharacterId);
        Assert.Equal(HttpStatusCode.OK, againStatus);
        Assert.Equal(expectedPurge, again.PurgeAfterUtcMs);

        var card = Assert.Single((await ListAsync(client)).Body.Characters);
        Assert.True(card.PendingDeletion);
        Assert.Equal(expectedPurge, card.PurgeAfterUtcMs);

        // The slot stays used and the name stays reserved.
        Assert.Equal(1, (await CreateOkAsync(client, "Bran")).Slot);
        var (other, _) = await LoginNewAsync();
        Assert.Equal(AuthReasonCode.NameTaken, (await CreateAsync(other, Request("Aria"))).Result.ReasonCode);

        var (enterStatus, enter) = await EnterAsync(client, aria.CharacterId);
        Assert.Equal(HttpStatusCode.Conflict, enterStatus);
        Assert.Equal(AuthReasonCode.CharacterPendingDeletion, enter.ReasonCode);
    }

    [Fact]
    public async Task OnlyTheOwnerCanDeleteOrEnter()
    {
        var (owner, _) = await LoginNewAsync();
        var (other, _) = await LoginNewAsync();
        var aria = await CreateOkAsync(owner, "Aria");

        var (deleteStatus, delete) = await DeleteAsync(other, aria.CharacterId);
        var (enterStatus, enter) = await EnterAsync(other, aria.CharacterId);
        var (missingStatus, missing) = await DeleteAsync(owner, 999_999);

        Assert.Equal(HttpStatusCode.NotFound, deleteStatus);
        Assert.Equal(AuthReasonCode.CharacterNotFound, delete.ReasonCode);
        Assert.Equal(HttpStatusCode.NotFound, enterStatus);
        Assert.Equal(AuthReasonCode.CharacterNotFound, enter.ReasonCode);
        Assert.Equal(HttpStatusCode.NotFound, missingStatus);
        Assert.Equal(AuthReasonCode.CharacterNotFound, missing.ReasonCode);
        Assert.False(Assert.Single((await ListAsync(owner)).Body.Characters).PendingDeletion);
    }

    // ---- Enter world (R-2.5-01, R-2.5-02)

    [Fact]
    public async Task EnterWorldIssuesATicketTheGameServerAccepts()
    {
        var (client, login) = await LoginNewAsync();
        await CreateOkAsync(client, "Aria");
        var bran = await CreateOkAsync(client, "Bran");

        var (status, entered) = await EnterAsync(client, bran.CharacterId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(entered.Ok);
        Assert.Equal(SessionTicket.SessionKeySize, entered.SessionKey.Length);
        Assert.True(entered.Ticket.Length <= ConnectRequest.MaxTicketLength);
        var lifetime = _config.NetSecurity.TicketLifetimeMs;
        Assert.Equal(_api.Clock.UtcNowMs + lifetime, entered.TicketExpiresAtUtcMs);

        // The game server's own validator, with the shared secret (R-2.5-05) and its config.
        var keys = new TicketKeyRing().Add(AuthApiFactory.TicketKeyId, _api.TicketSecret);
        var validator = new TicketValidator(keys, new InMemoryNonceCache(_api.Clock), _api.Clock, AuthApiFactory.Realm, lifetime);
        var validation = validator.Validate(entered.Ticket);

        Assert.True(validation.Ok, validation.Reason.ToString());
        Assert.Equal(login.AccountId, validation.Ticket!.AccountId);
        Assert.Equal(AuthApiFactory.Realm, validation.Ticket.RealmId);
        Assert.Equal(bran.Slot, validation.Ticket.Slot);
        Assert.Equal(entered.SessionKey, validation.SessionKey);

        // Single use (R-2.5-01).
        Assert.Equal(ConnectRejectReason.InvalidTicket, validator.Validate(entered.Ticket).Reason);
        // Each enter issues a fresh ticket and session key.
        var (_, again) = await EnterAsync(client, bran.CharacterId);
        Assert.NotEqual(entered.SessionKey, again.SessionKey);
        Assert.True(validator.Validate(again.Ticket).Ok);
    }
}
