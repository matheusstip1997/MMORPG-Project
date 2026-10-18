#nullable enable
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using Game.Gameplay.Entities;
using Game.UI.Auth;
using Game.UI.Config;
using Game.UI.Login;
using NUnit.Framework;
using Shared.Protocol.Auth;
using UnityEngine.TestTools;

namespace Game.UI.Tests
{
    /// <summary>
    /// End to end against a locally running Auth API (dev login) and GameServer with the same ticket secret:
    /// dev login, list, create, enter world, and the game connection is accepted. Skipped unless
    /// <c>MMORPG_E2E_AUTH_URL</c> is set (for example <c>http://127.0.0.1:5080</c>); the game server address is the
    /// first <c>ServerDirectory</c> row. Setup: unity/Assets/Game/UI/README.md.
    /// </summary>
    public sealed class LoginEndToEndTests
    {
        private const string AuthUrlVariable = "MMORPG_E2E_AUTH_URL";
        private const double TimeoutMs = 20_000;

        [UnityTest]
        public IEnumerator Dev_login_create_enter_world_and_connect()
        {
            var authUrl = Environment.GetEnvironmentVariable(AuthUrlVariable);
            if (string.IsNullOrEmpty(authUrl))
            {
                Assert.Ignore($"{AuthUrlVariable} is not set; see unity/Assets/Game/UI/README.md");
                yield break;
            }
            Assert.That(ClientConfig.TryLoad(out var tables, out var error), Is.True, error);
            var config = LoginConfig.From(tables);
            var server = config.Servers[0];
            var api = new AuthApiClient(new UnityWebRequestAuthHttp(config.Settings.AuthRequestTimeoutMs), authUrl);
            var random = new System.Random();
            long devId = 1_000_000 + random.Next(1, 1_000_000);

            var login = api.LoginAsync(DevLoginTicketSource.Prefix + devId.ToString(CultureInfo.InvariantCulture));
            while (!login.IsCompleted) yield return null;
            Assert.That(login.Result.Reason, Is.Empty, "login (is the Auth API running with MMORPG_AUTH_IDENTITY=dev?)");

            var name = "Etoe" + Letters(random, 8);
            var create = api.CreateCharacterAsync(server.RealmId, new CreateCharacterRequest
            {
                RequestId = Guid.NewGuid(), Name = name, InitialClass = config.Creation.AllowedInitialClasses[0],
                Appearance = new CharacterAppearance { Version = config.Creation.AppearanceVersion },
            });
            while (!create.IsCompleted) yield return null;
            Assert.That(create.Result.Reason, Is.Empty, "create");

            var list = api.ListCharactersAsync(server.RealmId);
            while (!list.IsCompleted) yield return null;
            Assert.That(list.Result.Reason, Is.Empty, "list");
            Assert.That(Array.Exists(list.Result.Response!.Characters, c => c.Name == name), Is.True);

            var enter = api.EnterWorldAsync(server.RealmId, create.Result.Response!.CharacterId);
            while (!enter.IsCompleted) yield return null;
            Assert.That(enter.Result.Reason, Is.Empty, "enter world");

            using var connector = new ClientConnectionWorldConnector();
            bool connected = false;
            string failure = "";
            connector.Connected += () => connected = true;
            connector.Failed += (reason, _) => failure = reason;
            var key = enter.Result.Response!.SessionKey;
            connector.Connect(server.GameHost, server.GamePort, enter.Result.Response.Ticket, key, config.NetLimits);
            Array.Clear(key, 0, key.Length);

            var clock = Stopwatch.StartNew();
            while (!connected && failure.Length == 0 && clock.Elapsed.TotalMilliseconds < TimeoutMs)
            {
                connector.Poll();
                yield return null;
            }
            Assert.That(failure, Is.Empty, "game connection (same MMORPG_TICKET_SECRET and key id on both?)");
            Assert.That(connected, Is.True, "game connection timed out");

            var delete = api.DeleteCharacterAsync(server.RealmId, create.Result.Response.CharacterId);
            while (!delete.IsCompleted) yield return null;
        }

        private static string Letters(System.Random random, int count)
        {
            var letters = new char[count];
            for (int i = 0; i < count; i++)
                letters[i] = (char)('a' + random.Next(26));
            return new string(letters);
        }
    }
}
