using System.Security.Cryptography;
using System.Text;
using GameServer.Net.Security;
using Shared.Security;

namespace GameServer.Tests.Net.Security;

/// <summary>
/// The Unity client's managed crypto and the server's platform crypto must agree byte for byte (R-2.5-02):
/// same AEAD output, same HKDF keys, and sessions that open each other's messages in both directions.
/// </summary>
public class CryptoInteropTests
{
    private static readonly int[] Lengths = [0, 1, 15, 16, 17, 63, 64, 65, 127, 128, 129, 255, 1_000, 1_200, 4_097, 16_384];

    [Fact]
    public void Managed_aead_produces_the_same_bytes_as_the_platform_aead()
    {
        var rng = new Random(8439);
        foreach (var length in Lengths)
        {
            foreach (var aadLength in new[] { 0, 8, 13 })
            {
                var key = RandomBytes(rng, 32);
                var nonce = RandomBytes(rng, 12);
                var plaintext = RandomBytes(rng, length);
                var aad = RandomBytes(rng, aadLength);

                var managedCiphertext = new byte[length];
                var managedTag = new byte[16];
                using (var managed = new ManagedChaCha20Poly1305(key))
                    managed.Seal(nonce, plaintext, managedCiphertext, managedTag, aad);

                var platformCiphertext = new byte[length];
                var platformTag = new byte[16];
                using (var platform = new ChaCha20Poly1305(key))
                    platform.Encrypt(nonce, plaintext, platformCiphertext, platformTag, aad);

                Assert.Equal(platformCiphertext, managedCiphertext);
                Assert.Equal(platformTag, managedTag);
            }
        }
    }

    [Fact]
    public void Each_aead_opens_what_the_other_sealed()
    {
        var rng = new Random(1);
        var key = RandomBytes(rng, 32);
        using var managed = new ManagedChaCha20Poly1305(key);
        using var platform = new SystemChaCha20Poly1305(key);
        foreach (var length in Lengths)
        {
            var nonce = RandomBytes(rng, 12);
            var aad = RandomBytes(rng, 8);
            var plaintext = RandomBytes(rng, length);
            var ciphertext = new byte[length];
            var tag = new byte[16];
            var opened = new byte[length];

            platform.Seal(nonce, plaintext, ciphertext, tag, aad);
            Assert.True(managed.TryOpen(nonce, ciphertext, tag, opened, aad));
            Assert.Equal(plaintext, opened);

            managed.Seal(nonce, plaintext, ciphertext, tag, aad);
            Assert.True(platform.TryOpen(nonce, ciphertext, tag, opened, aad));
            Assert.Equal(plaintext, opened);

            tag[0] ^= 1;
            Assert.False(platform.TryOpen(nonce, ciphertext, tag, opened, aad));
            Assert.False(managed.TryOpen(nonce, ciphertext, tag, opened, aad));
        }
    }

    [Fact]
    public void Managed_hkdf_derives_the_same_keys_as_the_platform_hkdf()
    {
        var rng = new Random(5869);
        foreach (var info in new[] { "", "mmorpg/session/v1/c2s-reliable", "mmorpg/session/v1/c2s-input-mac" })
        {
            foreach (var outputLength in new[] { 1, 32, 33, 64, 100 })
            {
                var ikm = RandomBytes(rng, 32);
                var salt = rng.Next(2) == 0 ? [] : RandomBytes(rng, 16);
                var infoBytes = Encoding.ASCII.GetBytes(info);
                var managed = new byte[outputLength];
                var platform = new byte[outputLength];

                HkdfSha256.DeriveKey(ikm, salt, infoBytes, managed);
                HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, platform, salt, infoBytes);

                Assert.Equal(platform, managed);
            }
        }
    }

    [Fact]
    public void Managed_client_and_platform_server_sessions_talk_both_ways()
    {
        var rng = new Random(2502);
        var sessionKey = RandomBytes(rng, 32);
        using var client = new SessionCrypto(sessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
        using var server = new SessionCrypto(sessionKey, SessionRole.Server, SystemChaCha20Poly1305.Create);
        var buffer = new byte[20_000];

        foreach (var length in Lengths)
        {
            var message = RandomBytes(rng, length);

            var toServer = new byte[length + SessionCrypto.ReliableOverhead];
            client.SealReliable(message, toServer);
            Assert.Equal(OpenResult.Ok, server.OpenReliable(toServer, buffer, out var serverLength));
            Assert.Equal(message, buffer[..serverLength]);

            var toClient = new byte[length + SessionCrypto.ReliableOverhead];
            server.SealReliable(message, toClient);
            Assert.Equal(OpenResult.Ok, client.OpenReliable(toClient, buffer, out var clientLength));
            Assert.Equal(message, buffer[..clientLength]);

            if (length > 1_200)
                continue;
            var input = new byte[length + SessionCrypto.InputOverhead];
            client.TagInput(message, input);
            Assert.Equal(OpenResult.Ok, server.VerifyInput(input, out var payload));
            Assert.Equal(message, payload.ToArray());
        }
    }

    [Fact]
    public void Managed_and_platform_sessions_write_identical_packets()
    {
        // Same key, same sequence, same plaintext: the framing is one definition, so the bytes must match exactly.
        var rng = new Random(7);
        var sessionKey = RandomBytes(rng, 32);
        using var managed = new SessionCrypto(sessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
        using var platform = new SessionCrypto(sessionKey, SessionRole.Client, SystemChaCha20Poly1305.Create);

        for (var i = 0; i < 10; i++)
        {
            var message = RandomBytes(rng, 1 + i * 37);
            var a = new byte[message.Length + SessionCrypto.ReliableOverhead];
            var b = new byte[a.Length];
            managed.SealReliable(message, a);
            platform.SealReliable(message, b);
            Assert.Equal(b, a);

            var inputA = new byte[message.Length + SessionCrypto.InputOverhead];
            var inputB = new byte[inputA.Length];
            managed.TagInput(message, inputA);
            platform.TagInput(message, inputB);
            Assert.Equal(inputB, inputA);
        }
    }

    private static byte[] RandomBytes(Random rng, int length)
    {
        var bytes = new byte[length];
        rng.NextBytes(bytes);
        return bytes;
    }
}
