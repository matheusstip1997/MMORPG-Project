#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Game.UI.ViewModels.Mail;
using Shared.Protocol;
using UnityEngine.InputSystem;

namespace Game.UI.Menus.Mail
{
    /// <summary>What the player does in the mail window; the binder turns it into requests (WP-M4-3).</summary>
    public interface IMailWindowActions
    {
        void SelectMail(long mailId);
        void Claim();
        void ClaimAll();
        void CloseMail();
    }

    /// <summary>
    /// Connects the mail window (spec 4.5, 9.3, 9.13; WP-M4-3) to the world client. The mailbox list, a mail's content, the claim
    /// answers and the open count of the <see cref="WorldClientFeed"/> go into the <see cref="MailWindowViewModel"/>. Opening the
    /// window (its key, the M shortcut) asks for the list; selecting a mail asks for its content; Take claims the selected mail,
    /// Take all every open mail, one claim at a time, through <see cref="WorldClientFeed.MailRequests"/>. An applied claim (after its
    /// commit) shows a news toast of what was taken and asks for the list again; the badge waits for the server's next count.
    /// Refusals show their <c>ReasonCodeStrings</c> text in the HUD toast. The toggle key is the <c>Hotkeys</c> row
    /// <c>toggleMail</c>, ignored while a text field has focus. Display time is the caller's clock in ms.
    /// </summary>
    public sealed class MailWindowBinder : IMailWindowActions, IDisposable
    {
        private readonly MailConfig _config;
        private readonly UiText _text;
        private readonly MailWindowViewModel _window;
        private readonly ToastViewModel _toast;
        private readonly int _toastMs;
        private readonly Func<double> _now;
        private readonly InputAction? _toggle;
        private WorldClientFeed? _feed;
        private bool _wasOpen;
        private bool _tradeWasOpen;

        /// <param name="readToggleKey">False in tests: no Input System action is made for the toggle key.</param>
        public MailWindowBinder(MailConfig config, UiText text, MailWindowViewModel window, ToastViewModel toast, int toastMs,
            Func<double> nowMs, bool readToggleKey = true)
        {
            _config = config;
            _text = text;
            _window = window;
            _toast = toast;
            _toastMs = toastMs;
            _now = nowMs;
            if (readToggleKey && config.ToggleBinding.Length > 0)
            {
                _toggle = new InputAction("ToggleMail", InputActionType.Button, config.ToggleBinding);
                _toggle.Enable();
            }
        }

        public WorldClientFeed? Feed => _feed;

        /// <summary>
        /// Whether the trade window is open (WP-M4-4); null for no trade window. TODO(open): spec 9.13 has no rule for the mail
        /// window beside a trade. Placeholder: a trade window opening closes the mail window (both sit on the left edge); M opens it
        /// again on top during the trade.
        /// </summary>
        public Func<bool>? TradeOpen { get; set; }

        public void Bind(WorldClientFeed feed)
        {
            Unbind();
            _feed = feed;
            feed.MailListReceived += OnList;
            feed.MailContentReceived += OnContent;
            feed.MailAnswered += OnAnswered;
            feed.MailCountReceived += OnCount;
            feed.Disconnected += OnDisconnected;
            if (feed.MailCount != null) // it may have come before the HUD bound
                OnCount(feed.MailCount);
        }

        public void Unbind()
        {
            if (_feed == null)
                return;
            _feed.MailListReceived -= OnList;
            _feed.MailContentReceived -= OnContent;
            _feed.MailAnswered -= OnAnswered;
            _feed.MailCountReceived -= OnCount;
            _feed.Disconnected -= OnDisconnected;
            _feed = null;
        }

        public void Dispose()
        {
            Unbind();
            _toggle?.Dispose();
        }

        /// <summary>Once per frame: a trade opening closes the window, the toggle key (ignored while a text field has focus), and the list when the window opens.</summary>
        public void Frame(double nowMs)
        {
            bool tradeOpen = TradeOpen?.Invoke() ?? false;
            if (tradeOpen && !_tradeWasOpen)
                _window.SetOpen(false);
            _tradeWasOpen = tradeOpen;
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                _window.Toggle();
            if (_window.Open && !_wasOpen)
                RequestList();
            _wasOpen = _window.Open;
        }

        public void SelectMail(long mailId)
        {
            if (_window.Select(mailId))
                _feed?.MailRequests?.OpenMail(mailId);
        }

        public void Claim()
        {
            if (_window.CanClaim)
                SendClaim(_window.Selected);
        }

        public void ClaimAll()
        {
            if (_window.CanClaimAll)
                SendClaim(0);
        }

        public void CloseMail() => _window.SetOpen(false);

        private void SendClaim(long mailId)
        {
            uint requestId = _feed?.MailRequests?.ClaimMail(mailId) ?? 0;
            if (requestId != 0)
                _window.SetPendingClaim(requestId);
        }

        private void RequestList() => _feed?.MailRequests?.RequestMailList();

        private void OnList(MailList list)
        {
            if (_window.SetList(list) is { } refused)
                _toast.Show(refused, false, _now(), _toastMs);
        }

        private void OnContent(MailContent content)
        {
            if (_window.SetContent(content) is { } refused)
                _toast.Show(refused, false, _now(), _toastMs);
        }

        private void OnAnswered(MailResult result)
        {
            string? refused = _window.OnResult(result, out bool applied);
            if (refused != null)
            {
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            if (!applied)
                return;
            _toast.ShowText(result.ItemsLeft > 0 || result.GoldLeft > 0 ? MailTextIds.ToastPartial : MailTextIds.ToastClaimed, "what",
                Taken(result), _now(), _toastMs);
            if (_window.Open)
                RequestList();
        }

        /// <summary>A new count while the window is open: the mailbox changed (a mail arrived or expired), so list it again.</summary>
        private void OnCount(MailCount count)
        {
            bool changed = count.Open != _window.OpenCount;
            _window.SetCount(count);
            if (changed && _window.Open && _window.PendingClaim == 0 && _window.HasList)
                RequestList();
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Clear();
            _wasOpen = false;
        }

        /// <summary>"Demon Ash x3, 50 gold": what a claim moved, from the tables' names.</summary>
        private string Taken(MailResult result)
        {
            var parts = new List<string>();
            foreach (var item in result.Taken)
                parts.Add(AttachmentText(_config, _text, item.ItemNetId, item.Count));
            if (result.GoldTaken > 0)
                parts.Add(_text.Get(MailTextIds.Gold, new TextArg("gold", result.GoldTaken.ToString(CultureInfo.InvariantCulture))));
            return string.Join(", ", parts);
        }

        /// <summary>"Demon Ash x3" (the item's <c>UiStrings</c> name; its net id when the tables lack it).</summary>
        public static string AttachmentText(MailConfig config, UiText text, int itemNetId, int count)
        {
            var item = config.Item(itemNetId);
            string name = item == null ? itemNetId.ToString(CultureInfo.InvariantCulture) : text.Get(item.NameId);
            return text.Get(MailTextIds.Attachment, new TextArg("name", name), new TextArg("count", count.ToString(CultureInfo.InvariantCulture)));
        }
    }
}
