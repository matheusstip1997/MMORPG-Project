#nullable enable
using System;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Social;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Social
{
    /// <summary>What the player does in the social window; the binder turns it into requests (WP-M4-16).</summary>
    public interface ISocialWindowActions
    {
        void CloseWindow();
        void SelectTab(SocialTab tab);
        void SetDraft(string text);
        /// <summary>Asks the typed name to be friends (or accepts that player's request).</summary>
        void AddFriend();
        void BlockTyped();
        void MuteTyped();
        void Accept(uint reference);
        void Decline(uint reference);
        void RemoveFriend(uint reference);
        void Unblock(uint reference);
        /// <summary>The name field gained or lost the focus: gameplay keys are off while it has it.</summary>
        void FocusField(bool focused);
    }

    /// <summary>
    /// Connects the social window (spec 6.5 "M4 social", 9.3, 9.13; WP-M4-16) to the world client: the server's lists
    /// (<see cref="WorldClientFeed.SocialReceived"/>) go into the <see cref="SocialWindowViewModel"/>; the buttons send through
    /// <see cref="WorldClientFeed.SocialRequests"/> (an add, block or mute by the typed name, the rest by the entry's ref). A refusal
    /// shows its <c>ReasonCodeStrings</c> text in the HUD toast, an applied change or a notice its <c>UiStrings</c> toast (a friend
    /// request received, accepted, declined, expired). The toggle key is the <c>Hotkeys</c> row <c>toggleSocial</c>, ignored while a
    /// text field has focus. One of the left-edge windows (closed while a trade is open). The server decides everything.
    /// </summary>
    public sealed class SocialWindowBinder : ISocialWindowActions, IDisposable
    {
        private readonly SocialWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;
        private bool _fieldFocused;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public SocialWindowBinder(SocialConfig config, SocialWindowViewModel window, ToastViewModel toast, int toastMs, Func<double> nowMs,
            bool readToggleKey = true)
        {
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleSocial", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>Whether the left-edge windows are blocked (a trade is open; the HUD's <c>WindowGroup</c>); null: never.</summary>
        public Func<bool>? TradeOpen { get; set; }

        private bool TradeIsOpen => TradeOpen?.Invoke() ?? false;

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.SocialReceived += OnState;
            feed.SocialAnswered += OnAnswered;
            feed.Disconnected += OnDisconnected;
            if (feed.Social != null) // it may have come before the HUD bound
                OnState(feed.Social);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.SocialReceived -= OnState;
            _feed.SocialAnswered -= OnAnswered;
            _feed.Disconnected -= OnDisconnected;
            if (_fieldFocused)
                _feed.WindowTextInput = false;
            _fieldFocused = false;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>The SO button and the key: opens or closes the window; while a trade is open it toasts why (WP-M4-11).</summary>
        public void Toggle()
        {
            if (!TradeIsOpen || _window.Open)
                SetOpen(!_window.Open);
            else
                _toast.Show(nameof(ClientReasonCode.WindowBlockedByTrade), false, _now(), _toastMs); // WP-M4-11: not silent
        }

        /// <summary>Once per frame: a trade keeps the window closed; the toggle key (ignored while a text field has focus).</summary>
        public void Frame(double nowMs)
        {
            if (TradeIsOpen && _window.Open)
                SetOpen(false);
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                Toggle();
        }

        public void CloseWindow() => SetOpen(false);

        public void SelectTab(SocialTab tab) => _window.SetTab(tab);

        public void SetDraft(string text) => _window.SetDraft(text);

        public void AddFriend() => SendTyped(SocialAction.AddFriend);

        public void BlockTyped() => SendTyped(SocialAction.Block);

        public void MuteTyped() => SendTyped(SocialAction.Mute);

        public void Accept(uint reference) => Send(SocialAction.AcceptFriend, reference);

        public void Decline(uint reference) => Send(SocialAction.DeclineFriend, reference);

        public void RemoveFriend(uint reference) => Send(SocialAction.RemoveFriend, reference);

        public void Unblock(uint reference) => Send(SocialAction.Unblock, reference);

        public void FocusField(bool focused)
        {
            _fieldFocused = focused && _window.Open;
            if (_feed != null)
                _feed.WindowTextInput = _fieldFocused;
        }

        private void SetOpen(bool open)
        {
            if (!open)
                FocusField(false);
            _window.SetOpen(open);
        }

        private void SendTyped(SocialAction action)
        {
            if (!_window.CanSend)
                return;
            _feed?.SocialRequests?.RequestSocial(action, 0, _window.TakeDraft());
        }

        private void Send(SocialAction action, uint reference)
        {
            if (reference != 0)
                _feed?.SocialRequests?.RequestSocial(action, reference, string.Empty);
        }

        private void OnState(SocialState state) => _window.SetState(state);

        /// <summary>A refusal's reason text, or the toast of an applied change or a notice.</summary>
        private void OnAnswered(SocialResult result)
        {
            if (result.Reason != SocialReason.None)
            {
                _toast.Show(result.Reason.ToString(), false, _now(), _toastMs);
                return;
            }
            string textId = ToastFor(result);
            if (textId.Length > 0)
                _toast.ShowText(textId, "name", result.Name, _now(), _toastMs);
        }

        /// <summary>The <c>UiStrings</c> id of an accepted answer or a notice; empty for none (a decline the player made).</summary>
        public static string ToastFor(SocialResult result)
        {
            switch (result.Notice)
            {
                case SocialNotice.RequestSent:
                    return SocialTextIds.ToastRequestSent;
                case SocialNotice.RequestReceived:
                    return SocialTextIds.ToastRequestReceived;
                case SocialNotice.RequestDeclined:
                    return SocialTextIds.ToastRequestDeclined;
                case SocialNotice.RequestExpired:
                    return SocialTextIds.ToastRequestExpired;
                case SocialNotice.FriendAdded:
                    return SocialTextIds.ToastFriendAdded;
            }
            switch (result.Action)
            {
                case SocialAction.RemoveFriend:
                    return SocialTextIds.ToastFriendRemoved;
                case SocialAction.Block:
                    return SocialTextIds.ToastBlocked;
                case SocialAction.Mute:
                    return SocialTextIds.ToastMuted;
                case SocialAction.Unblock:
                    return SocialTextIds.ToastUnblocked;
                default:
                    return string.Empty;
            }
        }

        private void OnDisconnected(string reasonCode)
        {
            FocusField(false);
            _window.Clear();
        }
    }
}
