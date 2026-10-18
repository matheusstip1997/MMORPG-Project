using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthApi.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Shared.Protocol.Auth;
using Shared.Util;

namespace AuthApi.Tests.Support;

/// <summary>
/// Hosts the Auth API in memory with test settings: fresh random secrets, the test databases, the repository's
/// data/tables, and a fake clock. Unless <see cref="UseRealIdentity"/> is set, logins go through
/// <see cref="FakeIdentityProvider"/>, so the tests run the same in Debug and Release.
/// </summary>
public sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    public const int Realm = 1;
    public const byte TicketKeyId = 7;

    public static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly Dictionary<string, string> _settings;

    public AuthApiFactory(string globalDb, string realmDb, Action<Dictionary<string, string>>? changeSettings = null)
    {
        _settings = new Dictionary<string, string>
        {
            [AuthApiSettings.TicketSecretVariable] = Convert.ToBase64String(TicketSecret),
            [AuthApiSettings.TicketKeyIdVariable] = TicketKeyId.ToString(),
            [AuthApiSettings.AccessTokenSecretVariable] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["MMORPG_GLOBAL_DB"] = globalDb,
            ["MMORPG_REALM_DB"] = realmDb,
            ["MMORPG_REALM_ID"] = Realm.ToString(),
            [AuthApiSettings.DataDirectoryVariable] = TablesDirectory(),
        };
        changeSettings?.Invoke(_settings);
    }

    public byte[] TicketSecret { get; } = RandomNumberGenerator.GetBytes(32);

    public FakeClock Clock { get; } = new();

    public bool UseRealIdentity { get; init; }

    public static string TablesDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Mmorpg.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "data", "tables");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClock>(Clock);
            if (!UseRealIdentity)
                services.AddSingleton<IIdentityProvider, FakeIdentityProvider>();
        });
    }

    /// <summary>Logs in as fake SteamID <paramref name="steamId"/> and returns a client that sends the access token.</summary>
    public async Task<(HttpClient Client, LoginResponse Login)> LoginAsync(long steamId)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/v1/login", new LoginRequest { Ticket = FakeIdentityProvider.TicketFor(steamId) }, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return (client, login);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}

/// <summary>Accepts <c>test:&lt;steamId&gt;</c>, and <c>test:&lt;steamId&gt;:&lt;ownerSteamId&gt;</c> for Family Sharing.</summary>
public sealed class FakeIdentityProvider : IIdentityProvider
{
    public static string TicketFor(long steamId) => $"test:{steamId}";

    public Task<IdentityResult> VerifyAsync(string ticket, CancellationToken ct)
    {
        var parts = ticket.Split(':');
        if (parts.Length is 2 or 3 && parts[0] == "test" && long.TryParse(parts[1], out var steamId))
        {
            long? owner = parts.Length == 3 && long.TryParse(parts[2], out var o) ? o : null;
            return Task.FromResult(IdentityResult.Verified(steamId, owner));
        }
        return Task.FromResult(IdentityResult.Refused(AuthReasonCode.InvalidTicket));
    }
}
