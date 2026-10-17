using System;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Shared.Security;

namespace Game.Tests.EditMode
{
    /// <summary>
    /// The managed crypto and session framing (R-2.5-02) under Unity's runtime: RFC vectors, and golden packets
    /// written by the server's platform ChaCha20-Poly1305, so the client is proven to speak the server's bytes.
    /// The full vector and fuzz suites run in .NET (tests/Shared.Tests/Security, tests/GameServer.Tests).
    /// </summary>
    public class SessionCryptoTests
    {
        private static readonly byte[] SessionKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);
            return bytes;
        }

        private static string ToHex(byte[] bytes) => string.Concat(bytes.Select(b => b.ToString("x2")));

        private static byte[] Sequence(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

        [Test]
        public void ChaCha20_block_matches_rfc8439_2_3_2()
        {
            var output = new byte[ChaCha20.BlockSize];

            ChaCha20.Block(Sequence(0, 32), 1, Hex("000000090000004a00000000"), output);

            Assert.AreEqual(
                "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e" +
                "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e", ToHex(output));
        }

        [Test]
        public void Poly1305_matches_rfc8439_2_5_2()
        {
            var tag = new byte[Poly1305.TagSize];

            Poly1305.ComputeTag(Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"),
                Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"), tag);

            Assert.AreEqual("a8061dc1305136c6c22b8baf0c0127a9", ToHex(tag));
        }

        [Test]
        public void Aead_matches_rfc8439_2_8_2()
        {
            var plaintext = Encoding.ASCII.GetBytes(
                "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");
            var nonce = Hex("070000004041424344454647");
            var aad = Hex("50515253c0c1c2c3c4c5c6c7");
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[16];
            using var aead = new ManagedChaCha20Poly1305(Sequence(0x80, 32));

            aead.Seal(nonce, plaintext, ciphertext, tag, aad);

            Assert.AreEqual(
                "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
                "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
                "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
                "3ff4def08e4b7a9de576d26586cec64b6116", ToHex(ciphertext));
            Assert.AreEqual("1ae10b594f09e26a7e902ecbd0600691", ToHex(tag));

            var opened = new byte[ciphertext.Length];
            Assert.IsTrue(aead.TryOpen(nonce, ciphertext, tag, opened, aad));
            Assert.AreEqual(plaintext, opened);
            tag[3] ^= 0x10;
            Assert.IsFalse(aead.TryOpen(nonce, ciphertext, tag, opened, aad));
        }

        [Test]
        public void Hkdf_matches_rfc5869_case_1_and_3()
        {
            var ikm = Enumerable.Repeat((byte)0x0b, 22).ToArray();
            var okm = new byte[42];

            HkdfSha256.DeriveKey(ikm, Sequence(0, 13), Sequence(0xf0, 10), okm);
            Assert.AreEqual("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", ToHex(okm));

            HkdfSha256.DeriveKey(ikm, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, okm);
            Assert.AreEqual("8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8", ToHex(okm));
        }

        [Test]
        public void Client_writes_the_packets_the_server_platform_crypto_writes()
        {
            var message = Encoding.ASCII.GetBytes("hello server");
            using var client = new SessionCrypto(SessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
            var reliable = new byte[message.Length + SessionCrypto.ReliableOverhead];
            var input = new byte[3 + SessionCrypto.InputOverhead];

            client.SealReliable(message, reliable);
            client.TagInput(new byte[] { 1, 2, 3 }, input);

            Assert.AreEqual("010000000000000065cff31d6f96000facf5faf874972bf37e18d8c847f3721764ccbf75", ToHex(reliable));
            Assert.AreEqual("010000000102032c5724474d2d90df", ToHex(input));
        }

        [Test]
        public void Client_opens_a_packet_sealed_by_the_server_platform_crypto()
        {
            using var client = new SessionCrypto(SessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
            var packet = Hex("010000000000000041c00f7a3814926c57468fdf552596f389707e185a28e677d9fb4a08");
            var plaintext = new byte[64];

            Assert.AreEqual(OpenResult.Ok, client.OpenReliable(packet, plaintext, out var length));
            Assert.AreEqual("hello server", Encoding.ASCII.GetString(plaintext, 0, length));
            Assert.AreEqual(OpenResult.Replayed, client.OpenReliable(packet, plaintext, out _));
        }

        [Test]
        public void Tampered_short_and_misdirected_packets_are_refused()
        {
            using var client = new SessionCrypto(SessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
            using var server = new SessionCrypto(SessionKey, SessionRole.Server, ManagedChaCha20Poly1305.Create);
            var packet = new byte[5 + SessionCrypto.ReliableOverhead];
            client.SealReliable(new byte[] { 1, 2, 3, 4, 5 }, packet);
            var plaintext = new byte[64];

            Assert.AreEqual(OpenResult.AuthenticationFailed, client.OpenReliable(packet, plaintext, out _), "wrong direction");
            Assert.AreEqual(OpenResult.Malformed, server.OpenReliable(new byte[SessionCrypto.ReliableOverhead - 1], plaintext, out _));
            packet[10] ^= 1;
            Assert.AreEqual(OpenResult.AuthenticationFailed, server.OpenReliable(packet, plaintext, out _));
            packet[10] ^= 1;
            Assert.AreEqual(OpenResult.Ok, server.OpenReliable(packet, plaintext, out var length));
            Assert.AreEqual(5, length);
        }
    }
}
