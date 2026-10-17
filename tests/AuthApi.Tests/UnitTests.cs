using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AuthApi.Characters;
using AuthApi.Identity;
using AuthApi.Tests.Support;
using AuthApi.Tokens;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol.Auth;

namespace AuthApi.Tests;

public sealed class CharacterNameRulesTests
{
    private static readonly CharacterCreationConfig Config = new()
    {
        NameMinLength = 3,
        NameMaxLength = 8,
        NameAllowedCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
        NameBlockedWords = ["admin"],
        AllowedInitialClasses = ["warrior"],
        StartWorldId = "main",
    };

    private readonly CharacterNameRules _rules = new(Config);

    [Theory]
    [InlineData("Ari", true)]
    [InlineData("Abcdefgh", true)]
    [InlineData("Ar", false)]
    [InlineData("Abcdefghi", false)]
    [InlineData("Ar1a", false)]
    [InlineData("Ar_a", false)]
    [InlineData("Arа", false)] // Cyrillic a: looks the same, not allowed
    [InlineData("xADMINx", false)]
    [InlineData(null, false)]
    public void ChecksLengthCharactersAndBlockedWords(string? name, bool valid) => Assert.Equal(valid, _rules.IsValid(name));
}

public sealed class AccessTokenServiceTests
{
    private const int LifetimeMs = 60_000;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly FakeClock _clock = new();

    private AccessTokenService Service(byte keyId = 1, byte[]? secret = null) => new(keyId, secret ?? _secret, _clock, LifetimeMs);

    [Fact]
    public void RoundTripsTheAccountUntilExpiry()
    {
        var service = Service();
        var issued = service.Issue(1234);

        Assert.Equal(_clock.UtcNowMs + LifetimeMs, issued.ExpiresAtUtcMs);
        Assert.Equal(new AccessTokenCheck(AuthReasonCode.None, 1234), service.Validate(issued.Token));

        _clock.Advance(LifetimeMs - 1);
        Assert.True(service.Validate(issued.Token).Ok);
        _clock.Advance(1);
        Assert.Equal(AuthReasonCode.AuthTokenExpired, service.Validate(issued.Token).Reason);
    }

    [Fact]
    public void RefusesTokensFromAnotherKeyOrSecret()
    {
        var token = Service().Issue(1).Token;

        Assert.Equal(AuthReasonCode.AuthTokenInvalid, Service(keyId: 2).Validate(token).Reason);
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, Service(secret: RandomNumberGenerator.GetBytes(32)).Validate(token).Reason);
    }

    [Fact]
    public void RefusesEveryTamperedByte()
    {
        var service = Service();
        var bytes = Base64Url.DecodeFromChars(service.Issue(1).Token);
        for (var i = 0; i < bytes.Length; i++)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;
            Assert.Equal(AuthReasonCode.AuthTokenInvalid, service.Validate(Base64Url.EncodeToString(tampered)).Reason);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64 !")]
    [InlineData("AAAA")]
    public void RefusesMalformedTokens(string? token) =>
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, Service().Validate(token).Reason);

    [Fact]
    public void RefusesATokenThatOutlivesTheLifetime()
    {
        // Issued by a misconfigured issuer with a longer lifetime and the same secret.
        var token = new AccessTokenService(1, _secret, _clock, LifetimeMs * 2).Issue(1).Token;
        Assert.Equal(AuthReasonCode.AuthTokenInvalid, Service().Validate(token).Reason);
    }
}

