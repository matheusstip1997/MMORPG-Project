using Shared.Security;

namespace Shared.Tests.Security;

/// <summary>RFC 5869 appendix A, the SHA-256 cases.</summary>
public class HkdfSha256Tests
{
    private static byte[] Sequence(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();

    public static TheoryData<byte[], byte[], byte[], string, string> Cases => new()
    {
        // Test case 1: basic.
        {
            Enumerable.Repeat((byte)0x0b, 22).ToArray(), Sequence(0x00, 13), Sequence(0xf0, 10),
            "077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5",
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        },
        // Test case 2: longer inputs and outputs (82 bytes, three HMAC blocks).
        {
            Sequence(0x00, 80), Sequence(0x60, 80), Sequence(0xb0, 80),
            "06a6b88c5853361a06104c9ceb35b45cef760014904671014a193f40c15fc244",
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87"
        },
        // Test case 3: empty salt and info (what SessionCrypto uses).
        {
            Enumerable.Repeat((byte)0x0b, 22).ToArray(), [], [],
            "19ef24a32c717b167f33a91d6f648bdf96596776afdb6377ac434c1c293ccb04",
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"
        },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_rfc5869(byte[] ikm, byte[] salt, byte[] info, string expectedPrk, string expectedOkm)
    {
        var prk = new byte[HkdfSha256.HashSize];
        HkdfSha256.Extract(salt, ikm, prk);
        Assert.Equal(Convert.FromHexString(expectedPrk), prk);

        var okm = new byte[expectedOkm.Length / 2];
        HkdfSha256.Expand(prk, info, okm);
        Assert.Equal(Convert.FromHexString(expectedOkm), okm);

        var derived = new byte[okm.Length];
        HkdfSha256.DeriveKey(ikm, salt, info, derived);
        Assert.Equal(okm, derived);
    }

    [Fact]
    public void Refuses_output_longer_than_255_blocks()
    {
        Assert.Throws<ArgumentException>(() => HkdfSha256.Expand(new byte[32], [], new byte[HkdfSha256.MaxOutputSize + 1]));
    }
}
