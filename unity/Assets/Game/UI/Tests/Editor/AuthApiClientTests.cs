#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Game.UI.Auth;
using NUnit.Framework;
using Shared.Protocol;
using Shared.Protocol.Auth;
using static Game.UI.Tests.LoginTestData;

namespace Game.UI.Tests
{
    /// <summary>The Auth API client against a fake HTTP handler (spec 9.10 "Auth API v0"): JSON as AuthApi writes it.</summary>
    public sealed class AuthApiClientTests
    {
        private const string Base = "http://127.0.0.1:5080";
        private const string Card = "{\"characterId\":11,\"slot\":2,\"name\":\"Aria\",\"classId\":\"mage\",\"level\":3,\"pendingDeletion\":true,\"purgeAfterUtcMs\":5000}";
        private const string ListBody = "{\"realmId\":1,\"slotsPerRealm\":4,\"serverTimeUtcMs\":1000,\"characters\":[" + Card + "],\"ok\":true,\"reasonCode\":\"None\"}";
        private const string LoginBody = "{\"accessToken\":\"abc.def\",\"accessTokenExpiresAtUtcMs\":99,\"accountId\":5,\"characters\":" + ListBody + ",\"ok\":true,\"reasonCode\":\"None\"}";

        private static string Refusal(string reason) => "{\"ok\":false,\"reasonCode\":\"" + reason + "\"}";

        private static (AuthApiClient Client, FakeHttp Http) SignedIn()
        {
            var http = new FakeHttp().Answer(200, LoginBody);
            var client = new AuthApiClient(http, Base);
            Assert.That(Done(client.LoginAsync("dev:1")).Ok, Is.True);
            return (client, http);
        }

        [Test]
        public void Login_posts_the_ticket_and_reads_token_and_characters()
        {
            var http = new FakeHttp().Answer(200, LoginBody);
            var client = new AuthApiClient(http, Base + "/");

            var call = Done(client.LoginAsync("dev:\"1\""));

            Assert.That(call.Ok, Is.True);
            Assert.That(call.Response!.AccessToken, Is.EqualTo("abc.def"));
            Assert.That(call.Response.AccountId, Is.EqualTo(5));
            var card = call.Response.Characters!.Characters.Single();
            Assert.That((card.CharacterId, card.Slot, card.Name, card.ClassId, card.Level, card.PendingDeletion, card.PurgeAfterUtcMs),
                Is.EqualTo((11L, 2, "Aria", "mage", 3, true, 5000L)));
            var request = http.Requests.Single();
            Assert.That((request.Method, request.Url, request.BearerToken), Is.EqualTo(("POST", Base + "/v1/login", (string?)null)));
            Assert.That(request.JsonBody, Is.EqualTo("{\"ticket\":\"dev:\\\"1\\\"\"}"));
            Assert.That(client.SignedIn, Is.True);
        }

        [Test]
        public void Calls_after_login_send_the_bearer_token_to_the_realm_path()
        {
            var (client, http) = SignedIn();
            http.Answer(200, ListBody);

            var list = Done(client.ListCharactersAsync(1));

            Assert.That(list.Ok, Is.True);
            Assert.That(list.Response!.SlotsPerRealm, Is.EqualTo(4));
            Assert.That(list.Response.ServerTimeUtcMs, Is.EqualTo(1000));
            var request = http.Requests.Last();
            Assert.That((request.Method, request.Url, request.BearerToken), Is.EqualTo(("GET", Base + "/v1/realms/1/characters", "abc.def")));
        }

        [Test]
        public void Create_sends_the_contract_fields()
        {
            var (client, http) = SignedIn();
            http.Answer(200, "{\"characterId\":42,\"character\":" + Card + ",\"ok\":true,\"reasonCode\":\"None\"}");
            var id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");

            var call = Done(client.CreateCharacterAsync(1, new CreateCharacterRequest
            {
                RequestId = id, Name = "Aria", InitialClass = "mage", Appearance = new CharacterAppearance { Version = 0 },
            }));

            Assert.That(call.Ok, Is.True);
            Assert.That(call.Response!.CharacterId, Is.EqualTo(42));
            Assert.That(call.Response.Character!.Name, Is.EqualTo("Aria"));
            var request = http.Requests.Last();
            Assert.That((request.Method, request.Url), Is.EqualTo(("POST", Base + "/v1/realms/1/characters")));
            Assert.That(request.JsonBody, Is.EqualTo(
                "{\"requestId\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"name\":\"Aria\",\"initialClass\":\"mage\",\"appearance\":{\"version\":0,\"data\":\"\"}}"));
        }

