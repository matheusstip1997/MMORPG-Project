using System;
using System.Security.Cryptography;

namespace Shared.Security
{
    /// <summary>
    /// HKDF with HMAC-SHA256, RFC 5869. Built on <see cref="HMACSHA256"/>, which netstandard2.1 and Unity have (the
    /// platform <c>HKDF</c> class is .NET 5+ only). Used once per connection, so it may allocate.
    /// </summary>
    public static class HkdfSha256
    {
        public const int HashSize = 32;
        public const int MaxOutputSize = 255 * HashSize;

        /// <summary>Extract then expand: fills <paramref name="output"/> with key material. An empty salt means HashSize zero bytes.</summary>
        public static void DeriveKey(ReadOnlySpan<byte> inputKeyMaterial, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> info, Span<byte> output)
        {
            Span<byte> prk = stackalloc byte[HashSize];
            Extract(salt, inputKeyMaterial, prk);
            Expand(prk, info, output);
            CryptographicOperations.ZeroMemory(prk);
        }

        /// <summary>PRK = HMAC-SHA256(salt, IKM).</summary>
        public static void Extract(ReadOnlySpan<byte> salt, ReadOnlySpan<byte> inputKeyMaterial, Span<byte> prk)
        {
            if (prk.Length != HashSize)
                throw new ArgumentException($"PRK must be {HashSize} bytes.", nameof(prk));
            var saltBytes = salt.IsEmpty ? new byte[HashSize] : salt.ToArray();
            using (var hmac = new HMACSHA256(saltBytes))
                Hash(hmac, inputKeyMaterial, prk);
            CryptographicOperations.ZeroMemory(saltBytes);
        }

        /// <summary>OKM = T(1) | T(2) | ..., T(i) = HMAC-SHA256(PRK, T(i-1) | info | i).</summary>
        public static void Expand(ReadOnlySpan<byte> prk, ReadOnlySpan<byte> info, Span<byte> output)
        {
            if (prk.Length < HashSize)
                throw new ArgumentException($"PRK must be at least {HashSize} bytes.", nameof(prk));
            if (output.Length > MaxOutputSize)
                throw new ArgumentException($"Output must be at most {MaxOutputSize} bytes.", nameof(output));

            var prkBytes = prk.ToArray();
            var input = new byte[HashSize + info.Length + 1];
            Span<byte> block = stackalloc byte[HashSize];
            using (var hmac = new HMACSHA256(prkBytes))
            {
                var previousLength = 0;
                var counter = 1;
                for (var offset = 0; offset < output.Length; offset += HashSize, counter++)
                {
                    // T(i-1) is already at the start of input, from the previous iteration.
                    info.CopyTo(input.AsSpan(previousLength));
                    input[previousLength + info.Length] = (byte)counter;
                    Hash(hmac, input.AsSpan(0, previousLength + info.Length + 1), block);

                    var count = Math.Min(HashSize, output.Length - offset);
                    block.Slice(0, count).CopyTo(output.Slice(offset));
                    block.CopyTo(input);
                    previousLength = HashSize;
                }
            }
            CryptographicOperations.ZeroMemory(block);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(prkBytes);
        }

        private static void Hash(HMACSHA256 hmac, ReadOnlySpan<byte> data, Span<byte> destination)
        {
            if (!hmac.TryComputeHash(data, destination, out var written) || written != HashSize)
                throw new CryptographicException("HMAC-SHA256 did not produce a full hash.");
        }
    }
}
