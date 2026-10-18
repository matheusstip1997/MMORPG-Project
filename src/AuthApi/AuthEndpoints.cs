using AuthApi.Characters;
using AuthApi.Identity;
using AuthApi.Tokens;
using GameServer.Persistence;
using Shared.Protocol.Auth;
using CreateCharacterResult = Shared.Protocol.Auth.CreateCharacterResult;

namespace AuthApi;

/// <summary>
/// HTTP endpoints (spec 2.1, 9.10). Every response is an <see cref="AuthResponse"/> with ok and reasonCode; the status
/// code follows the reason. Calls after login need "Authorization: Bearer &lt;accessToken&gt;".
/// </summary>
public static class AuthEndpoints
{
    private const string AccountIdItem = "AuthApi.AccountId";

    public static void Map(WebApplication app)
    {
        app.MapPost("/v1/login", LoginAsync);

        var realm = app.MapGroup("/v1/realms/{realmId}").AddEndpointFilter(RequireAccountInRealm);
        realm.MapGet("/characters", ListAsync);
        realm.MapPost("/characters", CreateAsync);
        realm.MapDelete("/characters/{characterId}", DeleteAsync);
        realm.MapPost("/characters/{characterId}/enter", EnterWorldAsync);
    }

    /// <summary>Verifies the login ticket, creates or loads the account (R-2.1-01), and returns a token and the character list.</summary>
    private static async Task<IResult> LoginAsync(
        LoginRequest? request, IIdentityProvider identity, AccountRepository accounts, AccessTokenService tokens,
        CharacterService characters, CancellationToken ct)
    {
        if (request == null || string.IsNullOrEmpty(request.Ticket))
            return Respond(new LoginResponse { ReasonCode = AuthReasonCode.RequestInvalid });

        var verified = await identity.VerifyAsync(request.Ticket, ct);
        if (!verified.Ok)
            return Respond(new LoginResponse { ReasonCode = verified.Reason });

        // TODO(bans): refuse when the account or its Family Sharing owner is banned (R-2.1-02). The bans table is not in
        // the global schema yet; the owner SteamID is stored on the account for it.
        var account = await accounts.GetOrCreateBySteamIdAsync(verified.SteamId, verified.OwnerSteamId, ct);
        var token = tokens.Issue(account.AccountId);
        return Respond(new LoginResponse
        {
            Ok = true,
            AccessToken = token.Token,
            AccessTokenExpiresAtUtcMs = token.ExpiresAtUtcMs,
            AccountId = account.AccountId,
            Characters = await characters.ListAsync(account.AccountId, ct),
        });
    }

    private static async Task<IResult> ListAsync(HttpContext context, CharacterService characters, CancellationToken ct) =>
        Respond(await characters.ListAsync(AccountId(context), ct));

    private static async Task<IResult> CreateAsync(
        HttpContext context, CreateCharacterRequest? request, CharacterService characters, CancellationToken ct)
    {
        if (request == null)
            return Respond(new CreateCharacterResult { ReasonCode = AuthReasonCode.RequestInvalid });
        return Respond(await characters.CreateAsync(AccountId(context), request, ct));
    }

    private static async Task<IResult> DeleteAsync(HttpContext context, long characterId, CharacterService characters, CancellationToken ct) =>
        Respond(await characters.DeleteAsync(AccountId(context), characterId, ct));

    private static async Task<IResult> EnterWorldAsync(HttpContext context, long characterId, CharacterService characters, CancellationToken ct) =>
        Respond(await characters.EnterWorldAsync(AccountId(context), characterId, ct));

    /// <summary>Checks the bearer token, then that the path names this API's realm.</summary>
    private static async ValueTask<object?> RequireAccountInRealm(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var tokens = http.RequestServices.GetRequiredService<AccessTokenService>();
        var check = tokens.Validate(ReadBearer(http.Request));
        if (!check.Ok)
            return Respond(new AuthResponse { ReasonCode = check.Reason });

        var realmId = http.Request.RouteValues["realmId"] as string;
        var characters = http.RequestServices.GetRequiredService<CharacterService>();
        if (!int.TryParse(realmId, out var realm) || realm != characters.RealmId)
            return Respond(new AuthResponse { ReasonCode = AuthReasonCode.RealmUnknown });

        http.Items[AccountIdItem] = check.AccountId;
        return await next(context);
    }

    private static string? ReadBearer(HttpRequest request)
    {
        const string scheme = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }

    private static long AccountId(HttpContext context) => (long)context.Items[AccountIdItem]!;

    private static IResult Respond<T>(T response) where T : AuthResponse =>
        Results.Json(response, statusCode: StatusFor(response.ReasonCode));

    internal static int StatusFor(AuthReasonCode reason) => reason switch
    {
        AuthReasonCode.None => StatusCodes.Status200OK,
        AuthReasonCode.RequestInvalid or AuthReasonCode.NameInvalid or AuthReasonCode.ClassNotAllowed
            or AuthReasonCode.AppearanceInvalid => StatusCodes.Status400BadRequest,
        AuthReasonCode.InvalidTicket or AuthReasonCode.AuthTokenInvalid or AuthReasonCode.AuthTokenExpired => StatusCodes.Status401Unauthorized,
        AuthReasonCode.RealmUnknown or AuthReasonCode.CharacterNotFound => StatusCodes.Status404NotFound,
        AuthReasonCode.NameTaken or AuthReasonCode.NoFreeSlot or AuthReasonCode.CharacterPendingDeletion => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status503ServiceUnavailable,
    };
}
