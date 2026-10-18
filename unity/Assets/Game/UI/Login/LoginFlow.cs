#nullable enable
using System;
using System.Threading.Tasks;
using Game.UI.Auth;
using Game.UI.Config;
using Game.UI.ViewModels;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;

namespace Game.UI.Login
{
    public enum LoginScreen
    {
        Title,
        ServerSelect,
        CharacterSelect,
        CharacterCreate,
    }

    /// <summary>
    /// Navigation and Auth API calls of the login screens (spec 9.10 flow). Fills the view models from the responses;
    /// every rule (name, slots, class, pending deletion, tickets) is the server's, and every refusal reaches the
    /// screen as a reason code. An expired or invalid access token sends the player back to the title to sign in again.
    /// Main thread only; the async methods never throw for network problems.
    /// </summary>
    public sealed class LoginFlow : IDisposable
    {
        private readonly LoginConfig _config;
        private readonly IAuthApi _auth;
        private readonly ILoginTicketSource _tickets;
        private readonly IWorldConnector _connector;
        private readonly Func<Guid> _newRequestId;
        private CharacterListResponse? _loginCharacters;
        private ServerDirectory? _server;
        private long _enteringCharacterId;

        public LoginFlow(LoginConfig config, IAuthApi auth, ILoginTicketSource tickets, IWorldConnector connector,
            string clientVersion, Func<Guid>? newRequestId = null)
        {
            _config = config;
            _auth = auth;
            _tickets = tickets;
            _connector = connector;
            _newRequestId = newRequestId ?? Guid.NewGuid;
            Title = new TitleViewModel(clientVersion, ProtocolVersion.Current);
            _connector.Connected += OnWorldConnected;
            _connector.Failed += OnWorldFailed;
        }

        public LoginScreen Screen { get; private set; } = LoginScreen.Title;

        public event Action? ScreenChanged;

        /// <summary>The game connection is accepted and handed off: load the world scene now (R-9.10-05).</summary>
        public event Action? WorldReady;

        /// <summary>Exit game (R-9.10-05): close the client.</summary>
        public event Action? ExitRequested;

        public TitleViewModel Title { get; }

        public ServerListViewModel Servers { get; } = new ServerListViewModel();

        public CharacterSelectViewModel Characters { get; } = new CharacterSelectViewModel();

        public CharacterCreateViewModel Create { get; } = new CharacterCreateViewModel();

        // ---- title (R-9.10-01)

        public async Task SignInAsync()
        {
            _auth.SignOut();
            _loginCharacters = null;
            Title.SetSigningIn();
            var ticket = await _tickets.GetTicketAsync();
            if (!ticket.Ok)
            {
                Title.SetFailed(ticket.Reason);
                return;
            }
            var login = await _auth.LoginAsync(ticket.Ticket);
            if (!login.Ok)
            {
                Title.SetFailed(login.Reason);
                return;
            }
            _loginCharacters = login.Response!.Characters;
            Title.SetSignedIn(ticket.BadgeTextId, ticket.BadgeArg);
        }

        /// <summary>Any button on the title: continue when signed in, try again after a failure.</summary>
        public void Continue()
        {
            if (Screen != LoginScreen.Title)
                return;
            if (Title.State == TitleState.Ready)
            {
                Servers.SetServers(_config.Servers, YourCharacters);
                Servers.SetStatus(LoginStatus.None);
                Show(LoginScreen.ServerSelect);
            }
            else if (Title.State == TitleState.Failed)
            {
                _ = SignInAsync();
            }
        }

        // ---- server selection (R-9.10-02, R-9.10-03)

        public void SelectServer(int index) => Servers.Select(index);

        /// <summary>
        /// Enter server: lists the characters on that realm. v0 has no gateway step before enter world, so the game
        /// connection opens there. TODO(B4-queue): the login queue position when the server is full (R-9.10-03).
        /// </summary>
        public async Task EnterServerAsync()
        {
            var row = Servers.Selected;
            if (row == null || !Servers.CanEnter)
            {
                if (row != null && !row.Selectable)
                    Servers.SetStatus(LoginStatus.Error(ClientReasonCode.ServerInMaintenance.ToString()));
                return;
            }
            Servers.SetStatus(LoginStatus.Busy(UiTextIds.ServerLoading));
            var list = await _auth.ListCharactersAsync(row.Server.RealmId);
            if (!list.Ok)
            {
                if (!SignedOutBy(list.Reason))
                    Servers.SetStatus(LoginStatus.Error(list.Reason));
                return;
            }
            _server = row.Server;
            Servers.SetStatus(LoginStatus.None);
            Characters.SetServer(row.Server.Name, row.Server.RealmId);
            Characters.SetStatus(LoginStatus.None);
            Characters.SetCharacters(list.Response!);
            Show(LoginScreen.CharacterSelect);
        }

        // ---- character selection (R-9.10-04, R-9.10-05)

        public void SelectSlot(int slot) => Characters.Select(slot);

        /// <summary>Back to server selection: nothing is connected yet in v0, and the sign-in is kept.</summary>
        public void BackToServers()
        {
            if (Screen != LoginScreen.CharacterSelect || !Characters.CanNavigate)
                return;
            Servers.SetServers(_config.Servers, YourCharacters);
            Show(LoginScreen.ServerSelect);
        }

        public void Exit()
        {
            if (Characters.Status.IsBusy)
                _connector.Dispose();
            ExitRequested?.Invoke();
        }

