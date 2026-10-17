using GameServer.Net.Security;
using Shared.Security;

namespace GameServer.Tests.Net.Security;

/// <summary>R-2.5-02: reliable messages are encrypted per direction with a replay window; client inputs carry a tag.</summary>
public sealed class SessionCryptoTests : IDisposable
{
    private static readonly byte[] SessionKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Message = "move to the gate"u8.ToArray();

    private readonly SessionCrypto _client = new(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);
    private readonly SessionCrypto _server = new(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);

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

    private static (OpenResult Result, byte[] Plaintext) Open(SessionCrypto receiver, byte[] packet)
    {
        var buffer = new byte[packet.Length];
        var result = receiver.OpenReliable(packet, buffer, out var length);
        return (result, buffer[..length]);
    }

    private static byte[] Tag(SessionCrypto client, byte[] payload)
    {
        var packet = new byte[payload.Length + SessionCrypto.InputOverhead];
        Assert.Equal(packet.Length, client.TagInput(payload, packet));
        return packet;
    }

    private static (OpenResult Result, byte[] Payload) Verify(SessionCrypto server, byte[] packet)
    {
        var result = server.VerifyInput(packet, out var payload);
        return (result, payload.ToArray());
    }

    private static void AssertOk(byte[] expected, (OpenResult Result, byte[] Data) actual)
    {
        Assert.Equal(OpenResult.Ok, actual.Result);
        Assert.Equal(expected, actual.Data);
    }

    [Fact]
    public void Reliable_message_round_trips_in_both_directions()
    {
        AssertOk(Message, Open(_server, Seal(_client, Message)));
        AssertOk(Message, Open(_client, Seal(_server, Message)));
    }

    [Fact]
    public void Empty_reliable_message_round_trips()
    {
        var (result, plaintext) = Open(_server, Seal(_client, []));

        Assert.Equal(OpenResult.Ok, result);
        Assert.Empty(plaintext);
    }

    [Fact]
    public void Ciphertext_does_not_contain_the_plaintext()
    {
        var packet = Seal(_client, Message);

        Assert.False(packet.AsSpan(SessionCrypto.ReliableHeaderSize, Message.Length).SequenceEqual(Message));
    }

    [Fact]
    public void Every_byte_of_a_reliable_packet_is_authenticated()
    {
        var packet = Seal(_client, Message);
        for (var i = 0; i < packet.Length; i++)
        {
            using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
            var tampered = packet.ToArray();
            tampered[i] ^= 0x01;

            var (result, _) = Open(server, tampered);

            // Flipping the sequence can also land on 0, which the replay window refuses first.
            Assert.True(result is OpenResult.AuthenticationFailed or OpenResult.Replayed, $"byte {i}: {result}");
        }
    }

    [Fact]
    public void Truncated_reliable_packet_is_malformed()
    {
        var packet = Seal(_client, Message);

        Assert.Equal(OpenResult.Malformed, Open(_server, packet[..(SessionCrypto.ReliableOverhead - 1)]).Result);
    }

    [Fact]
    public void Reliable_packet_is_refused_with_the_wrong_session_key()
    {
        using var stranger = new SessionCrypto(OtherKey, SessionRole.Server, SystemChaCha20Poly1305.Create);

        Assert.Equal(OpenResult.AuthenticationFailed, Open(stranger, Seal(_client, Message)).Result);
    }

    [Fact]
    public void Reflected_packet_does_not_open_because_keys_differ_per_direction()
    {
        // A packet the server sent, bounced back at the server.
        Assert.Equal(OpenResult.AuthenticationFailed, Open(_server, Seal(_server, Message)).Result);
    }

    [Fact]
    public void Replayed_reliable_packet_is_refused()
    {
        var packet = Seal(_client, Message);

        Assert.Equal(OpenResult.Ok, Open(_server, packet).Result);
        Assert.Equal(OpenResult.Replayed, Open(_server, packet).Result);
    }

    [Fact]
    public void Failed_packet_does_not_burn_its_sequence_number()
    {
        var packet = Seal(_client, Message);
        var forged = packet.ToArray();
        forged[^1] ^= 0x01;

        Assert.Equal(OpenResult.AuthenticationFailed, Open(_server, forged).Result);
        Assert.Equal(OpenResult.Ok, Open(_server, packet).Result);
    }

