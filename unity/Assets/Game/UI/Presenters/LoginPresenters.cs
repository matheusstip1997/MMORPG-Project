#nullable enable
using System;
using System.Globalization;
using System.Threading.Tasks;
using Game.UI.Config;
using Game.UI.Login;
using Game.UI.ViewModels;
using Shared.Data.Tables;
using UnityEngine.UIElements;

namespace Game.UI.Presenters
{
    /// <summary>
    /// Base of the login presenters (spec 9.9): builds a screen from <c>LoginLayout</c>, redraws when its view models
    /// raise <c>Changed</c>, and forwards clicks to <see cref="LoginFlow"/>. All text comes from <c>UiStrings</c> and
    /// <c>ReasonCodeStrings</c>.
    /// </summary>
    public abstract class LoginPresenter : IDisposable
    {
        private readonly ViewModel[] _sources;

        protected LoginPresenter(LoginConfig config, LoginFlow flow, string screen, params ViewModel[] sources)
        {
            Config = config;
            Flow = flow;
            View = new LoginScreenView(config, screen);
            _sources = sources;
        }

        public LoginScreenView View { get; }

        protected LoginConfig Config { get; }

        protected LoginFlow Flow { get; }

        protected UiText Text => Config.Text;

        protected string ClientProtocol => Flow.Title.ProtocolVersion.ToString(CultureInfo.InvariantCulture);

        /// <summary>Builds the elements, subscribes, and draws once.</summary>
        public void Bind()
        {
            Build();
            foreach (var source in _sources)
                source.Changed += Refresh;
            Refresh();
        }

        public void Dispose()
        {
            foreach (var source in _sources)
                source.Changed -= Refresh;
        }

        protected abstract void Build();

        protected abstract void Refresh();

        /// <summary>Starts a flow call from a click; a bug inside it is logged instead of lost.</summary>
        protected static void Fire(Func<Task> call)
        {
            call().ContinueWith(t => UnityEngine.Debug.LogException(t.Exception), TaskContinuationOptions.OnlyOnFaulted);
        }

        protected Label Badge() => View.Place("steam_badge", LoginWidgets.Label("", "login-badge"));

        protected void ShowBadge(Label badge) => badge.text = Text.Get(Flow.Title.BadgeTextId, Flow.Title.BadgeArg);
    }

    /// <summary>Title screen (spec 9.10, R-9.10-01).</summary>
    public sealed class TitlePresenter : LoginPresenter
    {
        private Label _pressAnyButton = null!;
        private Label _message = null!;
        private Label _badge = null!;
        private Label _version = null!;

        public TitlePresenter(LoginConfig config, LoginFlow flow) : base(config, flow, LoginScreens.Title, flow.Title)
        {
        }

        protected override void Build()
        {
            View.Place("title_art", LoginWidgets.Panel("login-art"));
            View.Place("title_logo", LoginWidgets.Label(Text.Get(UiTextIds.TitleLogo), "login-logo"));
            _pressAnyButton = View.Place("press_any_button", LoginWidgets.Label("", "login-prompt"));
            _message = View.Place("title_message", LoginWidgets.Label("", "login-message"));
            _badge = Badge();
            _version = View.Place("version_label", LoginWidgets.Label("", "login-small"));
        }

        protected override void Refresh()
        {
            var title = Flow.Title;
            _pressAnyButton.text = title.State == TitleState.Ready ? Text.Get(UiTextIds.PressAnyButton)
                : title.State == TitleState.Failed ? Text.Get(UiTextIds.Retry) : "";
            LoginWidgets.ShowStatus(_message, title.Status, Config, ClientProtocol);
            ShowBadge(_badge);
            _version.text = Text.Get(UiTextIds.VersionLabel,
                new TextArg("client", title.ClientVersion), new TextArg("protocol", ClientProtocol));
        }
    }

    /// <summary>Server selection (spec 9.10, R-9.10-02).</summary>
    public sealed class ServerSelectPresenter : LoginPresenter
    {
        private VisualElement _rows = null!;
        private Label _detail = null!;
        private Button _enter = null!;
        private Label _message = null!;
        private Label _badge = null!;

