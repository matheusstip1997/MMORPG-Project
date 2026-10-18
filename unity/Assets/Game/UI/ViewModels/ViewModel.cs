#nullable enable
using System;

namespace Game.UI.ViewModels
{
    /// <summary>
    /// Base of the UI view models (spec 9.9): state filled from server messages and config, no gameplay rules.
    /// Presenters subscribe to <see cref="Changed"/> and redraw; nobody polls.
    /// </summary>
    public abstract class ViewModel
    {
        public event Action? Changed;

        protected void NotifyChanged() => Changed?.Invoke();
    }

    public enum StatusKind
    {
        None,
        /// <summary>A request is running; <see cref="LoginStatus.TextId"/> says which.</summary>
        Busy,
        /// <summary>Refused or failed; <see cref="LoginStatus.ReasonCode"/> says why.</summary>
        Error,
    }

    /// <summary>The message line of a login screen: nothing, a busy text, or a reason code (spec 9.1).</summary>
    public sealed class LoginStatus
    {
        public static readonly LoginStatus None = new LoginStatus(StatusKind.None, "", "", 0);

        private LoginStatus(StatusKind kind, string textId, string reasonCode, ushort serverProtocolVersion)
        {
            Kind = kind;
            TextId = textId;
            ReasonCode = reasonCode;
            ServerProtocolVersion = serverProtocolVersion;
        }

        public StatusKind Kind { get; }

        /// <summary><c>UiStrings</c> id when <see cref="StatusKind.Busy"/>.</summary>
        public string TextId { get; }

        /// <summary><c>ReasonCodeStrings</c> code when <see cref="StatusKind.Error"/>.</summary>
        public string ReasonCode { get; }

        /// <summary>The server's protocol version for <c>VersionMismatch</c> text; 0 when unknown.</summary>
        public ushort ServerProtocolVersion { get; }

        public bool IsBusy => Kind == StatusKind.Busy;

        public static LoginStatus Busy(string textId) => new LoginStatus(StatusKind.Busy, textId, "", 0);

        public static LoginStatus Error(string reasonCode, ushort serverProtocolVersion = 0) =>
            new LoginStatus(StatusKind.Error, "", reasonCode, serverProtocolVersion);
    }
}
