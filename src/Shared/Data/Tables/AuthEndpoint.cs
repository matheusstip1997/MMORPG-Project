using System;
using System.Net;

namespace Shared.Data.Tables
{
    /// <summary>
    /// Which Auth API addresses a client may call. The enter-world response carries the session key, so it only travels
    /// over HTTPS, or plain HTTP on loopback for local dev where it never leaves the machine (R-2.5-02; the Auth API
    /// refuses the same addresses on its side).
    /// </summary>
    public static class AuthEndpoint
    {
        public static bool IsAllowed(string url, out string problem)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            {
                problem = $"'{url}' is not an absolute URL";
                return false;
            }
            if (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri)))
            {
                problem = "";
                return true;
            }
            problem = $"'{url}' must use https:// (plain http:// only on a loopback address)";
            return false;
        }

        private static bool IsLoopback(Uri uri) =>
            uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }
}
