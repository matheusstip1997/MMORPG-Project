#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Game.Net;
using Game.UI.Auth;
using Game.UI.Config;
using Game.UI.Login;
using NUnit.Framework;
using Shared.Data.Tables;
using Shared.Protocol.Auth;

namespace Game.UI.Tests
{
    /// <summary>Test doubles and builders for the login tests. Every fake completes synchronously.</summary>
    internal static class LoginTestData
    {
        public static T Done<T>(Task<T> task)
        {
            Assert.That(task.IsCompleted, Is.True, "fakes complete synchronously");
            return task.Result;
        }

        public static void Done(Task task)
        {
            Assert.That(task.IsCompleted, Is.True, "fakes complete synchronously");
            task.GetAwaiter().GetResult();
        }

        public static ServerDirectory Server(string id = "dev", int realm = 1, ServerStatus status = ServerStatus.Online) => new ServerDirectory
        {
            Id = id, Name = "Server " + id, Region = "Local", RealmId = realm, GameHost = "127.0.0.1", GamePort = 7777, Status = status,
        };

        public static LoginConfig TestConfig(params ServerDirectory[] servers) => new LoginConfig(
            new LoginSettings
            {
                ReferenceWidth = 1280, ReferenceHeight = 720, Scaling = UiScaleMode.FitHeight, AuthApiUrl = "http://127.0.0.1:5080",
                AuthRequestTimeoutMs = 5000, WorldScene = "Assets/World.unity",
            },
            Array.Empty<LoginScreens>(),
            Array.Empty<LoginLayout>(),
            servers.Length > 0 ? servers : new[] { Server() },
            new CharacterCreationConfig
            {
                NameMinLength = 3, NameMaxLength = 16, NameAllowedCharacters = "abc",
                AllowedInitialClasses = new[] { "warrior", "archer", "mage", "assist" },
            },
            new NetSecurity { MaxReliableMessageBytes = 16384, MaxUnreliableMessageBytes = 1200 },
            new UiText(Array.Empty<UiStrings>()),
            new ReasonText(Array.Empty<ReasonCodeStrings>()));

        public static CharacterCard Card(long id, int slot, string name = "Hero", bool pending = false, long purgeAfter = 0) => new CharacterCard
        {
            CharacterId = id, Slot = slot, Name = name, ClassId = "warrior", Level = 1, PendingDeletion = pending, PurgeAfterUtcMs = purgeAfter,
        };

        public static CharacterListResponse List(params CharacterCard[] cards) => new CharacterListResponse
        {
            Ok = true, RealmId = 1, SlotsPerRealm = 4, ServerTimeUtcMs = 1_000_000, Characters = cards,
        };
    }

    internal sealed class FakeTickets : ILoginTicketSource
    {
        public LoginTicket Next = LoginTicket.Issued("dev:7", UiTextIds.BadgeDev, new TextArg("id", "7"));

        public Task<LoginTicket> GetTicketAsync() => Task.FromResult(Next);
    }

    /// <summary>Scripted <see cref="IAuthApi"/>: each call returns the next queued outcome (or a default success).</summary>
    internal sealed class FakeAuthApi : IAuthApi
    {
        public readonly Queue<AuthCall<LoginResponse>> Logins = new Queue<AuthCall<LoginResponse>>();
        public readonly Queue<AuthCall<CharacterListResponse>> Lists = new Queue<AuthCall<CharacterListResponse>>();
        public readonly Queue<AuthCall<CreateCharacterResult>> Creates = new Queue<AuthCall<CreateCharacterResult>>();
        public readonly Queue<AuthCall<DeleteCharacterResult>> Deletes = new Queue<AuthCall<DeleteCharacterResult>>();
        public readonly Queue<AuthCall<EnterWorldResult>> Enters = new Queue<AuthCall<EnterWorldResult>>();
        public readonly List<string> Calls = new List<string>();
        public readonly List<CreateCharacterRequest> CreateRequests = new List<CreateCharacterRequest>();
        public CharacterListResponse DefaultList = LoginTestData.List(LoginTestData.Card(11, 0));
        public int SignOuts;