        public ServerSelectPresenter(LoginConfig config, LoginFlow flow)
            : base(config, flow, LoginScreens.ServerSelect, flow.Servers, flow.Title)
        {
        }

        protected override void Build()
        {
            View.Place("server_header", LoginWidgets.Label(Text.Get(UiTextIds.ServerHeader), "login-header"));
            var table = View.Place("server_table", LoginWidgets.Panel("server-table"));
            table.Add(Row(Text.Get(UiTextIds.ColumnName), Text.Get(UiTextIds.ColumnRegion), Text.Get(UiTextIds.ColumnPopulation),
                Text.Get(UiTextIds.ColumnPing), Text.Get(UiTextIds.ColumnYours), Text.Get(UiTextIds.ColumnStatus), "server-row--header"));
            _rows = new VisualElement();
            table.Add(_rows);
            var detail = View.Place("server_detail", LoginWidgets.Panel("server-detail"));
            _detail = LoginWidgets.Label("", "login-body");
            detail.Add(_detail);
            _enter = View.Place("enter_server_button", LoginWidgets.Button(Text.Get(UiTextIds.EnterServer), () => Fire(Flow.EnterServerAsync)));
            _message = View.Place("server_message", LoginWidgets.Label("", "login-message"));
            _badge = Badge();
        }

        protected override void Refresh()
        {
            var servers = Flow.Servers;
            _rows.Clear();
            string unknown = Text.Get(UiTextIds.ValueUnknown);
            for (int i = 0; i < servers.Rows.Count; i++)
            {
                int index = i;
                var row = servers.Rows[i];
                // TODO(B5-serverlist): population label and measured ping (R-9.10-02).
                var line = Row(row.Server.Name, row.Server.Region, unknown, unknown, Yours(row, unknown), StatusName(row.Server.Status),
                    "server_row");
                var button = new Button(() => Flow.SelectServer(index));
                button.AddToClassList("server-row-button");
                button.EnableInClassList("login-selected", i == servers.SelectedIndex);
                button.EnableInClassList("login-unavailable", !row.Selectable);
                button.Add(line);
                _rows.Add(button);
            }

            var selected = servers.Selected;
            _detail.text = selected == null
                ? Text.Get(UiTextIds.ServerDetailNone)
                : Text.Get(UiTextIds.ServerDetail, new TextArg("name", selected.Server.Name), new TextArg("region", selected.Server.Region),
                    new TextArg("status", StatusName(selected.Server.Status)), new TextArg("yours", Yours(selected, unknown)));
            _enter.SetEnabled(servers.CanEnter);
            LoginWidgets.ShowStatus(_message, servers.Status, Config, ClientProtocol);
            ShowBadge(_badge);
        }

        private static string Yours(ServerRowView row, string unknown) =>
            row.YourCharacters < 0 ? unknown : row.YourCharacters.ToString(CultureInfo.InvariantCulture);

        private string StatusName(ServerStatus status) => status switch
        {
            ServerStatus.Full => Text.Get(UiTextIds.StatusFull),
            ServerStatus.Maintenance => Text.Get(UiTextIds.StatusMaintenance),
            _ => Text.Get(UiTextIds.StatusOnline),
        };

        private static VisualElement Row(string name, string region, string population, string ping, string yours, string status, string className)
        {
            var row = new VisualElement();
            row.AddToClassList("server-row");
            row.AddToClassList(className);
            row.Add(Cell(name, "server-cell--name"));
            row.Add(Cell(region, "server-cell--region"));
            row.Add(Cell(population, "server-cell--population"));
            row.Add(Cell(ping, "server-cell--small"));
            row.Add(Cell(yours, "server-cell--small"));
            row.Add(Cell(status, "server-cell--status"));
            return row;
        }

        private static Label Cell(string text, string className)
        {
            var cell = LoginWidgets.Label(text, "server-cell");
            cell.AddToClassList(className);
            return cell;
        }
    }