public sealed class AuthApiSettingsTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        [AuthApiSettings.TicketSecretVariable] = Convert.ToBase64String(new byte[32]),
        [AuthApiSettings.TicketKeyIdVariable] = "3",
        [AuthApiSettings.AccessTokenSecretVariable] = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()),
        ["MMORPG_GLOBAL_DB"] = "Host=x",
        ["MMORPG_REALM_DB"] = "Host=y",
        ["MMORPG_REALM_ID"] = "1",
    };

    private static AuthApiSettings? Read(Dictionary<string, string?> values, out List<string> errors) =>
        AuthApiSettings.Read(name => values.GetValueOrDefault(name), out errors);

    [Fact]
    public void ReadsValidSettingsWithDefaults()
    {
        var settings = Read(Valid(), out var errors);

        Assert.Empty(errors);
        Assert.Equal(3, settings!.TicketKeyId);
        Assert.Equal(0, settings.AccessTokenKeyId);
        Assert.Equal(1, settings.RealmId);
        Assert.Equal(IdentityProviders.Steam, settings.IdentityMode);
        Assert.Equal(Path.Combine("data", "tables"), settings.DataDirectory);
        Assert.DoesNotContain("Host=", settings.ToString());
    }

    [Fact]
    public void ListsEveryMissingSetting()
    {
        Assert.Null(Read(new Dictionary<string, string?>(), out var errors));
        Assert.Contains(errors, e => e.Contains(AuthApiSettings.TicketSecretVariable));
        Assert.Contains(errors, e => e.Contains(AuthApiSettings.TicketKeyIdVariable));
        Assert.Contains(errors, e => e.Contains(AuthApiSettings.AccessTokenSecretVariable));
        Assert.Contains(errors, e => e.Contains("MMORPG_GLOBAL_DB"));
    }

    [Fact]
    public void RefusesShortSecretsAndASecretUsedTwice()
    {
        var values = Valid();
        values[AuthApiSettings.TicketSecretVariable] = Convert.ToBase64String(new byte[16]);
        Assert.Null(Read(values, out var errors));
        Assert.Contains(errors, e => e.Contains("at least 32 bytes"));

        values = Valid();
        values[AuthApiSettings.AccessTokenSecretVariable] = values[AuthApiSettings.TicketSecretVariable];
        Assert.Null(Read(values, out errors));
        Assert.Contains(errors, e => e.Contains("must differ"));
    }

    [Fact]
    public void RefusesAnUnknownIdentityProvider()
    {
        var values = Valid();
        values[AuthApiSettings.IdentityVariable] = "password";
        Assert.Null(Read(values, out var errors));
        Assert.Contains(errors, e => e.Contains(AuthApiSettings.IdentityVariable));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5080", false)]
    [InlineData("http://localhost:5080", false)]
    [InlineData("http://[::1]:5080", false)]
    [InlineData("https://0.0.0.0:443", false)]
    [InlineData("http://0.0.0.0:5080", true)]
    [InlineData("http://*:5080", true)]
    [InlineData("http://+:5080", true)]
    [InlineData("http://10.0.0.5:5080", true)]
    public void PlainHttpIsOnlyAllowedOnLoopback(string address, bool refused) =>
        Assert.Equal(refused, AuthApiApp.IsPlainHttpOffLoopback(address));
}

public sealed class AuthTablesTests
{
    private static CharacterCreationConfig ValidCreation() => new()
    {
        NameMinLength = 3,
        NameMaxLength = 16,
        NameAllowedCharacters = "abc",
        NameBlockedWords = ["admin"],
        AllowedInitialClasses = ["warrior", "mage"],
        StartWorldId = "main",
        StartLevel = 1,
        StartHp = 100,
    };

    private static List<string> Errors(CharacterCreationConfig row)
    {
        var errors = new RowErrors();
        row.Validate(errors);
        return errors.Items.Select(e => e.Field ?? "").ToList();
    }

    [Fact]
    public void AValidRowHasNoErrors() => Assert.Empty(Errors(ValidCreation()));