        /// <summary>Enter world: the Auth API issues the ticket and session key, then the game connection opens.</summary>
        public async Task EnterWorldAsync()
        {
            var card = Characters.SelectedCard;
            if (card == null || !Characters.CanEnter || _server == null)
                return;
            Characters.SetStatus(LoginStatus.Busy(UiTextIds.Entering));
            var enter = await _auth.EnterWorldAsync(_server.RealmId, card.CharacterId);
            if (!enter.Ok)
            {
                if (!SignedOutBy(enter.Reason))
                    Characters.SetStatus(LoginStatus.Error(enter.Reason));
                return;
            }
            var result = enter.Response!;
            _enteringCharacterId = card.CharacterId;
            _connector.Connect(_server.GameHost, _server.GamePort, result.Ticket, result.SessionKey, _config.NetLimits);
            // R-2.5-02: only the connection's derived keys are kept.
            Array.Clear(result.SessionKey, 0, result.SessionKey.Length);
        }

        /// <summary>Call every frame: drives the game connection while it opens.</summary>
        public void Poll() => _connector.Poll();

        public void RequestDelete() => Characters.BeginDelete();

        public void CancelDelete() => Characters.CancelDelete();

        /// <summary>Soft delete after confirmation (R-2.1-05), then the list again for the grace period on the card.</summary>
        public async Task ConfirmDeleteAsync()
        {
            var card = Characters.SelectedCard;
            if (card == null || !Characters.ConfirmingDelete || _server == null)
                return;
            Characters.EndDeleteConfirmation();
            Characters.SetStatus(LoginStatus.Busy(UiTextIds.CharLoading));
            var delete = await _auth.DeleteCharacterAsync(_server.RealmId, card.CharacterId);
            if (!delete.Ok)
            {
                if (!SignedOutBy(delete.Reason))
                    Characters.SetStatus(LoginStatus.Error(delete.Reason));
                return;
            }
            await RefreshCharactersAsync();
        }

        // ---- character creation placeholder (R-9.10-06)

        public void OpenCreate()
        {
            if (Screen != LoginScreen.CharacterSelect || !Characters.CanNavigate)
                return;
            Create.Open(_config.Creation.AllowedInitialClasses, _config.Creation.NameMaxLength, _newRequestId());
            Show(LoginScreen.CharacterCreate);
        }

        public void SetCreateName(string name) => Create.SetName(name);

        public void SelectCreateClass(int index) => Create.SelectClass(index);

        public void CancelCreate()
        {
            if (Screen != LoginScreen.CharacterCreate || Create.Status.IsBusy)
                return;
            Show(LoginScreen.CharacterSelect);
        }

        public async Task CreateAsync()
        {
            var classId = Create.SelectedClassId;
            if (!Create.CanCreate || classId == null || _server == null)
                return;
            Create.SetStatus(LoginStatus.Busy(UiTextIds.Creating));
            var request = new CreateCharacterRequest
            {
                RequestId = Create.RequestId,
                Name = Create.Name,
                InitialClass = classId,
                // TODO(open): appearance options (spec 9.10); until then the empty version 0 blob the server accepts.
                Appearance = new CharacterAppearance { Version = _config.Creation.AppearanceVersion },
            };
            var created = await _auth.CreateCharacterAsync(_server.RealmId, request);
            if (!created.Ok)
            {
                if (!SignedOutBy(created.Reason))
                    Create.SetStatus(LoginStatus.Error(created.Reason));
                return;
            }
            Create.SetStatus(LoginStatus.None);
            Show(LoginScreen.CharacterSelect);
            if (await RefreshCharactersAsync())
                Characters.SelectCharacter(created.Response!.CharacterId);
        }

        public void Dispose()
        {
            _connector.Connected -= OnWorldConnected;
            _connector.Failed -= OnWorldFailed;
            _connector.Dispose();
        }

        // ---- helpers

        private async Task<bool> RefreshCharactersAsync()
        {
            if (_server == null)
                return false;
            Characters.SetStatus(LoginStatus.Busy(UiTextIds.CharLoading));
            var list = await _auth.ListCharactersAsync(_server.RealmId);
            if (!list.Ok)
            {
                if (!SignedOutBy(list.Reason))
                    Characters.SetStatus(LoginStatus.Error(list.Reason));
                return false;
            }
            Characters.SetCharacters(list.Response!);
            Characters.SetStatus(LoginStatus.None);
            return true;
        }

        private void OnWorldConnected()
        {
            if (_server == null)
                return;
            _connector.HandOff(_server.RealmId, _enteringCharacterId);
            WorldReady?.Invoke();
        }

        private void OnWorldFailed(string reason, ushort serverProtocolVersion) =>
            Characters.SetStatus(LoginStatus.Error(reason, serverProtocolVersion));

        /// <summary>An expired or invalid access token ends the sign-in: back to the title, where any button signs in again.</summary>
        private bool SignedOutBy(string reason)
        {
            if (reason != nameof(AuthReasonCode.AuthTokenExpired) && reason != nameof(AuthReasonCode.AuthTokenInvalid))
                return false;
            _auth.SignOut();
            Servers.SetStatus(LoginStatus.None);
            Characters.SetStatus(LoginStatus.None);
            Create.SetStatus(LoginStatus.None);
            Title.SetFailed(reason);
            Show(LoginScreen.Title);
            return true;
        }

        private int YourCharacters(int realmId) =>
            _loginCharacters != null && _loginCharacters.RealmId == realmId ? _loginCharacters.Characters.Length : -1;

        private void Show(LoginScreen screen)
        {
            if (screen == Screen)
                return;
            Screen = screen;
            ScreenChanged?.Invoke();
        }
    }
}
