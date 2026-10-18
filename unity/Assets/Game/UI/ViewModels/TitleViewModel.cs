#nullable enable
using Game.UI.Config;

namespace Game.UI.ViewModels
{
    public enum TitleState
    {
        SigningIn,
        /// <summary>Signed in: any button continues (R-9.10-01).</summary>
        Ready,
        /// <summary>Sign-in failed: any button tries again.</summary>
        Failed,
    }

    /// <summary>Title screen (spec 9.10): sign-in state, the signed-in account badge, client and protocol version.</summary>
    public sealed class TitleViewModel : ViewModel
    {
        public TitleViewModel(string clientVersion, ushort protocolVersion)
        {
            ClientVersion = clientVersion;
            ProtocolVersion = protocolVersion;
        }

        public string ClientVersion { get; }

        public ushort ProtocolVersion { get; }

        public TitleState State { get; private set; } = TitleState.SigningIn;

        public LoginStatus Status { get; private set; } = LoginStatus.Busy(UiTextIds.SigningIn);

        /// <summary><c>UiStrings</c> id of the <c>steam_badge</c> text and its value.</summary>
        public string BadgeTextId { get; private set; } = UiTextIds.BadgeSignedOut;

        public TextArg BadgeArg { get; private set; }

        public void SetSigningIn()
        {
            State = TitleState.SigningIn;
            Status = LoginStatus.Busy(UiTextIds.SigningIn);
            NotifyChanged();
        }

        public void SetSignedIn(string badgeTextId, TextArg badgeArg)
        {
            State = TitleState.Ready;
            Status = LoginStatus.None;
            BadgeTextId = badgeTextId;
            BadgeArg = badgeArg;
            NotifyChanged();
        }

        /// <summary>Sign-in failed or the session ended (an expired access token sends the player back here).</summary>
        public void SetFailed(string reasonCode)
        {
            State = TitleState.Failed;
            Status = LoginStatus.Error(reasonCode);
            BadgeTextId = UiTextIds.BadgeSignedOut;
            BadgeArg = default;
            NotifyChanged();
        }
    }
}
