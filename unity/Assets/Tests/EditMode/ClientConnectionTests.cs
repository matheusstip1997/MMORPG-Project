using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Game.Net;
using Game.Net.Dev;
using NUnit.Framework;
using Shared.Protocol;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// Dev ticket parsing (R-2.5-04 bound checks), and a live connection from Unity's runtime to a running
    /// <c>DevConnect serve</c> host. The live test is skipped when no dev ticket file exists or its ticket has expired; the same
    /// <see cref="ClientConnection"/> code runs against the server transport in GameServer.Tests on every build.
    /// </summary>
    public class ClientConnectionTests
    {
        private const string ValidKey = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA="; // bytes 1..32

        private static string TicketJson(string host = "127.0.0.1", int port = 7777, string ticket = "AAECAw==", string key = ValidKey, int maxReliable = 16384) =>
            "{\"host\":\"" + host + "\",\"port\":" + port + ",\"accountId\":5,\"expiresAtMs\":1,\"ticket\":\"" + ticket +
            "\",\"sessionKey\":\"" + key + "\",\"maxReliableMessageBytes\":" + maxReliable + ",\"maxUnreliableMessageBytes\":1200}";

        [Test]
        public void Dev_ticket_file_parses()
        {
            Assert.IsTrue(DevTicket.TryParse(TicketJson(), out var ticket, out var error), error);
            Assert.AreEqual("127.0.0.1", ticket!.Host);
            Assert.AreEqual(7777, ticket.Port);
            Assert.AreEqual(5, ticket.AccountId);
            Assert.AreEqual(new byte[] { 0, 1, 2, 3 }, ticket.Ticket);
            Assert.AreEqual(32, ticket.SessionKey.Length);
            Assert.AreEqual(16384, ticket.Limits.MaxReliableMessageBytes);
            Assert.AreEqual(1200, ticket.Limits.MaxUnreliableMessageBytes);
        }

        [TestCase("not json")]
        [TestCase("{}")]
        public void Unreadable_dev_ticket_file_is_refused(string json)
        {
            Assert.IsFalse(DevTicket.TryParse(json, out _, out var error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void Dev_ticket_fields_are_bound_checked()
        {
            Assert.IsFalse(DevTicket.TryParse(TicketJson(port: 0), out _, out _), "port 0");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(port: 70000), out _, out _), "port too large");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(host: ""), out _, out _), "no host");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(ticket: "%%%"), out _, out _), "ticket not base64");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(ticket: Convert.ToBase64String(new byte[ConnectRequest.MaxTicketLength + 1])), out _, out _), "ticket too long");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(key: "AAECAw=="), out _, out _), "short session key");
            Assert.IsFalse(DevTicket.TryParse(TicketJson(maxReliable: 0), out _, out _), "no caps");
        }

        [Test]
        public void Connection_refuses_to_send_before_connecting()
        {
            using var connection = new ClientConnection(new Recorder(), new ClientNetLimits(1024, 1200));

            Assert.AreEqual(ConnectionState.Disconnected, connection.State);
            Assert.IsFalse(connection.SendReliable(new byte[] { 1 }));
            Assert.IsFalse(connection.SendInput(new byte[] { 1 }));
        }

        [Test]
        public void Connects_to_a_running_dev_test_host()
        {
            if (!DevTicket.TryLoad(DevTicket.DefaultPath, out var ticket, out var error) || ticket == null)
            {
                Assert.Ignore("Start `dotnet run --project src/Tools/DevConnect -- serve` to run this test (" + error + ").");
                return;
            }
            // A running host keeps the ticket fresh; an expired one is left over from a host that is gone.
            if (ticket.ExpiresAtMs <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                Assert.Ignore("The dev ticket has expired, so no DevConnect host is running. Start `dotnet run --project src/Tools/DevConnect -- serve` to run this test.");
                return;
            }

            var recorder = new Recorder();
            using var connection = new ClientConnection(recorder, ticket.Limits);
            connection.Connect(ticket.Host, ticket.Port, ticket.Ticket, ticket.SessionKey);
            Pump(connection, () => recorder.Connected || recorder.Disconnect != null);
            Assert.IsNull(recorder.Disconnect, $"refused: {recorder.Disconnect}");

            // DevConnect greets, echoes reliable messages, and answers inputs with a snapshot acking the input.
            Assert.IsTrue(connection.SendReliable(Encoding.UTF8.GetBytes("hello from the EditMode test")));
            var frames = new[] { new InputFrame { Sequence = 77, ClientTick = 1 } };
            var input = new byte[InputPacket.SizeFor(1)];
            InputPacket.Write(input, frames);
            Assert.IsTrue(connection.SendInput(input));
            Pump(connection, () => recorder.Reliable.Count >= 2 && recorder.LastInputAck.HasValue);

            Assert.That(recorder.Reliable, Has.Member("hello from the EditMode test"));
            Assert.AreEqual(77, recorder.LastInputAck);
            connection.Disconnect();
            Pump(connection, () => recorder.Disconnect != null);
            Assert.AreEqual(ClientDisconnectReason.ClosedByClient, recorder.Disconnect);
        }

        private static void Pump(ClientConnection connection, Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done())
            {
                Assert.Less(watch.ElapsedMilliseconds, 5000, "Timed out waiting for the network.");
                connection.PollEvents();
                Thread.Sleep(5);
            }
        }

        private sealed class Recorder : IClientConnectionListener
        {
            public bool Connected;
            public ClientDisconnectReason? Disconnect;
            public readonly List<string> Reliable = new List<string>();
            public int? LastInputAck;

            public void OnConnected() => Connected = true;

            public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion) =>
                Disconnect = reason;

            public void OnReliableReceived(ReadOnlySpan<byte> message) => Reliable.Add(Encoding.UTF8.GetString(message.ToArray()));

            public void OnUnreliableReceived(ReadOnlySpan<byte> message)
            {
                if (SnapshotReader.TryBegin(message, out var snapshot) && snapshot.Header.HasInputAck)
                    LastInputAck = snapshot.Header.LastInputSequence;
            }
        }
    }
}
