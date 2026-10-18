#nullable enable
using System.Globalization;
using System.Threading.Tasks;
using Shared.Data;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;
using Shared.Security;

namespace Game.UI.Auth
{
    /// <summary>
    /// Outcome of one Auth API call: the response when <see cref="Ok"/>, otherwise <see cref="Reason"/>, the code name
    /// the UI shows through <c>ReasonCodeStrings</c> (an <see cref="AuthReasonCode"/> from the server, or a
    /// <see cref="ClientReasonCode"/> the client detected).
    /// </summary>
    public sealed class AuthCall<T> where T : AuthResponse
    {
        private AuthCall(T? response, string reason)
        {
            Response = response;
            Reason = reason;
        }

        public bool Ok => Reason.Length == 0;

        /// <summary>Set when <see cref="Ok"/>.</summary>
        public T? Response { get; }

        /// <summary>Empty when <see cref="Ok"/>.</summary>
        public string Reason { get; }

        public bool Is(AuthReasonCode code) => Reason == code.ToString();

        public static AuthCall<T> Success(T response) => new AuthCall<T>(response, "");

        public static AuthCall<T> Refused(string reason) => new AuthCall<T>(null, reason);

        public static AuthCall<T> Refused(ClientReasonCode reason) => new AuthCall<T>(null, reason.ToString());
    }

    /// <summary>The Auth API calls of the login flow (spec 9.10 "Auth API v0").</summary>
    public interface IAuthApi
    {
        /// <summary><c>POST /v1/login</c>; on success the client keeps the access token for the other calls.</summary>
        Task<AuthCall<LoginResponse>> LoginAsync(string ticket);

        Task<AuthCall<CharacterListResponse>> ListCharactersAsync(int realmId);

        Task<AuthCall<CreateCharacterResult>> CreateCharacterAsync(int realmId, CreateCharacterRequest request);

        Task<AuthCall<DeleteCharacterResult>> DeleteCharacterAsync(int realmId, long characterId);

        /// <summary>The single-use game ticket and the session key (R-2.5-01, R-2.5-02). Clear the key after connecting.</summary>
        Task<AuthCall<EnterWorldResult>> EnterWorldAsync(int realmId, long characterId);

        /// <summary>Forgets the access token.</summary>
        void SignOut();
    }

    /// <summary>
    /// <see cref="IAuthApi"/> over HTTP (<see cref="IAuthHttp"/>). Every outcome is <c>ok | reasonCode</c>: transport
    /// failures become <see cref="ClientReasonCode.AuthApiUnreachable"/>, unreadable answers
    /// <see cref="ClientReasonCode.AuthResponseInvalid"/>, and an address that is neither HTTPS nor loopback HTTP is
    /// refused before anything is sent (<see cref="ClientReasonCode.AuthEndpointInsecure"/>, R-2.5-02). The access
    /// token stays in memory only.
    /// </summary>
    public sealed class AuthApiClient : IAuthApi
    {
        private readonly IAuthHttp _http;
        private readonly string _baseUrl;
        private readonly bool _endpointAllowed;
        private string? _accessToken;

        public AuthApiClient(IAuthHttp http, string baseUrl)
        {
            _http = http;
            _baseUrl = baseUrl.TrimEnd('/');
            _endpointAllowed = AuthEndpoint.IsAllowed(baseUrl, out _);
        }

        public bool SignedIn => _accessToken != null;

        public async Task<AuthCall<LoginResponse>> LoginAsync(string ticket)
        {
            var body = AuthJson.Login(new LoginRequest { Ticket = ticket });
            var call = await SendAsync<LoginResponse>("POST", "/v1/login", body, authorized: false, AuthJson.TryReadLogin);
            if (call.Ok)
            {
                if (call.Response!.AccessToken.Length == 0)
                    return AuthCall<LoginResponse>.Refused(ClientReasonCode.AuthResponseInvalid);
                _accessToken = call.Response.AccessToken;
            }
            return call;
        }

        public Task<AuthCall<CharacterListResponse>> ListCharactersAsync(int realmId) =>
            SendAsync<CharacterListResponse>("GET", RealmPath(realmId) + "/characters", null, authorized: true, AuthJson.TryReadCharacterList);

        public Task<AuthCall<CreateCharacterResult>> CreateCharacterAsync(int realmId, CreateCharacterRequest request) =>
            SendAsync<CreateCharacterResult>("POST", RealmPath(realmId) + "/characters", AuthJson.CreateCharacter(request),
                authorized: true, AuthJson.TryReadCreate);

        public Task<AuthCall<DeleteCharacterResult>> DeleteCharacterAsync(int realmId, long characterId) =>
            SendAsync<DeleteCharacterResult>("DELETE", CharacterPath(realmId, characterId), null, authorized: true, AuthJson.TryReadDelete);

        public async Task<AuthCall<EnterWorldResult>> EnterWorldAsync(int realmId, long characterId)
        {
            var call = await SendAsync<EnterWorldResult>("POST", CharacterPath(realmId, characterId) + "/enter", null,
                authorized: true, AuthJson.TryReadEnterWorld);
            if (call.Ok)
            {
                var result = call.Response!;
                // R-2.5-04: bound-check what goes into the connection request and the crypto.
                if (result.Ticket.Length == 0 || result.Ticket.Length > ConnectRequest.MaxTicketLength
                    || result.SessionKey.Length != SessionCrypto.KeySize)
                    return AuthCall<EnterWorldResult>.Refused(ClientReasonCode.AuthResponseInvalid);
            }
            return call;
        }

        public void SignOut() => _accessToken = null;

        private delegate bool BodyReader<in T>(ConfigJsonValue root, T into);

        private async Task<AuthCall<T>> SendAsync<T>(string method, string path, string? body, bool authorized, BodyReader<T> read)
            where T : AuthResponse, new()
        {
            if (!_endpointAllowed)
                return AuthCall<T>.Refused(ClientReasonCode.AuthEndpointInsecure);
            if (authorized && _accessToken == null)
                return AuthCall<T>.Refused(AuthReasonCode.AuthTokenInvalid.ToString());

            var response = await _http.SendAsync(new AuthHttpRequest(method, _baseUrl + path, body, authorized ? _accessToken : null));
            if (response.TransportFailed)
                return AuthCall<T>.Refused(ClientReasonCode.AuthApiUnreachable);

            var result = new T();
            if (!AuthJson.TryReadBase(response.Body, result, out var reason, out var root))
                return AuthCall<T>.Refused(ClientReasonCode.AuthResponseInvalid);
            if (!result.Ok)
                return AuthCall<T>.Refused(reason.Length > 0 ? reason : ClientReasonCode.AuthResponseInvalid.ToString());
            if (reason.Length > 0 || response.Status < 200 || response.Status > 299 || !read(root, result))
                return AuthCall<T>.Refused(ClientReasonCode.AuthResponseInvalid);
            return AuthCall<T>.Success(result);
        }

        private static string RealmPath(int realmId) => "/v1/realms/" + realmId.ToString(CultureInfo.InvariantCulture);

        private static string CharacterPath(int realmId, long characterId) =>
            RealmPath(realmId) + "/characters/" + characterId.ToString(CultureInfo.InvariantCulture);
    }
}