        public Task<AuthCall<LoginResponse>> LoginAsync(string ticket)
        {
            Calls.Add("login " + ticket);
            return Task.FromResult(Logins.Count > 0 ? Logins.Dequeue()
                : AuthCall<LoginResponse>.Success(new LoginResponse { Ok = true, AccessToken = "token", Characters = DefaultList }));
        }

        public Task<AuthCall<CharacterListResponse>> ListCharactersAsync(int realmId)
        {
            Calls.Add("list " + realmId);
            return Task.FromResult(Lists.Count > 0 ? Lists.Dequeue() : AuthCall<CharacterListResponse>.Success(DefaultList));
        }

        public Task<AuthCall<CreateCharacterResult>> CreateCharacterAsync(int realmId, CreateCharacterRequest request)
        {
            Calls.Add("create " + realmId);
            CreateRequests.Add(request);
            return Task.FromResult(Creates.Count > 0 ? Creates.Dequeue()
                : AuthCall<CreateCharacterResult>.Success(new CreateCharacterResult { Ok = true, CharacterId = 99 }));
        }

        public Task<AuthCall<DeleteCharacterResult>> DeleteCharacterAsync(int realmId, long characterId)
        {
            Calls.Add("delete " + characterId);
            return Task.FromResult(Deletes.Count > 0 ? Deletes.Dequeue()
                : AuthCall<DeleteCharacterResult>.Success(new DeleteCharacterResult { Ok = true }));
        }

        public Task<AuthCall<EnterWorldResult>> EnterWorldAsync(int realmId, long characterId)
        {
            Calls.Add("enter " + characterId);
            return Task.FromResult(Enters.Count > 0 ? Enters.Dequeue()
                : AuthCall<EnterWorldResult>.Success(new EnterWorldResult { Ok = true, Ticket = new byte[] { 1, 2, 3 }, SessionKey = Filled(32, 7) }));
        }

        public void SignOut() => SignOuts++;

        public static byte[] Filled(int length, byte value)
        {
            var bytes = new byte[length];
            for (int i = 0; i < length; i++)
                bytes[i] = value;
            return bytes;
        }
    }

    internal sealed class FakeConnector : IWorldConnector
    {
        public event Action? Connected;
        public event Action<string, ushort>? Failed;

        public string? Host;
        public int Port;
        public byte[]? Ticket;
        public byte[]? SessionKeySeen;
        public byte[]? SessionKeyPassed;
        public (int Realm, long Character)? HandedOff;
        public int Polls;
        public bool Disposed;

        public bool Connecting { get; private set; }

        public void Connect(string host, int port, byte[] ticket, byte[] sessionKey, ClientNetLimits limits)
        {
            Host = host;
            Port = port;
            Ticket = ticket;
            SessionKeyPassed = sessionKey;
            SessionKeySeen = (byte[])sessionKey.Clone();
            Connecting = true;
        }

        public void Poll() => Polls++;

        public void HandOff(int realmId, long characterId) => HandedOff = (realmId, characterId);

        public void Dispose() => Disposed = true;

        public void Accept()
        {
            Connecting = false;
            Connected?.Invoke();
        }

        public void Fail(string reason, ushort serverVersion = 0)
        {
            Connecting = false;
            Failed?.Invoke(reason, serverVersion);
        }
    }

    /// <summary>Scripted <see cref="IAuthHttp"/>: records requests and returns the queued answers.</summary>
    internal sealed class FakeHttp : IAuthHttp
    {
        public readonly Queue<AuthHttpResponse> Answers = new Queue<AuthHttpResponse>();
        public readonly List<AuthHttpRequest> Requests = new List<AuthHttpRequest>();

        public FakeHttp Answer(int status, string body)
        {
            Answers.Enqueue(AuthHttpResponse.Answer(status, body));
            return this;
        }

        public Task<AuthHttpResponse> SendAsync(AuthHttpRequest request)
        {
            Requests.Add(request);
            Assert.That(Answers.Count, Is.GreaterThan(0), "unexpected request " + request.Method + " " + request.Url);
            return Task.FromResult(Answers.Dequeue());
        }
    }
}
