using Shared.Protocol.Auth;

namespace AuthApi.Identity;

/// <summary>
/// Steam login (R-2.1-01, R-2.1-02). Not implemented yet: every login is refused with <c>ServerUnavailable</c>.
/// TODO(steam): verify the ticket with the Steam Web API (ISteamUserAuth/AuthenticateUserTicket) using the publisher key
/// from the parameter store, take the SteamID and the Family Sharing owner SteamID from the response, and make sure the
/// ticket is single use. No Steam calls or credentials exist in the repository until then.
/// </summary>
public sealed class SteamIdentityProvider : IIdentityProvider
{
    public Task<IdentityResult> VerifyAsync(string ticket, CancellationToken ct) =>
        Task.FromResult(IdentityResult.Refused(AuthReasonCode.ServerUnavailable));
}
