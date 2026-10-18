#nullable enable
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Game.UI.Auth
{
    /// <summary>
    /// <see cref="IAuthHttp"/> over UnityWebRequest. HTTPS plan (R-2.5-02): production uses an <c>https://</c>
    /// <c>LoginSettings.AuthApiUrl</c> with TLS ended in front of the Auth API (TODO(infra)), validated by the platform's
    /// certificate store; plain HTTP is only for a loopback Auth API in dev. <c>AuthEndpoint</c> refuses anything else
    /// before a request is made, and the player setting "Allow downloads over HTTP" is "development builds only", so a
    /// release build cannot send HTTP at all.
    /// </summary>
    public sealed class UnityWebRequestAuthHttp : IAuthHttp
    {
        private readonly int _timeoutSeconds;

        /// <param name="timeoutMs"><c>LoginSettings.AuthRequestTimeoutMs</c>; UnityWebRequest counts whole seconds, rounded up.</param>
        public UnityWebRequestAuthHttp(int timeoutMs)
        {
            _timeoutSeconds = (timeoutMs + 999) / 1000;
        }

        public Task<AuthHttpResponse> SendAsync(AuthHttpRequest request)
        {
            var completion = new TaskCompletionSource<AuthHttpResponse>();
            var web = new UnityWebRequest(request.Url, request.Method)
            {
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = _timeoutSeconds,
            };
            if (request.JsonBody != null)
            {
                web.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.JsonBody));
                web.SetRequestHeader("Content-Type", "application/json");
            }
            web.SetRequestHeader("Accept", "application/json");
            if (request.BearerToken != null)
                web.SetRequestHeader("Authorization", "Bearer " + request.BearerToken);

            web.SendWebRequest().completed += _ =>
            {
                // A 4xx or 5xx (ProtocolError) still carries the JSON body with the reason code.
                var response = web.result == UnityWebRequest.Result.Success || web.result == UnityWebRequest.Result.ProtocolError
                    ? AuthHttpResponse.Answer((int)web.responseCode, web.downloadHandler.text)
                    : AuthHttpResponse.Failed(web.error);
                web.Dispose();
                completion.SetResult(response);
            };
            return completion.Task;
        }
    }
}
