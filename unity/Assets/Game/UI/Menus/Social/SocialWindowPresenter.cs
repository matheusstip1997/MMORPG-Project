#nullable enable
using System;
using System.Collections.Generic;
using Game.UI.Config;
using Game.UI.HUD;
using Game.UI.ViewModels.Social;
using Shared.Protocol;
using UnityEngine.UIElements;

namespace Game.UI.Menus.Social
{
    /// <summary>
    /// Spec 9.13 <c>social_window</c> (spec 6.5 "M4 social", 9.3 SO; WP-M4-16; placeholder art, WP-M4-11 polishes it), placed by its
    /// <c>HudLayout</c> row. The header with the title and close; the Friends and Blocked tabs; a name field with Add (Friends) or
    /// Block and Mute (Blocked); on Friends the incoming requests (Accept, Decline) and every friend with "Online" or "Offline" (the
    /// only thing the server tells of a friend) and Remove; on Blocked every muted or blocked player with Remove. Names are the
    /// server's (<c>DisplayIdentity</c>), shown as written (rich text off); every other text from <c>UiStrings</c>.
    /// </summary>
    public sealed class SocialWindowPresenter : HudPresenter
    {
        private readonly SocialWindowViewModel _model;
        private readonly ISocialWindowActions _actions;
        private readonly List<string> _rowTexts = new List<string>();
        private VisualElement _window = null!;
        private Button _friendsTab = null!;
        private Button _blockedTab = null!;
        private TextField _name = null!;
        private Button _add = null!;
        private Button _block = null!;
        private Button _mute = null!;
        private Label _empty = null!;
        private ScrollView _list = null!;
        private bool _pointerInside;

        public SocialWindowPresenter(HudConfig config, HudView view, SocialWindowViewModel model, ISocialWindowActions actions)
            : base(config, view, model)
        {
            _model = model;
            _actions = actions;
        }

        public bool CapturesPointer => _model.Open && _pointerInside;

        public bool IsShown => _window.style.display == DisplayStyle.Flex;

        public VisualElement Window => _window;

        public TextField NameField => _name;

        public Button AddButton => _add;

        public Button BlockButton => _block;

        public Button MuteButton => _mute;

        public Button FriendsTab => _friendsTab;

        public Button BlockedTab => _blockedTab;

        public bool EmptyShown => _empty.style.display == DisplayStyle.Flex;

        /// <summary>The rows shown, top to bottom (requests first on Friends), as their text.</summary>
        public IReadOnlyList<string> RowTexts => _rowTexts;

        /// <summary>The button named <paramref name="name"/> in the list (<c>social_accept_&lt;ref&gt;</c>, <c>social_remove_&lt;ref&gt;</c>, ...), or null.</summary>
        public Button? ListButton(string name) => _list.Q<Button>(name);

        protected override void Build()
        {
            _window = View.Place(HudElementIds.SocialWindow, HudWidgets.Panel("social-window", Theme));
            _window.pickingMode = PickingMode.Position;
            _window.style.paddingLeft = 6;
            _window.style.paddingRight = 6;
            _window.style.paddingTop = 4;
            _window.style.paddingBottom = 4;
            _window.RegisterCallback<PointerEnterEvent>(_ => _pointerInside = true);
            _window.RegisterCallback<PointerLeaveEvent>(_ => _pointerInside = false);

            // WP-M4-11 (spec 9.13): the shared window header, its close labelled for screen readers.
            _window.Add(WindowChrome.Header(Config, Config.Text.Get(SocialTextIds.Title), "social_close", SocialTextIds.Close,
                SocialTextIds.CloseLabel, () => _actions.CloseWindow(), out _, out _));

            var tabs = Row("social-tabs");
            _friendsTab = Button("social_tab_friends", Config.Text.Get(SocialTextIds.TabFriends), () => _actions.SelectTab(SocialTab.Friends));
            _blockedTab = Button("social_tab_blocked", Config.Text.Get(SocialTextIds.TabBlocked), () => _actions.SelectTab(SocialTab.Blocked));
            tabs.Add(_friendsTab);
            tabs.Add(_blockedTab);
            _window.Add(tabs);

            var entry = Row("social-entry");
            _name = WindowChrome.Field(new TextField { name = "social_name", value = string.Empty, maxLength = ChatText.MaxNameChars }, Theme); // WP-M4-11 (D3)
            _name.label = Config.Text.Get(SocialTextIds.NameLabel);
            _name.style.flexGrow = 1;
            _name.RegisterValueChangedCallback(change => _actions.SetDraft(change.newValue));
            _name.RegisterCallback<FocusInEvent>(_ => _actions.FocusField(true));
            _name.RegisterCallback<FocusOutEvent>(_ => _actions.FocusField(false));
            entry.Add(_name);
            _add = Button("social_add", Config.Text.Get(SocialTextIds.Add), () => _actions.AddFriend());
            _block = Button("social_block", Config.Text.Get(SocialTextIds.Block), () => _actions.BlockTyped());
            _mute = Button("social_mute", Config.Text.Get(SocialTextIds.Mute), () => _actions.MuteTyped());
            entry.Add(_add);
            entry.Add(_block);
            entry.Add(_mute);
            _window.Add(entry);

            _empty = Text("", "social-empty");
            _window.Add(_empty);
            _list = new ScrollView(ScrollViewMode.Vertical) { name = "social_list" };
            _list.style.flexGrow = 1;
            _list.style.flexShrink = 1;
            _list.pickingMode = PickingMode.Position;
            _window.Add(_list);
        }

