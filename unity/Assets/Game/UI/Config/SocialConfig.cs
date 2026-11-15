#nullable enable
using Shared.Data;
using Shared.Data.Tables;

namespace Game.UI.Config
{
    /// <summary>
    /// <c>UiStrings</c> ids of the social window (spec 9.1: no user-facing text in code; spec 6.5 "M4 social", 9.3, 9.13; WP-M4-16).
    /// An EditMode test checks that each constant has a row.
    /// </summary>
    public static class SocialTextIds
    {
        public const string Title = "social.title";
        public const string Close = "social.close";
        /// <summary>The close button's label for screen readers (spec 9.8).</summary>
        public const string CloseLabel = "social.closeLabel";
        public const string TabFriends = "social.tab.friends";
        public const string TabBlocked = "social.tab.blocked";
        /// <summary>The name field's label.</summary>
        public const string NameLabel = "social.nameLabel";
        public const string Add = "social.add";
        public const string Block = "social.block";
        public const string Mute = "social.mute";
        public const string Remove = "social.remove";
        public const string Accept = "social.accept";
        public const string Decline = "social.decline";
        /// <summary>{name} wants to be your friend (an incoming request row).</summary>
        public const string Request = "social.request";
        /// <summary>{name} - Online.</summary>
        public const string FriendOnline = "social.friend.online";
        /// <summary>{name} - Offline.</summary>
        public const string FriendOffline = "social.friend.offline";
        /// <summary>{name} - Blocked.</summary>
        public const string Blocked = "social.entry.blocked";
        /// <summary>{name} - Muted.</summary>
        public const string Muted = "social.entry.muted";
        public const string NoFriends = "social.noFriends";
        public const string NoBlocks = "social.noBlocks";
        /// <summary>Friend request sent to {name}.</summary>
        public const string ToastRequestSent = "social.toast.requestSent";
        /// <summary>{name} wants to be your friend.</summary>
        public const string ToastRequestReceived = "social.toast.requestReceived";
        /// <summary>{name} declined your friend request.</summary>
        public const string ToastRequestDeclined = "social.toast.requestDeclined";
        /// <summary>Your friend request to {name} expired.</summary>
        public const string ToastRequestExpired = "social.toast.requestExpired";
        /// <summary>You and {name} are now friends.</summary>
        public const string ToastFriendAdded = "social.toast.friendAdded";
        public const string ToastFriendRemoved = "social.toast.friendRemoved";
        public const string ToastBlocked = "social.toast.blocked";
        public const string ToastMuted = "social.toast.muted";
        public const string ToastUnblocked = "social.toast.unblocked";

        public static readonly string[] All =
        {
            Title, Close, CloseLabel, TabFriends, TabBlocked, NameLabel, Add, Block, Mute, Remove, Accept, Decline, Request,
            FriendOnline, FriendOffline, Blocked, Muted, NoFriends, NoBlocks, ToastRequestSent, ToastRequestReceived,
            ToastRequestDeclined, ToastRequestExpired, ToastFriendAdded, ToastFriendRemoved, ToastBlocked, ToastMuted, ToastUnblocked,
        };
    }

    /// <summary>
    /// The config the social window reads (spec 6.5, 9.13; WP-M4-16): the toggle key (<c>Hotkeys</c> row <c>toggleSocial</c>). The
    /// window only shows the lists; the server owns them and decides every request.
    /// </summary>
    public sealed class SocialConfig
    {
        public SocialConfig(string toggleBinding) => ToggleBinding = toggleBinding;

        /// <summary>The <c>toggleSocial</c> control path; empty when the table has no row.</summary>
        public string ToggleBinding { get; }

        public static SocialConfig From(ConfigSet set) =>
            new SocialConfig(Hotkeys.Find(set, Hotkeys.ToggleSocial)?.Binding ?? string.Empty);
    }
}
