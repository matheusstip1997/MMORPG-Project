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
    /// window (its key, the M shortcut) asks for the list; selecting a mail asks for its content (the detail draws its items);
    /// Take claims the selected mail, Take all every open mail, one claim at a time, through
    /// <see cref="WorldClientFeed.MailRequests"/>. An applied claim (after its commit) shows a news toast of what was taken and
    /// asks for the list again, once: the server's new count that follows it does not ask again (one list request per claim,
    /// within the <c>MailSettings</c> rate limit). A count change while the window is open and nothing is asked lists again. A
    /// refused list is shown in the window and asked for again once, one rate-limit period later (no retry loop: the next try
    /// after that is a count change or reopening). Refusals show their <c>ReasonCodeStrings</c> text in the HUD toast. The toggle
    /// key is the <c>Hotkeys</c> row <c>toggleMail</c>, ignored while a text field has focus. Display time is the caller's clock in ms.
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
        private bool _listPending; // a list request waits for its answer
        private bool _retried; // the one retry of a refused list was sent since the last list that came through
        private double _retryAtMs = -1;
        private double _redrawAtMs;

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
        /// window beside a trade. Placeholder: the mail window stays closed while a trade is open (both sit on the left edge, and
        /// the mail window would hide the partner's offer and Lock/Confirm); its key and the M button do nothing until it ends.
        /// </summary>
        public Func<bool>? TradeOpen { get; set; }

        private bool TradeIsOpen => TradeOpen?.Invoke() ?? false;

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

        /// <summary>The M button and the key: opens or closes the window; nothing while a trade is open (the placeholder above).</summary>
        public void Toggle()
        {
            if (!TradeIsOpen || _window.Open)
                _window.Toggle();
        }

        /// <summary>
        /// Once per frame: a trade keeps the window closed; the toggle key (ignored while a text field has focus); the list when
        /// the window opens; the one retry of a refused list when it is due; the expiry times redrawn while open.
        /// </summary>
        public void Frame(double nowMs)
        {
            if (TradeIsOpen && _window.Open)
                _window.SetOpen(false);
            if (_toggle != null && _toggle.WasPressedThisFrame() && !(_feed?.TextInputActive ?? false))
                Toggle();
            if (_window.Open && !_wasOpen)
            {
                _retried = false;
                _retryAtMs = -1;
                _redrawAtMs = nowMs + _config.WindowRefreshMs;
                RequestList();
            }
            _wasOpen = _window.Open;
            if (!_window.Open)
                return;
            if (_retryAtMs >= 0 && nowMs >= _retryAtMs)
            {
                _retryAtMs = -1;
                _retried = true;
                if (!_listPending)
                    RequestList();
            }
            if (nowMs >= _redrawAtMs)
            {
                _redrawAtMs = nowMs + _config.WindowRefreshMs;
                _window.Redraw();
            }
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

        private void RequestList()
        {
            if ((_feed?.MailRequests?.RequestMailList() ?? 0) != 0)
                _listPending = true;
        }

        private void OnList(MailList list)
        {
            _listPending = false;
            if (_window.SetList(list) is { } refused)
            {
                if (!_retried && _retryAtMs < 0)
                    _retryAtMs = _now() + _config.ListRetryMs;
                _toast.Show(refused, false, _now(), _toastMs);
                return;
            }
            _retried = false;
            _retryAtMs = -1;
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
                Taken(_config, _text, result), _now(), _toastMs);
            if (_window.Open && !_listPending)
                RequestList(); // the one list of this claim; the count after it does not ask again
        }

        /// <summary>A new count while the window is open and nothing is asked: the mailbox changed (a mail arrived or expired).</summary>
        private void OnCount(MailCount count)
        {
            bool changed = count.Open != _window.OpenCount;
            _window.SetCount(count);
            if (changed && _window.Open && _window.PendingClaim == 0 && _window.HasList && !_listPending)
            {
                _retried = false;
                _retryAtMs = -1;
                RequestList();
            }
        }

        private void OnDisconnected(string reasonCode)
        {
            _window.Clear();
            _wasOpen = false;
            _listPending = false;
            _retried = false;
            _retryAtMs = -1;
        }

        /// <summary>
        /// "Demon Ash x3, 50 gold": what a claim moved, from the tables' names; past <see cref="MailConfig.ToastItemsShown"/>
        /// items "a, b, c and N more".
        /// </summary>
        public static string Taken(MailConfig config, UiText text, MailResult result)
        {
            var parts = new List<string>();
            foreach (var item in result.Taken)
                parts.Add(ItemText(config, text, item));
            if (result.GoldTaken > 0)
                parts.Add(text.Get(MailTextIds.Gold, new TextArg("gold", result.GoldTaken.ToString(CultureInfo.InvariantCulture))));
            int shown = Math.Max(1, config.ToastItemsShown);
            if (parts.Count <= shown)
                return string.Join(", ", parts);
            return text.Get(MailTextIds.ToastMore, new TextArg("list", string.Join(", ", parts.GetRange(0, shown))),
                new TextArg("count", (parts.Count - shown).ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>"Demon Ash x3" (the item's <c>UiStrings</c> name; its net id when the tables lack it).</summary>
        public static string AttachmentText(MailConfig config, UiText text, int itemNetId, int count)
        {
            var item = config.Item(itemNetId);
            string name = item == null ? "#" + itemNetId.ToString(CultureInfo.InvariantCulture) : text.Get(item.NameId);
            return text.Get(MailTextIds.Attachment, new TextArg("name", name), new TextArg("count", count.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>A header attachment: "Demon Ash x3", with its rarity above common and the stolen mark ("Iron Sword x1 (Rare, Stolen)").</summary>
        public static string ItemText(MailConfig config, UiText text, MailAttachmentEntry item) =>
            Details(config, text, AttachmentText(config, text, item.ItemNetId, item.Count), item.Rarity, item.Stolen, 0,
                Array.Empty<ushort>());

        /// <summary>
        /// An item of a mail's content: as <see cref="ItemText(MailConfig, UiText, MailAttachmentEntry)"/>, with the enchant level on
        /// its name ("Forged Sword +7 x1"), its quality and each socketed gem with its stat (the inventory tooltip's texts).
        /// </summary>
        public static string ItemText(MailConfig config, UiText text, InventorySlotEntry item)
        {
            string line = AttachmentText(config, text, item.ItemNetId, item.Count);
            if (item.Enchant > 0)
            {
                var def = config.Item(item.ItemNetId);
                string name = def == null ? "#" + item.ItemNetId.ToString(CultureInfo.InvariantCulture) : text.Get(def.NameId);
                line = text.Get(MailTextIds.Attachment, new TextArg("name", text.Get(EnchantTextIds.TooltipName, new TextArg("name", name),
                    new TextArg("level", NumberText.Of(item.Enchant)))), new TextArg("count", NumberText.Of(item.Count)));
            }
            return Details(config, text, line, item.Rarity, item.Stolen, item.Quality, item.Gems);
        }

        private static string Details(MailConfig config, UiText text, string line, byte rarity, bool stolen, int quality, ushort[] gems)
        {
            var details = new List<string>();
            if (rarity > 0)
            {
                string rarityId = config.Inventory.RarityTextId(rarity);
                details.Add(rarityId.Length > 0 ? text.Get(rarityId) : NumberText.Of(rarity));
            }
            if (stolen)
                details.Add(text.Get(InventoryTextIds.TooltipStolen));
            if (quality > 0)
                details.Add(text.Get(InventoryTextIds.TooltipQuality, new TextArg("quality", NumberText.Of(quality))));
            foreach (ushort gem in gems)
            {
                if (config.Inventory.Item(gem) is { } info)
                    details.Add(Forge.GemPanelPresenter.GemLine(text, config.Inventory.Gems, info.Def.Id, GemTextIds.TooltipGem));
            }
            return details.Count == 0 ? line : text.Get(MailTextIds.ItemDetails, new TextArg("item", line),
                new TextArg("details", string.Join(", ", details)));
        }
    }
}
