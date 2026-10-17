using System.Buffers.Binary;
using GameServer.Net;
using GameServer.Net.Security;
using Shared.Security;
using GameServer.Net.Tickets;
using GameServer.Tests.Support;
using Shared.Protocol;

namespace GameServer.Tests.Net.Security;

/// <summary>
/// R-2.5-04: the decoders that see untrusted bytes (connect request with its ticket, reliable messages, client inputs)
/// never throw and never accept random or mutated input. Seeds are fixed so failures reproduce.
/// </summary>
public class DecoderFuzzTests
{
    private const int Iterations = 5_000;
    private const int Realm = 1;
    private const int LifetimeMs = 60_000;
    private static readonly byte[] TicketSecret = Enumerable.Repeat((byte)0x42, 32).ToArray();
    private static readonly byte[] SessionKey = Enumerable.Range(7, 32).Select(i => (byte)i).ToArray();

    private readonly FakeClock _clock = new();

    private static byte[] RandomBytes(Random rng, int maxLength)
    {
        var bytes = new byte[rng.Next(0, maxLength + 1)];
        rng.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Bit flips, byte overwrites, truncation, or extension of a valid input; never returns it unchanged.</summary>
    private static byte[] Mutate(Random rng, byte[] valid)
    {
        while (true)
        {
            var bytes = valid.ToArray();
            switch (rng.Next(4))
            {
                case 0:
                    for (int i = 0, flips = rng.Next(1, 4); i < flips; i++)
                        bytes[rng.Next(bytes.Length)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1:
                    bytes[rng.Next(bytes.Length)] = (byte)rng.Next(256);
                    break;
                case 2:
                    bytes = bytes[..rng.Next(bytes.Length)];
                    break;
                default:
                    var extra = new byte[rng.Next(1, 16)];
                    rng.NextBytes(extra);
                    bytes = [.. bytes, .. extra];
                    break;
            }
            if (!bytes.AsSpan().SequenceEqual(valid))
                return bytes;
        }
    }

    private ConnectionApprover Approver() =>
        new(new TicketValidator(new TicketKeyRing().Add(1, TicketSecret), new InMemoryNonceCache(_clock), _clock, Realm, LifetimeMs));

    private byte[] ValidRequest()
    {
        var ticket = new DevTicketIssuer(1, TicketSecret, _clock, LifetimeMs).Issue(accountId: 5, Realm, slot: 0).Ticket;
        var request = new byte[ConnectRequest.SizeFor(ticket.Length)];
        ConnectRequest.Write(request, ProtocolVersion.Current, ticket);
        return request;
    }

    [Fact]
    public void Connect_request_decoder_refuses_random_bytes()
    {
        var rng = new Random(250_401);
        var approver = Approver();

        for (var i = 0; i < Iterations; i++)
        {
            var bytes = RandomBytes(rng, ConnectRequest.HeaderSize + SessionTicketCodec.Size + 8);
            // Keep the version right half the time so the ticket decoder is reached.
            if (bytes.Length >= 2 && rng.Next(2) == 0)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes, ProtocolVersion.Current);

            Assert.False(approver.Approve(bytes).Ok);
        }
    }

    [Fact]
    public void Connect_request_decoder_refuses_every_mutation_of_a_valid_request()
    {
        var rng = new Random(250_402);
        var approver = Approver();

        for (var i = 0; i < Iterations; i++)
        {
            var mutated = Mutate(rng, ValidRequest());
            var result = approver.Approve(mutated);
            Assert.False(result.Ok, $"iteration {i}: {Convert.ToHexString(mutated)}");
        }

        Assert.True(approver.Approve(ValidRequest()).Ok);
    }

    [Fact]
    public void Ticket_decoder_reads_only_exact_size_version_2_tickets()
    {
        var rng = new Random(250_403);

        for (var i = 0; i < Iterations; i++)
        {
            var bytes = RandomBytes(rng, SessionTicketCodec.Size + 8);
            var expected = bytes.Length == SessionTicketCodec.Size && bytes[0] == SessionTicket.CurrentVersion;

            Assert.Equal(expected, SessionTicketCodec.TryReadPayload(bytes, out _));
            Assert.False(SessionTicketCodec.HasValidSignature(bytes, TicketKey.FromSecret(TicketSecret)));
        }
    }

    [Fact]
    public void Reliable_decoder_refuses_random_bytes()
    {
        var rng = new Random(250_404);
        using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
        var buffer = new byte[256];

        for (var i = 0; i < Iterations; i++)
        {
            var bytes = RandomBytes(rng, 200);
            Assert.NotEqual(OpenResult.Ok, server.OpenReliable(bytes, buffer, out var length));
            Assert.Equal(0, length);
        }
    }

    [Fact]
    public void Reliable_decoder_refuses_every_mutation_of_a_valid_packet()
    {
        var rng = new Random(250_405);
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
        using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
        var plaintext = "trade gold 100"u8.ToArray();
        var buffer = new byte[256];

        for (var i = 0; i < Iterations; i++)
        {
            var packet = new byte[plaintext.Length + SessionCrypto.ReliableOverhead];
            client.SealReliable(plaintext, packet);

            Assert.NotEqual(OpenResult.Ok, server.OpenReliable(Mutate(rng, packet), buffer, out _));
            Assert.Equal(OpenResult.Ok, server.OpenReliable(packet, buffer, out _));
        }
    }

    [Fact]
    public void Reliable_decoder_reports_malformed_when_the_destination_is_too_small()
    {
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
        using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
        var packet = new byte[10 + SessionCrypto.ReliableOverhead];
        client.SealReliable(new byte[10], packet);

        Assert.Equal(OpenResult.Malformed, server.OpenReliable(packet, new byte[9], out _));
    }

    [Fact]
    public void Input_decoder_refuses_random_bytes()
    {
        var rng = new Random(250_406);
        using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);

        for (var i = 0; i < Iterations; i++)
        {
            Assert.NotEqual(OpenResult.Ok, server.VerifyInput(RandomBytes(rng, 200), out var payload));
            Assert.True(payload.IsEmpty);
        }
    }

    [Fact]
    public void Input_decoder_refuses_every_mutation_of_a_valid_packet()
    {
        var rng = new Random(250_407);
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
        using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
        var input = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        for (var i = 0; i < Iterations; i++)
        {
            var packet = new byte[input.Length + SessionCrypto.InputOverhead];
            client.TagInput(input, packet);

            Assert.NotEqual(OpenResult.Ok, server.VerifyInput(Mutate(rng, packet), out _));
            Assert.Equal(OpenResult.Ok, server.VerifyInput(packet, out _));
        }
    }
}
