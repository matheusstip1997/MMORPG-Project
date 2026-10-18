#nullable enable
using System;
using System.Linq;
using Game.UI.Auth;
using Game.UI.Config;
using Game.UI.Login;
using Game.UI.ViewModels;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol;
using Shared.Protocol.Auth;
using static Game.UI.Tests.LoginTestData;

namespace Game.UI.Tests
{
    /// <summary>Navigation and calls of the login screens (spec 9.10) with a fake Auth API and a fake game connection.</summary>
    public sealed class LoginFlowTests
    {
        private static readonly Guid RequestId = new Guid("11111111-2222-3333-4444-555555555555");

        private FakeAuthApi _auth = null!;
        private FakeTickets _tickets = null!;
        private FakeConnector _connector = null!;
        private LoginFlow _flow = null!;

        [SetUp]
        public void SetUp()
        {
            _auth = new FakeAuthApi();
            _tickets = new FakeTickets();
            _connector = new FakeConnector();
            _flow = new LoginFlow(TestConfig(Server(), Server("down", 2, ServerStatus.Maintenance)), _auth, _tickets, _connector, "0.1.0",
                () => RequestId);
        }

        private void ToCharacterSelect()
        {
            Done(_flow.SignInAsync());
            _flow.Continue();
            Done(_flow.EnterServerAsync());
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.CharacterSelect));
        }

        [Test]
        public void Sign_in_shows_the_badge_and_any_button_continues_to_the_servers()
        {
            Done(_flow.SignInAsync());

            Assert.That(_flow.Title.State, Is.EqualTo(TitleState.Ready));
            Assert.That(_flow.Title.BadgeTextId, Is.EqualTo(UiTextIds.BadgeDev));
            Assert.That(_flow.Title.BadgeArg.Value, Is.EqualTo("7"));
            Assert.That(_auth.Calls, Is.EqualTo(new[] { "login dev:7" }));

            _flow.Continue();

            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.ServerSelect));
            Assert.That(_flow.Servers.Rows.Count, Is.EqualTo(2));
            Assert.That(_flow.Servers.Rows[0].YourCharacters, Is.EqualTo(1), "from the login response, same realm");
            Assert.That(_flow.Servers.Rows[1].YourCharacters, Is.EqualTo(-1), "other realm: unknown");
            Assert.That(_flow.Servers.SelectedIndex, Is.EqualTo(0));
        }

        [Test]
        public void A_refused_login_stays_on_the_title_and_any_button_retries()
        {
            _auth.Logins.Enqueue(AuthCall<LoginResponse>.Refused(nameof(AuthReasonCode.InvalidTicket)));

            Done(_flow.SignInAsync());

            Assert.That(_flow.Title.State, Is.EqualTo(TitleState.Failed));
            Assert.That(_flow.Title.Status.ReasonCode, Is.EqualTo(nameof(AuthReasonCode.InvalidTicket)));
            _flow.Continue();
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.Title));
            Assert.That(_flow.Title.State, Is.EqualTo(TitleState.Ready), "the retry signed in");
            Assert.That(_auth.Calls.Count(c => c.StartsWith("login")), Is.EqualTo(2));
        }

        [Test]
        public void No_steam_ticket_shows_steam_unavailable_without_calling_the_api()
        {
            _tickets.Next = LoginTicket.Failed(ClientReasonCode.SteamUnavailable);

            Done(_flow.SignInAsync());

            Assert.That(_flow.Title.Status.ReasonCode, Is.EqualTo(nameof(ClientReasonCode.SteamUnavailable)));
            Assert.That(_auth.Calls, Is.Empty);
        }

        [Test]
        public void Enter_server_lists_the_characters_into_the_slots()
        {
            _auth.DefaultList = List(Card(11, 0), Card(12, 2, "Bea", pending: true, purgeAfter: 1_000_000 + 2 * GracePeriod.MsPerDay + 1));

            ToCharacterSelect();

            var characters = _flow.Characters;
            Assert.That(characters.Slots.Count, Is.EqualTo(4));
            Assert.That(characters.UsedSlots, Is.EqualTo(2));
            Assert.That(characters.Slots[1].Card, Is.Null);
            Assert.That(characters.Slots[2].DeletionDaysLeft, Is.EqualTo(3));
            Assert.That(characters.SelectedSlot, Is.EqualTo(0));
            Assert.That(characters.ServerName, Is.EqualTo("Server dev"));
            Assert.That(_auth.Calls.Last(), Is.EqualTo("list 1"));
        }

        [Test]
        public void A_server_in_maintenance_cannot_be_entered()
        {
            Done(_flow.SignInAsync());
            _flow.Continue();
            _flow.SelectServer(1);

            Done(_flow.EnterServerAsync());

            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.ServerSelect));
            Assert.That(_flow.Servers.CanEnter, Is.False);
            Assert.That(_flow.Servers.Status.ReasonCode, Is.EqualTo(nameof(ClientReasonCode.ServerInMaintenance)));
        }

        [Test]
        public void A_refused_list_shows_its_reason_on_the_server_screen()
        {
            Done(_flow.SignInAsync());
            _flow.Continue();
            _auth.Lists.Enqueue(AuthCall<CharacterListResponse>.Refused(nameof(AuthReasonCode.ServerUnavailable)));

            Done(_flow.EnterServerAsync());

            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.ServerSelect));
            Assert.That(_flow.Servers.Status.ReasonCode, Is.EqualTo(nameof(AuthReasonCode.ServerUnavailable)));
        }

        [TestCase(nameof(AuthReasonCode.AuthTokenExpired))]
        [TestCase(nameof(AuthReasonCode.AuthTokenInvalid))]
        public void An_ended_session_goes_back_to_the_title(string reason)
        {
            ToCharacterSelect();
            _auth.Enters.Enqueue(AuthCall<EnterWorldResult>.Refused(reason));

            Done(_flow.EnterWorldAsync());

            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.Title));
            Assert.That(_flow.Title.State, Is.EqualTo(TitleState.Failed));
            Assert.That(_flow.Title.Status.ReasonCode, Is.EqualTo(reason));
            Assert.That(_auth.SignOuts, Is.GreaterThanOrEqualTo(2), "signed out at sign-in and again here");
        }

        [Test]
        public void Enter_world_connects_with_the_ticket_and_clears_the_session_key()
        {
            ToCharacterSelect();

            Done(_flow.EnterWorldAsync());

            Assert.That(_auth.Calls.Last(), Is.EqualTo("enter 11"));
            Assert.That((_connector.Host, _connector.Port), Is.EqualTo(("127.0.0.1", 7777)));
            Assert.That(_connector.Ticket, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(_connector.SessionKeySeen, Is.EqualTo(FakeAuthApi.Filled(32, 7)));
            Assert.That(_connector.SessionKeyPassed!.All(b => b == 0), Is.True, "R-2.5-02: the caller's copy is cleared");
            Assert.That(_flow.Characters.Status.Kind, Is.EqualTo(StatusKind.Busy));
            Assert.That(_flow.Characters.CanEnter, Is.False);
        }

        [Test]
        public void An_accepted_connection_is_handed_off_and_the_world_loads()
        {
            ToCharacterSelect();
            Done(_flow.EnterWorldAsync());
            bool worldReady = false;
            _flow.WorldReady += () => worldReady = true;

            _connector.Accept();

            Assert.That(_connector.HandedOff, Is.EqualTo((1, 11L)));
            Assert.That(worldReady, Is.True);
        }

        [Test]
        public void A_rejected_connection_shows_the_reject_reason_with_the_server_version()
        {
            ToCharacterSelect();
            Done(_flow.EnterWorldAsync());

            _connector.Fail(nameof(ConnectRejectReason.VersionMismatch), 3);

            Assert.That(_flow.Characters.Status.ReasonCode, Is.EqualTo(nameof(ConnectRejectReason.VersionMismatch)));
            Assert.That(_flow.Characters.Status.ServerProtocolVersion, Is.EqualTo(3));
            Assert.That(_flow.Characters.CanEnter, Is.True, "the player can try again");
        }

        [Test]
        public void A_refused_enter_world_shows_its_reason()
        {
            ToCharacterSelect();
            _auth.Enters.Enqueue(AuthCall<EnterWorldResult>.Refused(nameof(AuthReasonCode.CharacterPendingDeletion)));

            Done(_flow.EnterWorldAsync());

            Assert.That(_flow.Characters.Status.ReasonCode, Is.EqualTo(nameof(AuthReasonCode.CharacterPendingDeletion)));
            Assert.That(_connector.Host, Is.Null);
        }

        [Test]
        public void Delete_needs_confirmation_then_lists_again()
        {
            ToCharacterSelect();

            _flow.RequestDelete();
            Assert.That(_flow.Characters.ConfirmingDelete, Is.True);
            Assert.That(_flow.Characters.CanEnter, Is.False);
            _flow.CancelDelete();
            Assert.That(_flow.Characters.ConfirmingDelete, Is.False);
            Assert.That(_auth.Calls.Any(c => c.StartsWith("delete")), Is.False);

            _flow.RequestDelete();
            _auth.DefaultList = List(Card(11, 0, pending: true, purgeAfter: 1_000_000 + GracePeriod.MsPerDay));
            Done(_flow.ConfirmDeleteAsync());

            Assert.That(_auth.Calls.Skip(_auth.Calls.Count - 2), Is.EqualTo(new[] { "delete 11", "list 1" }));
            Assert.That(_flow.Characters.ConfirmingDelete, Is.False);
            Assert.That(_flow.Characters.SelectedCard!.PendingDeletion, Is.True);
            Assert.That(_flow.Characters.CanDelete, Is.False);
            Assert.That(_flow.Characters.Status.Kind, Is.EqualTo(StatusKind.None));
        }

        [Test]
        public void Confirm_delete_without_the_dialog_does_nothing()
        {
            ToCharacterSelect();

            Done(_flow.ConfirmDeleteAsync());

            Assert.That(_auth.Calls.Any(c => c.StartsWith("delete")), Is.False);
        }

        [Test]
        public void Create_sends_name_class_and_empty_appearance_then_selects_the_new_character()
        {
            ToCharacterSelect();
            _flow.OpenCreate();
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.CharacterCreate));
            Assert.That(_flow.Create.ClassIds, Is.EqualTo(new[] { "warrior", "archer", "mage", "assist" }));
            Assert.That(_flow.Create.CanCreate, Is.False, "no name yet");

            _flow.SetCreateName("Aria");
            _flow.SelectCreateClass(2);
            _auth.DefaultList = List(Card(11, 0), Card(99, 1, "Aria"));
            Done(_flow.CreateAsync());

            var request = _auth.CreateRequests.Single();
            Assert.That((request.RequestId, request.Name, request.InitialClass), Is.EqualTo((RequestId, "Aria", "mage")));
            Assert.That((request.Appearance.Version, request.Appearance.Data.Length), Is.EqualTo((0, 0)));
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.CharacterSelect));
            Assert.That(_flow.Characters.SelectedSlot, Is.EqualTo(1));
        }

        [Test]
        public void A_refused_create_stays_on_the_screen_and_a_retry_reuses_the_request_id()
        {
            ToCharacterSelect();
            _flow.OpenCreate();
            _flow.SetCreateName("Taken");
            _auth.Creates.Enqueue(AuthCall<CreateCharacterResult>.Refused(nameof(AuthReasonCode.NameTaken)));

            Done(_flow.CreateAsync());

            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.CharacterCreate));
            Assert.That(_flow.Create.Status.ReasonCode, Is.EqualTo(nameof(AuthReasonCode.NameTaken)));
            Done(_flow.CreateAsync());
            Assert.That(_auth.CreateRequests.Select(r => r.RequestId).Distinct(), Is.EqualTo(new[] { RequestId }));
        }

        [Test]
        public void Back_from_creation_and_back_to_servers_and_exit()
        {
            ToCharacterSelect();
            _flow.OpenCreate();
            _flow.CancelCreate();
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.CharacterSelect));

            _flow.BackToServers();
            Assert.That(_flow.Screen, Is.EqualTo(LoginScreen.ServerSelect));

            bool exit = false;
            _flow.ExitRequested += () => exit = true;
            _flow.Exit();
            Assert.That(exit, Is.True);
        }

        [Test]
        public void Screen_changes_raise_one_event_each()
        {
            int changes = 0;
            _flow.ScreenChanged += () => changes++;

            ToCharacterSelect();

            Assert.That(changes, Is.EqualTo(2));
        }

        [Test]
        public void Poll_drives_the_connector_and_dispose_releases_it()
        {
            _flow.Poll();
            _flow.Dispose();

            Assert.That(_connector.Polls, Is.EqualTo(1));
            Assert.That(_connector.Disposed, Is.True);
        }
    }
}