        protected override void Refresh()
        {
            Show(_window, _model.Open);
            if (!_model.Open)
            {
                _pointerInside = false; // a hidden window captures nothing (no leave event comes)
                return;
            }
            bool friends = _model.Tab == SocialTab.Friends;
            HudWidgets.Border(_friendsTab, friends ? Theme.Accent : Theme.Border);
            HudWidgets.Border(_blockedTab, friends ? Theme.Border : Theme.Accent);
            if (_name.value != _model.Draft)
                _name.SetValueWithoutNotify(_model.Draft);
            Show(_add, friends);
            Show(_block, !friends);
            Show(_mute, !friends);
            _add.SetEnabled(_model.CanSend);
            _block.SetEnabled(_model.CanSend);
            _mute.SetEnabled(_model.CanSend);

            // The rows are rebuilt on a change only (a list of at most a few dozen; the window's model changes rarely).
            _list.Clear();
            _rowTexts.Clear();
            if (friends)
            {
                foreach (var request in _model.Incoming)
                {
                    uint reference = request.Ref;
                    AddRow(Config.Text.Get(SocialTextIds.Request, new TextArg("name", request.Name)),
                        Button("social_accept_" + reference, Config.Text.Get(SocialTextIds.Accept), () => _actions.Accept(reference)),
                        Button("social_decline_" + reference, Config.Text.Get(SocialTextIds.Decline), () => _actions.Decline(reference)));
                }
                foreach (var friend in _model.Friends)
                {
                    uint reference = friend.Ref;
                    AddRow(Config.Text.Get(friend.Online ? SocialTextIds.FriendOnline : SocialTextIds.FriendOffline,
                            new TextArg("name", friend.Name)),
                        Button("social_remove_" + reference, Config.Text.Get(SocialTextIds.Remove), () => _actions.RemoveFriend(reference)));
                }
                ShowEmpty(_model.Incoming.Count == 0 && _model.Friends.Count == 0, SocialTextIds.NoFriends);
            }
            else
            {
                foreach (var block in _model.Blocks)
                {
                    uint reference = block.Ref;
                    AddRow(Config.Text.Get(block.Kind == SocialBlockKind.Mute ? SocialTextIds.Muted : SocialTextIds.Blocked,
                            new TextArg("name", block.Name)),
                        Button("social_unblock_" + reference, Config.Text.Get(SocialTextIds.Remove), () => _actions.Unblock(reference)));
                }
                ShowEmpty(_model.Blocks.Count == 0, SocialTextIds.NoBlocks);
            }
        }

        private void ShowEmpty(bool empty, string textId)
        {
            Show(_empty, empty);
            if (empty)
                HudWidgets.SetText(_empty, Config.Text.Get(textId));
        }

        private void AddRow(string text, params Button[] buttons)
        {
            var row = Row("social-row");
            var label = Text(text, "social-row__text");
            label.style.flexGrow = 1;
            label.style.flexShrink = 1;
            label.style.whiteSpace = WhiteSpace.Normal;
            row.Add(label);
            foreach (var button in buttons)
                row.Add(button);
            _list.Add(row);
            _rowTexts.Add(text);
        }

        private static VisualElement Row(string className)
        {
            var row = HudWidgets.Box(className);
            row.style.flexDirection = FlexDirection.Row;
            row.pickingMode = PickingMode.Position;
            return row;
        }

        private Label Text(string text, string className)
        {
            var label = HudWidgets.Label(text, className);
            label.enableRichText = false; // names come from the server: shown as written, never as markup
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
    }
}
