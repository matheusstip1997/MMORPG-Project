#nullable enable
using System.Threading.Tasks;

namespace Game.UI.Auth
{
    /// <summary>One HTTP request to the Auth API.</summary>
    public sealed class AuthHttpRequest
    {
        public AuthHttpRequest(string method, string url, string? jsonBody, string? bearerToken)
        {
            Method = method;
            Url = url;
            JsonBody = jsonBody;
            BearerToken = bearerToken;
        }

        public string Method { get; }

        public string Url { get; }

        public string? JsonBody { get; }

        /// <summary>Sent as <c>Authorization: Bearer &lt;token&gt;</c> (spec 9.10).</summary>
        public string? BearerToken { get; }
    }

    /// <summary>The answer, or a transport failure (no connection, timeout) with no answer at all.</summary>
    public sealed class AuthHttpResponse
    {
        private AuthHttpResponse(bool transportFailed, int status, string body, string error)
        {
            TransportFailed = transportFailed;
            Status = status;
            Body = body;
            Error = error;
        }

        public bool TransportFailed { get; }

        public int Status { get; }

        public string Body { get; }

        /// <summary>Transport error text for logs only, never shown to the player.</summary>
        public string Error { get; }

        public static AuthHttpResponse Answer(int status, string body) => new AuthHttpResponse(false, status, body ?? "", "");

        public static AuthHttpResponse Failed(string error) => new AuthHttpResponse(true, 0, "", error ?? "");
    }

    /// <summary>
    /// Sends Auth API requests. <see cref="UnityWebRequestAuthHttp"/> in the game, a fake in tests. Completes on the
    /// caller's thread (Unity's main thread); never throws for network problems, it returns
    /// <see cref="AuthHttpResponse.Failed"/>.
    /// </summary>
    public interface IAuthHttp
    {
        Task<AuthHttpResponse> SendAsync(AuthHttpRequest request);
    }
}
