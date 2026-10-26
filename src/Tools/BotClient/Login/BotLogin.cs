using System.Net.Http.Headers;
using System.Text;
using Game.UI.Auth;
using Shared.Protocol.Auth;
using Shared.Util;

namespace BotClient.Login;

/// <summary><see cref="IAuthHttp"/> over <see cref="HttpClient"/>: what <c>UnityWebRequestAuthHttp</c> is in the game.</summary>
public sealed class HttpClientAuthHttp : IAuthHttp
{
    private readonly HttpClient _http;

    public HttpClientAuthHttp(HttpClient http)
    {
        _http = http;
    }

    public async Task<AuthHttpResponse> SendAsync(AuthHttpRequest request)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.JsonBody != null)
            message.Content = new StringContent(request.JsonBody, Encoding.UTF8, "application/json");
        if (request.BearerToken != null)
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.BearerToken);
        try
        {
            using var response = await _http.SendAsync(message).ConfigureAwait(false);
            return AuthHttpResponse.Answer((int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
        catch (HttpRequestException e)
        {
            return AuthHttpResponse.Failed(e.Message);
        }
        catch (TaskCanceledException e)
        {
            return AuthHttpResponse.Failed(e.Message);
        }
    }
}

/// <summary>What a bot needs to connect: the single-use ticket and the session key of one character (R-2.5-01, -02).</summary>
public sealed record BotTicket(long AccountId, long CharacterId, string CharacterName, byte[] Ticket, byte[] SessionKey);

/// <summary>Why a login failed: the Auth API's reason code (spec 9.10) and the call that gave it.</summary>
public sealed class BotLoginException(string step, string reason) : Exception($"{step}: {reason}")
{
    public string Step { get; } = step;
    public string Reason { get; } = reason;
}

/// <summary>
/// The login screens' flow without the screens (spec 9.10), through the Unity client's own <see cref="AuthApiClient"/>:
/// dev login <c>dev:&lt;n&gt;</c> (MMORPG_AUTH_IDENTITY=dev), a character of the wanted class (the first one there, or a
/// new one named <c>Bot</c> plus letters), then enter-world.
/// </summary>
public static class BotLogin
{
    private const int NameRetries = 5;
    private const int NameLetters = 8;

    public static async Task<BotTicket> LoginAsync(IAuthHttp http, string authUrl, int realmId, long devAccount, string classId, IRng rng)
    {
        var api = new AuthApiClient(http, authUrl);
        var login = await api.LoginAsync("dev:" + devAccount).ConfigureAwait(false);
        if (!login.Ok)
            throw new BotLoginException("login", login.Reason);

        var cards = login.Response!.Characters?.RealmId == realmId ? login.Response.Characters.Characters : null;
        if (cards == null)
        {
            var list = await api.ListCharactersAsync(realmId).ConfigureAwait(false);
            if (!list.Ok)
                throw new BotLoginException("characters", list.Reason);
            cards = list.Response!.Characters;
        }

        var card = cards.FirstOrDefault(c => c.ClassId == classId && !c.PendingDeletion);
        long characterId;
        string name;
        if (card != null)
        {
            characterId = card.CharacterId;
            name = card.Name;
        }
        else
        {
            (characterId, name) = await CreateAsync(api, realmId, classId, rng).ConfigureAwait(false);
        }

        var enter = await api.EnterWorldAsync(realmId, characterId).ConfigureAwait(false);
        if (!enter.Ok)
            throw new BotLoginException("enter", enter.Reason);
        return new BotTicket(login.Response.AccountId, characterId, name, enter.Response!.Ticket, enter.Response.SessionKey);
    }

    private static async Task<(long, string)> CreateAsync(AuthApiClient api, int realmId, string classId, IRng rng)
    {
        for (int attempt = 0; ; attempt++)
        {
            string name = NewName(rng);
            var request = new CreateCharacterRequest
            {
                RequestId = Guid.NewGuid(), Name = name, InitialClass = classId, Appearance = new CharacterAppearance(),
            };
            var created = await api.CreateCharacterAsync(realmId, request).ConfigureAwait(false);
            if (created.Ok)
                return (created.Response!.CharacterId, name);
            if (!created.Is(AuthReasonCode.NameTaken) || attempt + 1 >= NameRetries)
                throw new BotLoginException("create", created.Reason);
        }
    }

    /// <summary>Names allow ASCII letters only (CharacterCreationConfig): <c>Bot</c> plus random letters.</summary>
    public static string NewName(IRng rng)
    {
        var name = new StringBuilder("Bot", 3 + NameLetters);
        for (int i = 0; i < NameLetters; i++)
            name.Append((char)('a' + rng.NextInt(26)));
        return name.ToString();
    }
}