    /// <summary>Character selection (spec 9.10, R-9.10-04, R-9.10-05) with the delete confirmation.</summary>
    public sealed class CharacterSelectPresenter : LoginPresenter
    {
        private Label _server = null!;
        private Label _counter = null!;
        private VisualElement _slots = null!;
        private Label _detail = null!;
        private Label _message = null!;
        private Button _back = null!;
        private Button _exit = null!;
        private Button _create = null!;
        private Button _delete = null!;
        private Button _enter = null!;
        private VisualElement _confirm = null!;
        private Label _confirmText = null!;

        public CharacterSelectPresenter(LoginConfig config, LoginFlow flow)
            : base(config, flow, LoginScreens.CharacterSelect, flow.Characters)
        {
        }

        protected override void Build()
        {
            _server = View.Place("server_label", LoginWidgets.Label("", "login-header"));
            _counter = View.Place("slot_counter", LoginWidgets.Label("", "login-body"));
            _slots = View.Place("slot_list", LoginWidgets.Panel("slot-list"));
            var preview = View.Place("character_preview", LoginWidgets.Panel("character-preview"));
            // TODO(I): 3D preview of the selected character (spec 9.10); a plain panel until the entity visuals exist.
            preview.Add(LoginWidgets.Label(Text.Get(UiTextIds.Preview), "login-small"));
            var detail = View.Place("character_detail", LoginWidgets.Panel("character-detail"));
            _detail = LoginWidgets.Label("", "login-body");
            detail.Add(_detail);
            _message = View.Place("char_message", LoginWidgets.Label("", "login-message"));
            _back = View.Place("btn_back_server", LoginWidgets.Button(Text.Get(UiTextIds.BackToServers), Flow.BackToServers));
            _exit = View.Place("btn_exit", LoginWidgets.Button(Text.Get(UiTextIds.ExitGame), Flow.Exit));
            _create = View.Place("btn_create", LoginWidgets.Button(Text.Get(UiTextIds.CreateCharacter), Flow.OpenCreate));
            _delete = View.Place("btn_delete", LoginWidgets.Button(Text.Get(UiTextIds.Delete), Flow.RequestDelete));
            _enter = View.Place("btn_enter", LoginWidgets.Button(Text.Get(UiTextIds.EnterWorld), () => Fire(Flow.EnterWorldAsync)));

            _confirm = View.Place("delete_confirm", LoginWidgets.Panel("login-dialog"));
            _confirmText = LoginWidgets.Label("", "login-body");
            _confirm.Add(_confirmText);
            var buttons = new VisualElement();
            buttons.AddToClassList("login-dialog-buttons");
            buttons.Add(LoginWidgets.Button(Text.Get(UiTextIds.DeleteNo), Flow.CancelDelete));
            buttons.Add(LoginWidgets.Button(Text.Get(UiTextIds.DeleteYes), () => Fire(Flow.ConfirmDeleteAsync)));
            _confirm.Add(buttons);
        }