    [Fact]
    public void CrossFieldRulesAreChecked()
    {
        var row = ValidCreation();
        row.NameMinLength = 17;
        row.NameAllowedCharacters = "a a";
        row.NameBlockedWords = ["Admin", " gm", ""];
        row.AllowedInitialClasses = ["warrior", "warrior", ""];
        row.StartWorldId = "";

        var errors = Errors(row);

        Assert.Contains(nameof(CharacterCreationConfig.NameMinLength), errors);
        Assert.Equal(2, errors.Count(e => e == nameof(CharacterCreationConfig.NameAllowedCharacters))); // space, duplicate 'a'
        Assert.Equal(3, errors.Count(e => e == nameof(CharacterCreationConfig.NameBlockedWords)));
        Assert.Equal(2, errors.Count(e => e == nameof(CharacterCreationConfig.AllowedInitialClasses)));
        Assert.Contains(nameof(CharacterCreationConfig.StartWorldId), errors);
    }

    [Fact]
    public void EmptyListsAreRefused()
    {
        var row = ValidCreation();
        row.NameAllowedCharacters = "";
        row.AllowedInitialClasses = [];

        var errors = Errors(row);

        Assert.Contains(nameof(CharacterCreationConfig.NameAllowedCharacters), errors);
        Assert.Contains(nameof(CharacterCreationConfig.AllowedInitialClasses), errors);
    }

    [Fact]
    public void TheRepositoryTablesLoad()
    {
        var config = AuthConfig.Load(AuthApiFactory.TablesDirectory());

        Assert.Equal(4, config.Slots.SlotsPerRealm); // R-2.1-05
        Assert.Equal(["warrior", "archer", "mage", "assist"], config.Creation.AllowedInitialClasses);
    }
}

/// <summary>The dev login exists only behind the DevLogin build flag (spec 2.1). CI builds Release, where it must be absent.</summary>
public sealed class DevLoginBuildFlagTests
{
    private const string DevProviderType = "AuthApi.Identity.Dev.DevIdentityProvider";

#if DEV_LOGIN
    [Fact]
    public void TheDevLoginIsCompiledIn()
    {
        Assert.True(IdentityProviders.DevLoginCompiled);
        Assert.NotNull(typeof(IdentityProviders).Assembly.GetType(DevProviderType));
        Assert.True(IdentityProviders.IsAvailable(IdentityProviders.Dev, out _));
    }

    [Fact]
    public async Task DevLoginCreatesOrLoadsTheAccount()
    {
        await using var databases = new AuthTestDatabases();
        await databases.CreateAsync();
        await using var api = new AuthApiFactory(databases.GlobalConnectionString, databases.RealmConnectionString,
            s => s[AuthApiSettings.IdentityVariable] = IdentityProviders.Dev) { UseRealIdentity = true };
        var client = api.CreateClient();

        async Task<LoginResponse> Login(string ticket)
        {
            var response = await client.PostAsJsonAsync("/v1/login", new LoginRequest { Ticket = ticket }, AuthApiFactory.Json);
            return (await response.Content.ReadFromJsonAsync<LoginResponse>(AuthApiFactory.Json))!;
        }

        var first = await Login("dev:42");
        var second = await Login("dev:42");

        Assert.True(first.Ok);
        Assert.Equal(first.AccountId, second.AccountId);
        Assert.Equal(AuthReasonCode.InvalidTicket, (await Login("dev:-1")).ReasonCode);
        Assert.Equal(AuthReasonCode.InvalidTicket, (await Login("42")).ReasonCode);
    }
#else
    [Fact]
    public void TheDevLoginIsNotInThisBuild()
    {
        Assert.False(IdentityProviders.DevLoginCompiled);
        Assert.Null(typeof(IdentityProviders).Assembly.GetType(DevProviderType));
        Assert.False(IdentityProviders.IsAvailable(IdentityProviders.Dev, out var error));
        Assert.Contains("not compiled", error);
        Assert.Throws<InvalidOperationException>(() => IdentityProviders.Create(IdentityProviders.Dev));
    }
#endif

    [Fact]
    public async Task TheSteamStubRefusesEveryLogin()
    {
        var result = await new SteamIdentityProvider().VerifyAsync("abcd", CancellationToken.None);
        Assert.Equal(AuthReasonCode.ServerUnavailable, result.Reason);
    }
}