        [Test]
        public void Delete_and_enter_world_use_the_character_paths()
        {
            var (client, http) = SignedIn();
            var key = Convert.ToBase64String(FakeAuthApi.Filled(32, 9));
            http.Answer(200, "{\"purgeAfterUtcMs\":777,\"ok\":true,\"reasonCode\":\"None\"}")
                .Answer(200, "{\"ticket\":\"AQID\",\"sessionKey\":\"" + key + "\",\"ticketExpiresAtUtcMs\":60,\"ok\":true,\"reasonCode\":\"None\"}");

            var delete = Done(client.DeleteCharacterAsync(1, 11));
            var enter = Done(client.EnterWorldAsync(1, 11));

            Assert.That(delete.Response!.PurgeAfterUtcMs, Is.EqualTo(777));
            Assert.That(enter.Response!.Ticket, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(enter.Response.SessionKey, Is.EqualTo(FakeAuthApi.Filled(32, 9)));
            Assert.That(http.Requests[1].Method + " " + http.Requests[1].Url, Is.EqualTo("DELETE " + Base + "/v1/realms/1/characters/11"));
            Assert.That(http.Requests[2].Method + " " + http.Requests[2].Url, Is.EqualTo("POST " + Base + "/v1/realms/1/characters/11/enter"));
        }

        private static IEnumerable<AuthReasonCode> ServerReasons() =>
            Enum.GetValues(typeof(AuthReasonCode)).Cast<AuthReasonCode>().Where(r => r != AuthReasonCode.None);

        [TestCaseSource(nameof(ServerReasons))]
        public void Every_server_refusal_reaches_the_caller_as_its_code(AuthReasonCode reason)
        {
            var (client, http) = SignedIn();
            http.Answer(409, Refusal(reason.ToString()));

            var call = Done(client.CreateCharacterAsync(1, new CreateCharacterRequest { RequestId = Guid.NewGuid() }));

            Assert.That(call.Ok, Is.False);
            Assert.That(call.Reason, Is.EqualTo(reason.ToString()));
            Assert.That(call.Is(reason), Is.True);
            Assert.That(call.Response, Is.Null);
        }

        [TestCaseSource(nameof(ServerReasons))]
        public void Every_login_refusal_reaches_the_caller_and_keeps_no_token(AuthReasonCode reason)
        {
            var http = new FakeHttp().Answer(401, Refusal(reason.ToString()));
            var client = new AuthApiClient(http, Base);

            var call = Done(client.LoginAsync("dev:1"));

            Assert.That(call.Reason, Is.EqualTo(reason.ToString()));
            Assert.That(client.SignedIn, Is.False);
        }

        [Test]
        public void A_code_newer_than_the_client_is_kept_for_the_text_lookup()
        {
            var (client, http) = SignedIn();
            http.Answer(409, Refusal("SomethingNew"));

            Assert.That(Done(client.ListCharactersAsync(1)).Reason, Is.EqualTo("SomethingNew"));
        }

        [Test]
        public void Transport_failure_is_auth_api_unreachable()
        {
            var http = new FakeHttp();
            http.Answers.Enqueue(AuthHttpResponse.Failed("Cannot connect to destination host"));
            var client = new AuthApiClient(http, Base);

            Assert.That(Done(client.LoginAsync("dev:1")).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthApiUnreachable)));
        }

        [TestCase("<html>502 Bad Gateway</html>")]
        [TestCase("")]
        [TestCase("[1,2]")]
        [TestCase("{\"ok\":\"yes\",\"reasonCode\":\"None\"}")]
        [TestCase("{\"ok\":false,\"reasonCode\":\"\"}")]
        [TestCase("{\"ok\":true,\"reasonCode\":\"NameTaken\"}")]
        [TestCase("{\"ok\":true,\"reasonCode\":\"None\",\"accessToken\":\"\"}")]
        [TestCase("{\"ok\":true,\"reasonCode\":\"None\",\"accessToken\":5}")]
        public void Unreadable_answers_are_auth_response_invalid(string body)
        {
            var client = new AuthApiClient(new FakeHttp().Answer(200, body), Base);

            Assert.That(Done(client.LoginAsync("dev:1")).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthResponseInvalid)));
        }

        [Test]
        public void Ok_with_an_error_status_is_not_trusted()
        {
            var client = new AuthApiClient(new FakeHttp().Answer(500, LoginBody), Base);

            Assert.That(Done(client.LoginAsync("dev:1")).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthResponseInvalid)));
        }

        [TestCase("", 32)]
        [TestCase("AQID", 16)]
        public void Enter_world_with_a_missing_ticket_or_wrong_key_size_is_invalid(string ticket, int keyBytes)
        {
            var (client, http) = SignedIn();
            var key = Convert.ToBase64String(new byte[keyBytes]);
            http.Answer(200, "{\"ticket\":\"" + ticket + "\",\"sessionKey\":\"" + key + "\",\"ok\":true,\"reasonCode\":\"None\"}");

            Assert.That(Done(client.EnterWorldAsync(1, 11)).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthResponseInvalid)));
        }

        [Test]
        public void Bad_base64_in_enter_world_is_invalid()
        {
            var (client, http) = SignedIn();
            http.Answer(200, "{\"ticket\":\"***\",\"sessionKey\":\"***\",\"ok\":true,\"reasonCode\":\"None\"}");

            Assert.That(Done(client.EnterWorldAsync(1, 11)).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthResponseInvalid)));
        }

        [TestCase("http://192.168.1.20:5080")]
        [TestCase("http://auth.example.com")]
        public void Plain_http_off_loopback_is_refused_before_sending(string url)
        {
            var http = new FakeHttp();
            var client = new AuthApiClient(http, url);

            Assert.That(Done(client.LoginAsync("dev:1")).Reason, Is.EqualTo(nameof(ClientReasonCode.AuthEndpointInsecure)));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public void Calls_before_login_are_refused_without_a_request()
        {
            var http = new FakeHttp();
            var client = new AuthApiClient(http, Base);

            Assert.That(Done(client.ListCharactersAsync(1)).Reason, Is.EqualTo(nameof(AuthReasonCode.AuthTokenInvalid)));
            Assert.That(http.Requests, Is.Empty);
        }

        [Test]
        public void Sign_out_forgets_the_token()
        {
            var (client, _) = SignedIn();

            client.SignOut();

            Assert.That(client.SignedIn, Is.False);
        }
    }
}
