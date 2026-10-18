#nullable enable
using System.Threading.Tasks;
using Game.UI.Config;
using Shared.Protocol;

namespace Game.UI.Auth
{
    /// <summary>A login ticket for <c>POST /v1/login</c>, and how the title screen names the signed-in account.</summary>
    public sealed class LoginTicket
    {
        private LoginTicket(string ticket, string reason, string badgeTextId, TextArg badgeArg)
        {
            Ticket = ticket;
            Reason = reason;
            BadgeTextId = badgeTextId;
            BadgeArg = badgeArg;
        }

        public bool Ok => Reason.Length == 0;

        public string Ticket { get; }

        /// <summary>Reason code when no ticket could be made.</summary>
        public string Reason { get; }

        /// <summary><c>UiStrings</c> id of the <c>steam_badge</c> text.</summary>
        public string BadgeTextId { get; }

        public TextArg BadgeArg { get; }

        public static LoginTicket Issued(string ticket, string badgeTextId, TextArg badgeArg) =>
            new LoginTicket(ticket, "", badgeTextId, badgeArg);

        public static LoginTicket Failed(ClientReasonCode reason) =>
            new LoginTicket("", reason.ToString(), UiTextIds.BadgeSignedOut, default);
    }

    /// <summary>Where the login ticket comes from: Steam in production (R-2.1-01), the dev login in development builds.</summary>
    public interface ILoginTicketSource
    {
        Task<LoginTicket> GetTicketAsync();
    }

    /// <summary>
    /// Steam session ticket (R-2.1-01). Stub until the Steamworks integration exists: it always reports
    /// <see cref="ClientReasonCode.SteamUnavailable"/>. TODO(steam): <c>GetAuthSessionTicket</c> as hex, the persona name
    /// for the badge, and cancelling the ticket when the session ends. Steam code stays in this auth layer.
    /// </summary>
    public sealed class SteamLoginTicketSource : ILoginTicketSource
    {
        public Task<LoginTicket> GetTicketAsync() => Task.FromResult(LoginTicket.Failed(ClientReasonCode.SteamUnavailable));
    }
}
