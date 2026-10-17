using Shared.Protocol.Auth;

namespace AuthApi.Identity;

/// <summary>Who logged in: the account key is the SteamID (R-2.1-01), plus the Family Sharing owner if any (R-2.1-02).</summary>
public readonly record struct IdentityResult(AuthReasonCode Reason, long SteamId, long? OwnerSteamId)
{
    public bool Ok => Reason == AuthReasonCode.None;

    public static IdentityResult Verified(long steamId, long? ownerSteamId) => new(AuthReasonCode.None, steamId, ownerSteamId);

    public static IdentityResult Refused(AuthReasonCode reason) => new(reason, 0, null);
}

/// <summary>Verifies a login ticket. Production uses Steam only (R-2.1-01); the dev provider exists only behind the DevLogin build flag.</summary>
public interface IIdentityProvider
{
    Task<IdentityResult> VerifyAsync(string ticket, CancellationToken ct);
}

/// <summary>Picks the identity provider named by <c>MMORPG_AUTH_IDENTITY</c>.</summary>
public static class IdentityProviders
{
    public const string Steam = "steam";
    public const string Dev = "dev";

    /// <summary>True when this build contains the dev login (DevLogin.props).</summary>
    public const bool DevLoginCompiled =
#if DEV_LOGIN
        true;
#else
        false;
#endif

    public static bool IsAvailable(string mode, out string error)
    {
        error = "";
        if (mode == Steam || (mode == Dev && DevLoginCompiled))
            return true;
        error = mode == Dev
            ? "the dev login is not compiled into this build (DevLogin=false, the Release default)"
            : $"unknown identity provider '{mode}' (expected '{Steam}' or '{Dev}')";
        return false;
    }

    public static IIdentityProvider Create(string mode) => mode switch
    {
        Steam => new SteamIdentityProvider(),
#if DEV_LOGIN
        Dev => new Dev.DevIdentityProvider(),
#endif
        _ => throw new InvalidOperationException($"identity provider '{mode}' is not available in this build"),
    };
}