        protected override void Refresh()
        {
            var characters = Flow.Characters;
            _server.text = Text.Get(UiTextIds.ServerLabel, new TextArg("name", characters.ServerName));
            _counter.text = Text.Get(UiTextIds.SlotCounter,
                new TextArg("used", characters.UsedSlots.ToString(CultureInfo.InvariantCulture)),
                new TextArg("total", characters.SlotsPerRealm.ToString(CultureInfo.InvariantCulture)));

            _slots.Clear();
            foreach (var slot in characters.Slots)
            {
                int number = slot.Slot;
                var card = new Button(() => Flow.SelectSlot(number));
                card.AddToClassList("slot-card");
                card.EnableInClassList("login-selected", number == characters.SelectedSlot);
                card.Add(LoginWidgets.Label(CardText(slot), "login-body"));
                if (slot.Card != null && slot.Card.PendingDeletion)
                {
                    card.AddToClassList("slot-card--pending");
                    card.Add(LoginWidgets.Label(Text.Get(UiTextIds.PendingDeletion,
                        new TextArg("days", slot.DeletionDaysLeft.ToString(CultureInfo.InvariantCulture))), "login-warning"));
                }
                _slots.Add(card);
            }

            var selected = characters.SelectedCard;
            _detail.text = selected == null
                ? Text.Get(UiTextIds.DetailNone)
                : Text.Get(UiTextIds.Detail, new TextArg("name", selected.Name), new TextArg("class", Text.ClassName(selected.ClassId)),
                    new TextArg("level", selected.Level.ToString(CultureInfo.InvariantCulture)));

            LoginWidgets.ShowStatus(_message, characters.Status, Config, ClientProtocol);
            _enter.SetEnabled(characters.CanEnter);
            _delete.SetEnabled(characters.CanDelete);
            _create.SetEnabled(characters.CanNavigate);
            _back.SetEnabled(characters.CanNavigate);
            _exit.SetEnabled(!characters.ConfirmingDelete);

            _confirm.style.display = characters.ConfirmingDelete ? DisplayStyle.Flex : DisplayStyle.None;
            if (characters.ConfirmingDelete && selected != null)
                _confirmText.text = Text.Get(UiTextIds.DeleteConfirm, new TextArg("name", selected.Name));
        }

        private string CardText(SlotView slot) => slot.Card == null
            ? Text.Get(UiTextIds.EmptySlot)
            : Text.Get(UiTextIds.Card, new TextArg("name", slot.Card.Name), new TextArg("class", Text.ClassName(slot.Card.ClassId)),
                new TextArg("level", slot.Card.Level.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Character creation placeholder (R-9.10-06): name, initial class, create and back.</summary>
    public sealed class CharacterCreatePresenter : LoginPresenter
    {
        private TextField _name = null!;
        private VisualElement _classes = null!;
        private Label _message = null!;
        private Button _confirm = null!;
        private Button _cancel = null!;

        public CharacterCreatePresenter(LoginConfig config, LoginFlow flow)
            : base(config, flow, LoginScreens.CharacterCreate, flow.Create)
        {
        }

        protected override void Build()
        {
            View.Place("create_header", LoginWidgets.Label(Text.Get(UiTextIds.CreateHeader), "login-header"));
            View.Place("create_note", LoginWidgets.Label(Text.Get(UiTextIds.CreateNote), "login-small")).AddToClassList("login-note");
            _name = View.Place("create_name", new TextField(Text.Get(UiTextIds.CreateNameLabel)));
            _name.AddToClassList("login-text-field");
            _name.RegisterValueChangedCallback(e => Flow.SetCreateName(e.newValue));
            _classes = View.Place("create_class_list", new VisualElement());
            _classes.AddToClassList("class-list");
            _message = View.Place("create_message", LoginWidgets.Label("", "login-message"));
            _cancel = View.Place("btn_create_cancel", LoginWidgets.Button(Text.Get(UiTextIds.CreateCancel), Flow.CancelCreate));
            _confirm = View.Place("btn_create_confirm", LoginWidgets.Button(Text.Get(UiTextIds.CreateConfirm), () => Fire(Flow.CreateAsync)));
        }

        protected override void Refresh()
        {
            var create = Flow.Create;
            _name.maxLength = create.NameMaxLength > 0 ? create.NameMaxLength : -1;
            if (_name.value != create.Name)
                _name.SetValueWithoutNotify(create.Name);
            _name.SetEnabled(!create.Status.IsBusy);

            _classes.Clear();
            for (int i = 0; i < create.ClassIds.Count; i++)
            {
                int index = i;
                var button = LoginWidgets.Button(Text.ClassName(create.ClassIds[i]), () => Flow.SelectCreateClass(index));
                button.EnableInClassList("login-selected", i == create.SelectedClass);
                button.SetEnabled(!create.Status.IsBusy);
                _classes.Add(button);
            }

            LoginWidgets.ShowStatus(_message, create.Status, Config, ClientProtocol);
            _confirm.SetEnabled(create.CanCreate);
            _cancel.SetEnabled(!create.Status.IsBusy);
        }
    }
}
