using Shared.Security;

namespace Shared.Tests.Security;

/// <summary>
/// The session framing (R-2.5-02) with the managed AEAD on both ends, as a .NET client or bot would use it.
/// Interop with the server's platform AEAD is in GameServer.Tests (CryptoInteropTests).
/// </summary>
public sealed class ManagedSessionCryptoTests : IDisposable
{
    private static readonly byte[] SessionKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Message = "inventory: move slot 3 to slot 7"u8.ToArray();

    private readonly SessionCrypto _client = new(SessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
    private readonly SessionCrypto _server = new(SessionKey, SessionRole.Server, ManagedChaCha20Poly1305.Create);

    public void Dispose()
    {
        _client.Dispose();
        _server.Dispose();
    }

    private static byte[] Seal(SessionCrypto sender, byte[] plaintext)
    {
        var packet = new byte[plaintext.Length + SessionCrypto.ReliableOverhead];
        Assert.Equal(packet.Length, sender.SealReliable(plaintext, packet));
        return packet;
    }

    private static OpenResult Open(SessionCrypto receiver, byte[] packet, out byte[] plaintext)
    {
        var buffer = new byte[Math.Max(0, packet.Length - SessionCrypto.ReliableOverhead)];
        var result = receiver.OpenReliable(packet, buffer, out var length);
        plaintext = buffer[..length];
        return result;
    }

    private static byte[] Tag(SessionCrypto client, byte[] payload)
    {
        var packet = new byte[payload.Length + SessionCrypto.InputOverhead];
        client.TagInput(payload, packet);
        return packet;
    }

    [Fact]
    public void Writes_the_golden_packets_the_server_platform_crypto_writes()
    {
        // Generated with the server's SystemChaCha20Poly1305; the Unity EditMode tests check the same bytes.
        var message = "hello server"u8.ToArray();

        Assert.Equal("010000000000000065cff31d6f96000facf5faf874972bf37e18d8c847f3721764ccbf75", Convert.ToHexStringLower(Seal(_client, message)));
        Assert.Equal("010000000000000041c00f7a3814926c57468fdf552596f389707e185a28e677d9fb4a08", Convert.ToHexStringLower(Seal(_server, message)));
        Assert.Equal("010000000102032c5724474d2d90df", Convert.ToHexStringLower(Tag(_client, [1, 2, 3])));
    }

    [Fact]
    public void Reliable_messages_round_trip_both_ways()
    {
        Assert.Equal(OpenResult.Ok, Open(_server, Seal(_client, Message), out var atServer));
        Assert.Equal(Message, atServer);
        Assert.Equal(OpenResult.Ok, Open(_client, Seal(_server, Message), out var atClient));
        Assert.Equal(Message, atClient);
    }

    [Fact]
    public void Empty_reliable_message_round_trips()
    {
        Assert.Equal(OpenResult.Ok, Open(_server, Seal(_client, []), out var plaintext));
        Assert.Empty(plaintext);
    }

    [Fact]
    public void A_message_does_not_open_in_the_direction_it_was_sent_from()
    {
        Assert.Equal(OpenResult.AuthenticationFailed, Open(_client, Seal(_client, Message), out _));
    }

    [Fact]
    public void Every_flipped_byte_is_refused()
    {
        var packet = Seal(_client, Message);
        for (var i = 0; i < packet.Length; i++)
        {
            using var server = new SessionCrypto(SessionKey, SessionRole.Server, ManagedChaCha20Poly1305.Create);
            packet[i] ^= 0x40;
            var result = Open(server, packet, out _);
            Assert.True(result is OpenResult.AuthenticationFailed or OpenResult.Replayed, $"byte {i}: {result}");
            packet[i] ^= 0x40;
        }
    }

    [Fact]
    public void Replayed_and_out_of_window_messages_are_refused()
    {
        var first = Seal(_client, Message);
        var later = new byte[ReplayWindow.Size + 1][];
        for (var i = 0; i < later.Length; i++)
            later[i] = Seal(_client, Message);

        Assert.Equal(OpenResult.Ok, Open(_server, later[^1], out _));
        Assert.Equal(OpenResult.Replayed, Open(_server, later[^1], out _));
        Assert.Equal(OpenResult.Replayed, Open(_server, first, out _)); // 65 behind: outside the window
        Assert.Equal(OpenResult.Ok, Open(_server, later[1], out _)); // 63 behind: inside
    }

    [Fact]
    public void Short_packets_and_small_buffers_are_malformed()
    {
        var packet = Seal(_client, Message);

        Assert.Equal(OpenResult.Malformed, _server.OpenReliable(packet.AsSpan(0, SessionCrypto.ReliableOverhead - 1), new byte[64], out _));
        Assert.Equal(OpenResult.Malformed, _server.OpenReliable(packet, new byte[Message.Length - 1], out _));
        Assert.Equal(OpenResult.Malformed, _server.VerifyInput(new byte[SessionCrypto.InputOverhead - 1], out _));
    }

    [Fact]
    public void Tagged_inputs_verify_once_and_return_the_payload()
    {
        var packet = Tag(_client, [1, 2, 3]);

        Assert.Equal(OpenResult.Ok, _server.VerifyInput(packet, out var payload));
        Assert.Equal(new byte[] { 1, 2, 3 }, payload.ToArray());
        Assert.Equal(OpenResult.Replayed, _server.VerifyInput(packet, out _));
    }

    [Fact]
    public void Tampered_inputs_are_refused()
    {
        var packet = Tag(_client, [1, 2, 3]);
        for (var i = SessionCrypto.InputHeaderSize; i < packet.Length; i++)
        {
            packet[i] ^= 0x01;
            Assert.Equal(OpenResult.AuthenticationFailed, _server.VerifyInput(packet, out _));
            packet[i] ^= 0x01;
        }
        Assert.Equal(OpenResult.Ok, _server.VerifyInput(packet, out _));
    }

    [Fact]
    public void Only_the_client_tags_and_only_the_server_verifies()
    {
        Assert.Throws<InvalidOperationException>(() => _server.TagInput([1], new byte[64]));
        Assert.Throws<InvalidOperationException>(() => _client.VerifyInput(new byte[64], out _));
    }

    [Fact]
    public void Sealing_stops_before_the_sequence_wraps()
    {
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create, ulong.MaxValue - 1, uint.MaxValue - 1);

        Seal(client, Message);
        Tag(client, [1]);
        Assert.Throws<InvalidOperationException>(() => Seal(client, Message));
        Assert.Throws<InvalidOperationException>(() => Tag(client, [1]));
    }

    [Fact]
    public void Random_bytes_never_open_or_verify()
    {
        var rng = new Random(2504);
        var buffer = new byte[512];
        for (var i = 0; i < 2_000; i++)
        {
            var junk = new byte[rng.Next(0, 300)];
            rng.NextBytes(junk);
            Assert.NotEqual(OpenResult.Ok, _server.OpenReliable(junk, buffer, out var length));
            Assert.Equal(0, length);
            Assert.NotEqual(OpenResult.Ok, _server.VerifyInput(junk, out _));
        }
    }

    [Fact]
    public void Disposed_session_refuses_to_seal()
    {
        _client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Seal(_client, Message));
    }
}
