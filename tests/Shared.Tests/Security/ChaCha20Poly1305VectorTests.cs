using System.Numerics;
using System.Text;
using Shared.Security;

namespace Shared.Tests.Security;

/// <summary>RFC 8439 test vectors for the managed ChaCha20, Poly1305, and AEAD (R-2.5-02).</summary>
public class ChaCha20Poly1305VectorTests
{
    private static readonly byte[] Sunscreen = Encoding.ASCII.GetBytes(
        "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.");

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace(":", ""));

    private static byte[] Sequence(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Block_function_matches_rfc8439_2_3_2()
    {
        var output = new byte[ChaCha20.BlockSize];

        ChaCha20.Block(Sequence(0, 32), 1, Hex("000000090000004a00000000"), output);

        Assert.Equal(Hex(
            "10f1e7e4d13b5915500fdd1fa32071c4c7d1f4c733c068030422aa9ac3d46c4e" +
            "d2826446079faa0914c2d705d98b02a2b5129cd1de164eb9cbd083e8a2503c4e"), output);
    }

    [Fact]
    public void Encryption_matches_rfc8439_2_4_2()
    {
        var output = new byte[Sunscreen.Length];

        ChaCha20.Xor(Sequence(0, 32), 1, Hex("000000000000004a00000000"), Sunscreen, output);

        Assert.Equal(Hex(
            "6e2e359a2568f98041ba0728dd0d6981e97e7aec1d4360c20a27afccfd9fae0b" +
            "f91b65c5524733ab8f593dabcd62b3571639d624e65152ab8f530c359f0861d8" +
            "07ca0dbf500d6a6156a38e088a22b65e52bc514d16ccf806818ce91ab7793736" +
            "5af90bbf74a35be6b40b8eedf2785e42874d"), output);
    }

    [Fact]
    public void Encryption_works_in_place()
    {
        var buffer = (byte[])Sunscreen.Clone();

        ChaCha20.Xor(Sequence(0, 32), 1, Hex("000000000000004a00000000"), buffer, buffer);
        ChaCha20.Xor(Sequence(0, 32), 1, Hex("000000000000004a00000000"), buffer, buffer);

        Assert.Equal(Sunscreen, buffer);
    }

    [Fact]
    public void Poly1305_matches_rfc8439_2_5_2()
    {
        var tag = new byte[Poly1305.TagSize];

        Poly1305.ComputeTag(
            Hex("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"),
            Encoding.ASCII.GetBytes("Cryptographic Forum Research Group"),
            tag);

        Assert.Equal(Hex("a8061dc1305136c6c22b8baf0c0127a9"), tag);
    }

    [Fact]
    public void Poly1305_key_generation_matches_rfc8439_2_6_2()
    {
        var block = new byte[ChaCha20.BlockSize];

        ChaCha20.Block(Sequence(0x80, 32), 0, Hex("000000000001020304050607"), block);

        Assert.Equal(Hex("8ad5a08b905f81cc815040274ab29471a833b637e3fd0da508dbb8e2fdd1a646"), block[..32]);
    }

    [Fact]
    public void Aead_matches_rfc8439_2_8_2()
    {
        using var aead = new ManagedChaCha20Poly1305(Sequence(0x80, 32));
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var ciphertext = new byte[Sunscreen.Length];
        var tag = new byte[16];

        aead.Seal(nonce, Sunscreen, ciphertext, tag, aad);

        Assert.Equal(Hex(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6" +
            "3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36" +
            "92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b6116"), ciphertext);
        Assert.Equal(Hex("1ae10b594f09e26a7e902ecbd0600691"), tag);

        var plaintext = new byte[ciphertext.Length];
        Assert.True(aead.TryOpen(nonce, ciphertext, tag, plaintext, aad));
        Assert.Equal(Sunscreen, plaintext);
    }

    [Fact]
    public void Aead_refuses_any_flipped_bit_and_leaves_the_plaintext_untouched()
    {
        using var aead = new ManagedChaCha20Poly1305(Sequence(0x80, 32));
        var nonce = Hex("070000004041424344454647");
        var aad = Hex("50515253c0c1c2c3c4c5c6c7");
        var ciphertext = new byte[Sunscreen.Length];
        var tag = new byte[16];
        aead.Seal(nonce, Sunscreen, ciphertext, tag, aad);

        foreach (var (target, name) in new[] { (ciphertext, "ciphertext"), (tag, "tag"), (aad, "aad"), (nonce, "nonce") })
        {
            for (var i = 0; i < target.Length; i++)
            {
                target[i] ^= 0x01;
                var plaintext = new byte[ciphertext.Length];
                Assert.False(aead.TryOpen(nonce, ciphertext, tag, plaintext, aad), $"{name} byte {i}");
                Assert.All(plaintext, b => Assert.Equal(0, b));
                target[i] ^= 0x01;
            }
        }
    }

    // RFC 8439 appendix A.3, the vectors that exercise the final reduction and carries.
    [Theory]
    [InlineData("02000000000000000000000000000000" + "00000000000000000000000000000000", "ffffffffffffffffffffffffffffffff", "03000000000000000000000000000000")]
    [InlineData("02000000000000000000000000000000" + "ffffffffffffffffffffffffffffffff", "02000000000000000000000000000000", "03000000000000000000000000000000")]
    [InlineData("01000000000000000000000000000000" + "00000000000000000000000000000000",
        "ffffffffffffffffffffffffffffffff" + "f0ffffffffffffffffffffffffffffff" + "11000000000000000000000000000000", "05000000000000000000000000000000")]
    [InlineData("01000000000000000000000000000000" + "00000000000000000000000000000000",
        "ffffffffffffffffffffffffffffffff" + "fbfefefefefefefefefefefefefefefe" + "01010101010101010101010101010101", "00000000000000000000000000000000")]
    [InlineData("02000000000000000000000000000000" + "00000000000000000000000000000000", "fdffffffffffffffffffffffffffffff", "faffffffffffffffffffffffffffffff")]
    [InlineData("01000000000000000400000000000000" + "00000000000000000000000000000000",
        "e33594d7505e43b900000000000000003394d7505e4379cd01000000000000000000000000000000000000000000000001000000000000000000000000000000",
        "14000000000000005500000000000000")]
    [InlineData("01000000000000000400000000000000" + "00000000000000000000000000000000",
        "e33594d7505e43b900000000000000003394d7505e4379cd010000000000000000000000000000000000000000000000",
        "13000000000000000000000000000000")]
    public void Poly1305_matches_rfc8439_appendix_a3(string key, string message, string expectedTag)
    {
        var tag = new byte[Poly1305.TagSize];

        Poly1305.ComputeTag(Hex(key), Hex(message), tag);

        Assert.Equal(Hex(expectedTag), tag);
    }

    [Fact]
    public void Poly1305_matches_a_big_integer_reference_on_random_and_extreme_inputs()
    {
        var rng = new Random(8439);
        var tag = new byte[Poly1305.TagSize];
        for (var run = 0; run < 2_000; run++)
        {
            var key = new byte[32];
            var message = new byte[rng.Next(0, 200)];
            // A third of the runs use all-0xFF bytes, the worst case for the carries.
            if (run % 3 == 0)
            {
                Array.Fill(key, (byte)0xFF);
                Array.Fill(message, (byte)0xFF);
            }
            else
            {
                rng.NextBytes(key);
                rng.NextBytes(message);
            }

            Poly1305.ComputeTag(key, message, tag);

            Assert.Equal(ReferencePoly1305(key, message), tag);
        }
    }

    /// <summary>RFC 8439 section 2.5.1 written with <see cref="BigInteger"/>, as directly as the text reads.</summary>
    private static byte[] ReferencePoly1305(byte[] key, byte[] message)
    {
        var p = (BigInteger.One << 130) - 5;
        var rBytes = key[..16];
        rBytes[3] &= 15; rBytes[7] &= 15; rBytes[11] &= 15; rBytes[15] &= 15;
        rBytes[4] &= 252; rBytes[8] &= 252; rBytes[12] &= 252;
        var r = new BigInteger(rBytes, isUnsigned: true);
        var s = new BigInteger(key[16..], isUnsigned: true);

        var accumulator = BigInteger.Zero;
        for (var offset = 0; offset < message.Length; offset += 16)
        {
            var block = message.AsSpan(offset, Math.Min(16, message.Length - offset));
            var n = new BigInteger(block, isUnsigned: true) + (BigInteger.One << (8 * block.Length));
            accumulator = (accumulator + n) * r % p;
        }
        accumulator = (accumulator + s) & ((BigInteger.One << 128) - 1);

        var tag = new byte[16];
        accumulator.TryWriteBytes(tag, out _, isUnsigned: true);
        return tag;
    }
}
