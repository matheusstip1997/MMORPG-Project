using System.Net;

namespace GameServer.Host;

/// <summary>
/// Where the HTTP services (Auth API, admin panel) may listen without TLS. Plain HTTP is allowed on loopback only: the
/// Auth API's enter-world response holds the session key (R-2.5-02), and the admin panel is never public (R-2.5-05).
/// <para>
/// Dev containers (spec 11.3, user decision 2026-10-26): inside a container a service must listen on the wildcard address
/// for the published port to reach it, so a wildcard bind is also allowed when all three hold: the build has the dev
/// login (<c>DevLogin</c>, Debug only), <c>MMORPG_DEV_CONTAINER_HTTP=true</c>, and the official .NET image's
/// <c>DOTNET_RUNNING_IN_CONTAINER=true</c>. docker-compose.yml publishes those ports on the host's 127.0.0.1 only.
/// </para>
/// </summary>
public static class HttpListenPolicy
{
    public const string DevContainerHttpVariable = "MMORPG_DEV_CONTAINER_HTTP";
    public const string RunningInContainerVariable = "DOTNET_RUNNING_IN_CONTAINER";

    /// <summary>True for a plain HTTP address that is not loopback (wildcards included), or an unreadable one.</summary>
    public static bool IsPlainHttpOffLoopback(string address)
    {
        if (!TryParse(address, out var uri))
            return true;
        if (uri.Scheme != Uri.UriSchemeHttp)
            return false;
        return !(uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)));
    }

    /// <summary>True for a plain HTTP address on the wildcard address (<c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c>).</summary>
    public static bool IsPlainHttpWildcard(string address) =>
        TryParse(address, out var uri) && uri.Scheme == Uri.UriSchemeHttp
        && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip)
        && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));

    /// <summary>Whether the dev container exception applies (see the class remarks).</summary>
    public static bool AllowsDevContainerHttp(bool devLoginBuild, Func<string, string?> environment) =>
        devLoginBuild && IsTrue(environment(DevContainerHttpVariable)) && IsTrue(environment(RunningInContainerVariable));

    /// <summary>Whether a service may listen on <paramref name="address"/>.</summary>
    public static bool IsAllowed(string address, bool devContainerHttp) =>
        !IsPlainHttpOffLoopback(address) || (devContainerHttp && IsPlainHttpWildcard(address));

    /// <summary>The first address of a <c>;</c>-separated URL list that may not be used, or null.</summary>
    public static string? FirstRefused(string urls, bool devContainerHttp) =>
        urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(url => !IsAllowed(url, devContainerHttp));

    private static bool TryParse(string address, out Uri uri) =>
        Uri.TryCreate(address.Replace("://*", "://0.0.0.0").Replace("://+", "://0.0.0.0"), UriKind.Absolute, out uri!);

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase) || value?.Trim() == "1";
}