    [Fact]
    public void Reordered_reliable_packets_within_the_window_are_accepted_once()
    {
        var first = Seal(_client, Message);
        var second = Seal(_client, Message);

        Assert.Equal(OpenResult.Ok, Open(_server, second).Result);
        Assert.Equal(OpenResult.Ok, Open(_server, first).Result);
        Assert.Equal(OpenResult.Replayed, Open(_server, first).Result);
    }

    [Fact]
    public void Same_plaintext_never_produces_the_same_packet_because_the_nonce_never_repeats()
    {
        var packets = Enumerable.Range(0, 1_000).Select(_ => Convert.ToHexString(Seal(_client, Message))).ToList();

        Assert.Equal(packets.Count, packets.Distinct().Count());
    }

    [Fact]
    public void Sealing_refuses_to_wrap_the_sequence_and_reuse_a_nonce()
    {
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create, reliableSendSequence: ulong.MaxValue - 1, inputSendSequence: 0);

        Seal(client, Message);

        Assert.Throws<InvalidOperationException>(() => Seal(client, Message));
    }

    [Fact]
    public void Sealing_after_dispose_throws()
    {
        _client.Dispose();

        Assert.Throws<ObjectDisposedException>(() => Seal(_client, Message));
    }

    [Fact]
    public void Tagged_input_verifies_and_yields_its_payload()
    {
        AssertOk(Message, Verify(_server, Tag(_client, Message)));
    }

    [Fact]
    public void Input_payload_travels_in_clear()
    {
        var packet = Tag(_client, Message);

        Assert.True(packet.AsSpan(SessionCrypto.InputHeaderSize, Message.Length).SequenceEqual(Message));
    }

    [Fact]
    public void Every_byte_of_an_input_packet_is_authenticated()
    {
        var packet = Tag(_client, Message);
        for (var i = 0; i < packet.Length; i++)
        {
            using var server = new SessionCrypto(SessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
            var tampered = packet.ToArray();
            tampered[i] ^= 0x01;

            var (result, _) = Verify(server, tampered);

            Assert.True(result is OpenResult.AuthenticationFailed or OpenResult.Replayed, $"byte {i}: {result}");
        }
    }

    [Fact]
    public void Input_with_the_wrong_session_key_is_refused()
    {
        using var stranger = new SessionCrypto(OtherKey, SessionRole.Client, SystemChaCha20Poly1305.Create);

        Assert.Equal(OpenResult.AuthenticationFailed, Verify(_server, Tag(stranger, Message)).Result);
    }

    [Fact]
    public void Untagged_input_is_refused()
    {
        Assert.Equal(OpenResult.Malformed, Verify(_server, new byte[SessionCrypto.InputOverhead - 1]).Result);
        Assert.Equal(OpenResult.Replayed, Verify(_server, new byte[64]).Result); // sequence 0
    }

    [Fact]
    public void Replayed_input_is_refused()
    {
        var packet = Tag(_client, Message);

        Assert.Equal(OpenResult.Ok, Verify(_server, packet).Result);
        Assert.Equal(OpenResult.Replayed, Verify(_server, packet).Result);
    }

    [Fact]
    public void Input_older_than_the_window_is_refused()
    {
        var old = Tag(_client, Message);
        for (var i = 0; i < ReplayWindow.Size; i++)
            Assert.Equal(OpenResult.Ok, Verify(_server, Tag(_client, Message)).Result);

        Assert.Equal(OpenResult.Replayed, Verify(_server, old).Result);
    }

    [Fact]
    public void Input_tagging_refuses_to_wrap_the_sequence()
    {
        using var client = new SessionCrypto(SessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create, reliableSendSequence: 0, inputSendSequence: uint.MaxValue - 1);

        Tag(client, Message);

        Assert.Throws<InvalidOperationException>(() => Tag(client, Message));
    }

    [Fact]
    public void Only_the_client_tags_and_only_the_server_verifies()
    {
        Assert.Throws<InvalidOperationException>(() => Tag(_server, Message));
        Assert.Throws<InvalidOperationException>(() => Verify(_client, new byte[32]));
    }

    [Fact]
    public void Session_key_must_be_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new SessionCrypto(new byte[16], SessionRole.Server, SystemChaCha20Poly1305.Create));
    }
}
