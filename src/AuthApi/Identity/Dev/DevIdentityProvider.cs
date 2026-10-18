using System.Globalization;
using Shared.Protocol.Auth;

namespace AuthApi.Identity.Dev;

/// <summary>
/// Dev login (spec 2.1 implementation notes). Compiled only with the DevLogin build flag (DevLogin.props) and used only
/// when <c>MMORPG_AUTH_IDENTITY=dev</c>. The ticket <c>dev:&lt;n&gt;</c> logs in as fake SteamID n (a positive integer),
/// creating the account on first use. Anyone who can reach the API can log in as anyone: dev machines only.
/// </summary>
public sealed class DevIdentityProvider : IIdentityProvider
{
    public const string Prefix = "dev:";

    public Task<IdentityResult> VerifyAsync(string ticket, CancellationToken ct)
    {
        if (ticket.StartsWith(Prefix, StringComparison.Ordinal)
            && long.TryParse(ticket.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var steamId)
            && steamId > 0)
        {
            return Task.FromResult(IdentityResult.Verified(steamId, ownerSteamId: null));
        }

        return Task.FromResult(IdentityResult.Refused(AuthReasonCode.InvalidTicket));
    }
}
