#nullable enable
using System.Globalization;
using Game.UI.Config;
using Game.UI.ViewModels.Hud;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.HUD
{
    /// <summary>
    /// Spec 9.3 <c>party_frame</c> (WP-M4-2; placeholder art): top left under the resource panel, one row per other member in
    /// join order with the name, "Lv n", the leader mark, the offline mark and the HP bar (the server's percent, refreshed every
    /// <c>PartySettings.stateIntervalMs</c> and on every change); for the leader a Kick and a Lead button per row; a Leave button
    /// under the rows. Hidden outside a party. Clicks on the buttons are not attacks (<see cref="CapturesPointer"/>). Party
    /// members always see each other (R-2.4-03), so a member out of view still has its row.
    /// </summary>
    public sealed class PartyFramePresenter : HudPresenter
    {
        private readonly PartyFrameViewModel _model;
        private readonly PartyBinder _binder;
        private readonly Row[] _rows = new Row[ReliableMessages.MaxPartyMembers - 1];
        private VisualElement _panel = null!;
        private Button _leave = null!;
        private int _pointerInside;

        public PartyFramePresenter(HudConfig config, HudView view, PartyFrameViewModel model, PartyBinder binder) : base(config, view, model)
        {
            _model = model;
            _binder = binder;
        }

        private sealed class Row
        {
            public VisualElement Root = null!;
            public Label Name = null!;
            public Label Level = null!;
            public Label Mark = null!;
            public VisualElement Fill = null!;
            public Button Kick = null!;
            public Button Lead = null!;
            public uint EntityId;
        }

        /// <summary>The pointer is on a button of the frame: its clicks are not attacks.</summary>
        public bool CapturesPointer => _model.InParty && _pointerInside > 0;

        /// <summary>Whether the frame shows now (tests, the QA smoke).</summary>
        public bool Visible => _panel.style.display == DisplayStyle.Flex;

        /// <summary>The shown rows as "name level mark hp%" (tests, the QA smoke).</summary>
        public string[] ShownRows()
        {
            var shown = new System.Collections.Generic.List<string>();
            foreach (var row in _rows)
            {
                if (row.Root.style.display == DisplayStyle.Flex)
                    shown.Add($"{row.Name.text} {row.Level.text} {row.Mark.text}".TrimEnd());
            }
            return shown.ToArray();
        }

        public Button LeaveButton => _leave;

        /// <summary>The Kick and Lead buttons of the row at <paramref name="index"/> (tests).</summary>
        public (Button Kick, Button Lead) ButtonsOf(int index) => (_rows[index].Kick, _rows[index].Lead);

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.PartyFrame, HudWidgets.Panel("hud-party", Theme));
            _panel.style.paddingLeft = 4;
            _panel.style.paddingRight = 4;
            _panel.style.paddingTop = 2;
            _panel.style.paddingBottom = 2;
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = new Row { Root = HudWidgets.Box("hud-party__row") };
                row.Root.style.marginBottom = 3;
                var line = HudWidgets.Box("hud-party__line");
                line.style.flexDirection = FlexDirection.Row;
                line.style.alignItems = Align.Center;
                row.Name = Plain("hud-party__name");
                row.Name.style.flexGrow = 1;
                row.Level = HudWidgets.Label("", "hud-party__level");
                row.Level.style.color = Theme.Locked;
                row.Level.style.marginLeft = 4;
                row.Mark = HudWidgets.Label("", "hud-party__mark");
                row.Mark.style.color = Theme.Accent;
                row.Mark.style.marginLeft = 4;
                int index = i;
                row.Kick = SmallButton(HudTextIds.PartyKick, () => _binder.Kick(_rows[index].EntityId));
                row.Lead = SmallButton(HudTextIds.PartyLead, () => _binder.PassLead(_rows[index].EntityId));
                line.Add(row.Name);
                line.Add(row.Level);
                line.Add(row.Mark);
                line.Add(row.Lead);
                line.Add(row.Kick);
                row.Root.Add(line);
                row.Root.Add(HudWidgets.Bar("hud-party__hp", Theme.Hp, out row.Fill));
                row.Root.style.display = DisplayStyle.None;
                _panel.Add(row.Root);
                _rows[i] = row;
            }
            _leave = SmallButton(HudTextIds.PartyLeave, () => _binder.Leave());
            _leave.name = "party_leave";
            _panel.Add(_leave);
        }

        private Button SmallButton(string textId, System.Action click)
        {
            var button = new Button(click) { focusable = false, text = Config.Text.Get(textId) };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            button.style.marginLeft = 2;
            HudWidgets.Border(button, Theme.Border);
            button.RegisterCallback<PointerEnterEvent>(_ => _pointerInside++);
            button.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = System.Math.Max(0, _pointerInside - 1));
            return button;
        }

        private static Label Plain(string className)
        {
            var label = HudWidgets.Label("", className);
            label.enableRichText = false; // names are shown as sent, never as markup
            return label;
        }

        protected override void Refresh()
        {
            Show(_panel, _model.InParty);
            if (!_model.InParty)
            {
                _pointerInside = 0;
                return;
            }
            var rows = _model.Rows;
            for (int i = 0; i < _rows.Length; i++)
            {
                var row = _rows[i];
                if (i >= rows.Count)
                {
                    row.Root.style.display = DisplayStyle.None;
                    continue;
                }
                var member = rows[i];
                row.EntityId = member.EntityId;
                row.Root.style.display = DisplayStyle.Flex;
                HudWidgets.SetText(row.Name, member.Name);
                row.Name.style.color = member.Online ? Theme.Text : Theme.Locked;
                HudWidgets.SetText(row.Level, Config.Text.Get(HudTextIds.PartyLevel,
                    new TextArg("level", member.Level.ToString(CultureInfo.InvariantCulture))));
                HudWidgets.SetText(row.Mark, member.IsLeader ? Config.Text.Get(HudTextIds.PartyLeader)
                    : member.Online ? "" : Config.Text.Get(HudTextIds.PartyOffline));
                HudWidgets.SetFill(row.Fill, member.HpPercent / 100f);
                Show(row.Kick, _model.OwnIsLeader);
                Show(row.Lead, _model.OwnIsLeader);
            }
        }
    }

    /// <summary>
    /// The party invite toast (<c>party_invite</c>, WP-M4-2; placeholder art): "{name} invites you to a party ({seconds} s)" with
    /// Accept and Decline, top center under the tutorial prompt; it hides when answered or when the invite lapses. Clicks on
    /// its buttons are not attacks (<see cref="CapturesPointer"/>).
    /// </summary>
    public sealed class PartyInvitePresenter : HudPresenter
    {
        private readonly PartyInviteViewModel _model;
        private readonly PartyBinder _binder;
        private VisualElement _panel = null!;
        private Label _text = null!;
        private Button _accept = null!;
        private Button _decline = null!;
        private int _pointerInside;

        public PartyInvitePresenter(HudConfig config, HudView view, PartyInviteViewModel model, PartyBinder binder) : base(config, view, model)
        {
            _model = model;
            _binder = binder;
        }

        public bool CapturesPointer => _model.Visible && _pointerInside > 0;

        /// <summary>The text shown now, empty while hidden (tests, the QA smoke).</summary>
        public string Text => _panel.style.display == DisplayStyle.Flex ? _text.text : "";

        public Button AcceptButton => _accept;

        public Button DeclineButton => _decline;

        protected override void Build()
        {
            _panel = View.Place(HudElementIds.PartyInvite, HudWidgets.Panel("party-invite", Theme));
            _panel.style.flexDirection = FlexDirection.Row;
            _panel.style.alignItems = Align.Center;
            _panel.style.paddingLeft = 8;
            _panel.style.paddingRight = 4;
            HudWidgets.Border(_panel, Theme.Accent);
            _text = HudWidgets.Label("", "party-invite__text");
            _text.enableRichText = false;
            _text.style.color = Theme.Text;
            _text.style.flexGrow = 1;
            _text.style.flexShrink = 1;
            _text.style.whiteSpace = WhiteSpace.Normal;
            _panel.Add(_text);
            _accept = Button(HudTextIds.PartyAccept, () => _binder.Accept(), "party_accept");
            _decline = Button(HudTextIds.PartyDecline, () => _binder.Decline(), "party_decline");
            _panel.Add(_accept);
            _panel.Add(_decline);
        }

        private Button Button(string textId, System.Action click, string name)
        {
            var button = new Button(click) { focusable = false, name = name, text = Config.Text.Get(textId) };
            button.style.color = Theme.Text;
            button.style.backgroundColor = Theme.Panel;
            HudWidgets.Border(button, Theme.Border);
            button.RegisterCallback<PointerEnterEvent>(_ => _pointerInside++);
            button.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = System.Math.Max(0, _pointerInside - 1));
            return button;
        }

        protected override void Refresh()
        {
            Show(_panel, _model.Visible);
            if (!_model.Visible)
            {
                _pointerInside = 0;
                return;
            }
            HudWidgets.SetText(_text, Config.Text.Get(HudTextIds.PartyInvite, new TextArg("name", _model.InviterName),
                new TextArg("seconds", _model.SecondsLeft.ToString(CultureInfo.InvariantCulture))));
        }
    }
}
