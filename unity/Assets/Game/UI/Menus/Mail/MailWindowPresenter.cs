#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Mail;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Mail
{
    /// <summary>
    /// Spec 9.13 <c>mail_window</c> (spec 4.5, 9.3; WP-M4-3), placed by its <c>HudLayout</c> row. The header with the title, the
    /// open count of the cap and close; "mailbox full" at the cap; the list (oldest first, the first to expire), one row per mail
    /// with its subject (a <c>UiStrings</c> id with typed arguments: a number, or an item's name), its gold and the time to its
    /// expiry; the selected mail's items (from its content: the enchant level, rarity, the stolen mark, quality, gems); Take and Take all, disabled while a claim waits for its answer.
    /// A refused list says so (the binder asks again once). Away from a mailbox (WP-M4-14, R-4.5-03) Take and Take all are off and
    /// a line gives the <c>MailboxNotInReach</c> reason text. Text from <c>UiStrings</c>, rich text off for anything the server filled in; the server decides every claim.
    /// </summary>
    public sealed class MailWindowPresenter : HudPresenter
    {
        private const long MsPerHour = 3_600_000L;
        private const long MsPerDay = 24 * MsPerHour;

        private readonly MailConfig _mail;
        private readonly MailWindowViewModel _model;
        private readonly IMailWindowActions _actions;
        private readonly Func<long> _utcNowMs;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<long> _rowMails = new List<long>();
        private VisualElement _window = null!;
        private Label _count = null!;
        private Label _full = null!;
        private Label _empty = null!;
        private Label _unavailable = null!;
        private Label _more = null!;
        private ScrollView _list = null!;
        private Label _detail = null!;
        private Button _claim = null!;
        private Button _claimAll = null!;
        private Label _mailboxHint = null!;
        private bool _pointerInside;

        /// <param name="utcNowMs">The time the expiry countdown is shown against (UTC milliseconds).</param>
        public MailWindowPresenter(HudConfig config, HudView view, MailConfig mail, MailWindowViewModel model, IMailWindowActions actions,
            Func<long> utcNowMs)
            : base(config, view, model)
        {
            _mail = mail;
            _model = model;
            _actions = actions;
            _utcNowMs = utcNowMs;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public int RowCount => _rowMails.Count;

        public Button Row(int index) => _rows[index];

        /// <summary>Scrolls the list to row <paramref name="index"/> (the QA smoke's claim-mail step).</summary>
        public void ScrollTo(int index) => _list.ScrollTo(_rows[index]);

        /// <summary>The text of list row <paramref name="index"/>.</summary>
        public string RowLabel(int index) => _rows[index].Q<Label>(className: "mail-row__text").text;

        public string CountText => _count.text;

        public string DetailText => _detail.text;

        public bool FullShown => _full.style.display == DisplayStyle.Flex;

        public bool UnavailableShown => _unavailable.style.display == DisplayStyle.Flex;

        public bool EmptyShown => _empty.style.display == DisplayStyle.Flex;

        public Button ClaimButton => _claim;

        public Button ClaimAllButton => _claimAll;

        /// <summary>The "take it at a mailbox" line (WP-M4-14) is shown.</summary>
        public bool MailboxHintShown => _mailboxHint.style.display == DisplayStyle.Flex;

        public string MailboxHintText => _mailboxHint.text;

        protected override void Build()
        {
            _window = View.Place(HudElementIds.MailWindow, HudWidgets.Panel("mail-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            var header = HudWidgets.Box("mail-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.justifyContent = Justify.SpaceBetween;
            header.pickingMode = PickingMode.Position;
            var title = HudWidgets.Label(Config.Text.Get(MailTextIds.Title), "mail-title");
            title.style.color = Theme.Accent;
            header.Add(title);
            _count = Text("", "mail-count");
            header.Add(_count);
            var close = Button("mail_close", Config.Text.Get(MailTextIds.Close), () => _actions.CloseMail());
            close.tooltip = Config.Text.Get(MailTextIds.CloseLabel);
            header.Add(close);
            _window.Add(header);

            _full = Text(Config.Text.Get(MailTextIds.Full), "mail-full");
            _full.style.color = Theme.Accent;
            _full.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_full);
            _empty = Text(Config.Text.Get(MailTextIds.Empty), "mail-empty");
            _window.Add(_empty);
            _unavailable = Text(Config.Text.Get(MailTextIds.Unavailable), "mail-unavailable");
            _unavailable.style.whiteSpace = WhiteSpace.Normal;
            _window.Add(_unavailable);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "mail_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
            _more = Text("", "mail-more");
            _window.Add(_more);

            _detail = Text("", "mail-detail");
            _detail.style.whiteSpace = WhiteSpace.Normal;
            _detail.style.marginTop = 4;
            _window.Add(_detail);
            var buttons = HudWidgets.Box("mail-buttons");
            buttons.style.flexDirection = FlexDirection.Row;
            buttons.pickingMode = PickingMode.Position;
            _claim = Button("mail_claim", Config.Text.Get(MailTextIds.Claim), () => _actions.Claim());
            _claimAll = Button("mail_claim_all", Config.Text.Get(MailTextIds.ClaimAll), () => _actions.ClaimAll());
            buttons.Add(_claim);
            buttons.Add(_claimAll);
            _window.Add(buttons);
            // WP-M4-14 (R-4.5-03): away from a mailbox the buttons are off and this line says why (the refusal's own text).
            _mailboxHint = Text(Config.Reasons.Text(nameof(MailReason.MailboxNotInReach)), "mail-mailbox-hint");
            _mailboxHint.name = "mail_hint";
            _mailboxHint.style.whiteSpace = WhiteSpace.Normal;
            _mailboxHint.style.color = Theme.Accent;
            _window.Add(_mailboxHint);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            HudWidgets.SetText(_count, Config.Text.Get(MailTextIds.Count,
                new TextArg("open", Number(_model.OpenCount)), new TextArg("cap", Number(_model.Cap))));
            Show(_full, _model.IsFull);
            Show(_unavailable, _model.ListRefused);
            Show(_empty, _model.HasList && !_model.ListRefused && _model.Mails.Count == 0);
            RefreshRows();
            int more = _model.Total - _model.Mails.Count;
            Show(_more, more > 0);
            if (more > 0)
                HudWidgets.SetText(_more, Config.Text.Get(MailTextIds.More, new TextArg("count", Number(more))));
            HudWidgets.SetText(_detail, DetailFor(_model.SelectedMail));
            _claim.SetEnabled(_model.CanClaim);
            _claimAll.SetEnabled(_model.CanClaimAll);
            Show(_mailboxHint, _model.ShowMailboxHint);
        }

        private void RefreshRows()
        {
            var mails = _model.Mails;
            while (_rows.Count < mails.Count)
            {
                int index = _rows.Count;
                var row = Button("mail_row_" + index, "", () => _actions.SelectMail(_rowMails[index]));
                row.style.flexDirection = FlexDirection.Row;
                var label = Text("", "mail-row__text");
                label.style.flexShrink = 1;
                label.style.whiteSpace = WhiteSpace.Normal;
                row.Add(label);
                _rows.Add(row);
                _list.Add(row);
            }
            _rowMails.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                bool used = i < mails.Count;
                Show(_rows[i], used);
                if (!used)
                    continue;
                var mail = mails[i];
                _rowMails.Add(mail.MailId);
                _rows[i].Q<Label>(className: "mail-row__text").text = RowText(mail);
                HudWidgets.Border(_rows[i], mail.MailId == _model.Selected ? Theme.Accent : Theme.Border);
            }
        }

        /// <summary>"Your listing sold   50 gold   Expires in 29 d".</summary>
        private string RowText(MailHeader mail)
        {
            string text = Subject(mail);
            if (mail.Gold > 0)
                text += "   " + Config.Text.Get(MailTextIds.Gold, new TextArg("gold", Number(mail.Gold)));
            return text + "   " + Expiry(mail.ExpiresUtcMs);
        }

        /// <summary>
        /// The selected mail: its subject, then one line per item and the gold. From the mail's content once it came (the enchant
        /// level, quality and gems of each item), from its header before (rarity and the stolen mark); the hint without a selection.
        /// </summary>
        private string DetailFor(MailHeader? mail)
        {
            if (mail == null)
                return Config.Text.Get(MailTextIds.Hint);
            var lines = new List<string> { Subject(mail) };
            var content = _model.Content;
            if (content != null && content.Header.MailId == mail.MailId)
            {
                foreach (var item in content.Items)
                    lines.Add(MailWindowBinder.ItemText(_mail, Config.Text, item));
            }
            else
            {
                foreach (var item in mail.Items)
                    lines.Add(MailWindowBinder.ItemText(_mail, Config.Text, item));
            }
            if (mail.Gold > 0)
                lines.Add(Config.Text.Get(MailTextIds.Gold, new TextArg("gold", Number(mail.Gold))));
            return string.Join("\n", lines);
        }

        /// <summary>The subject's <c>UiStrings</c> text with its typed arguments filled in (never text from the server).</summary>
        public string Subject(MailHeader mail)
        {
            var args = new TextArg[mail.Args.Length];
            for (int i = 0; i < mail.Args.Length; i++)
            {
                var arg = mail.Args[i];
                string value = arg.Kind == MailArgKind.Item && _mail.Item((int)arg.Value) is { } item
                    ? Config.Text.Get(item.NameId)
                    : Number(arg.Value);
                args[i] = new TextArg(arg.Name, value);
            }
            return Config.Text.Get(mail.SubjectId, args);
        }

        private string Expiry(long expiresUtcMs)
        {
            long left = Math.Max(0, expiresUtcMs - _utcNowMs());
            return left >= MsPerDay
                ? Config.Text.Get(MailTextIds.Expires, new TextArg("days", Number(left / MsPerDay)))
                : Config.Text.Get(MailTextIds.ExpiresHours, new TextArg("hours", Number((left + MsPerHour - 1) / MsPerHour)));
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false; // mail text carries item names and numbers: shown as written, never as markup
            label.style.color = Theme.Text;
            return label;
        }

        private Button Button(string name, string text, Action onClick)
        {
            var button = new Button(onClick) { focusable = false, name = name, text = text };
            button.enableRichText = false;
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            return button;
        }

        private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
