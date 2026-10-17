using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Shared.Security
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD, RFC 8439 section 2.8, in managed code for the Unity client, which has no platform
    /// implementation (R-2.5-02). Byte for byte the same as <c>System.Security.Cryptography.ChaCha20Poly1305</c>; the
    /// server keeps the platform one for speed. The tag is checked in constant time before anything is decrypted.
    /// </summary>
    public sealed class ManagedChaCha20Poly1305 : IAead
    {
        public const int KeySize = ChaCha20.KeySize;
        public const int NonceSize = ChaCha20.NonceSize;
        public const int TagSize = Poly1305.TagSize;

        private readonly byte[] _key = new byte[KeySize];
        private bool _disposed;

        public ManagedChaCha20Poly1305(ReadOnlySpan<byte> key)
        {
            if (key.Length != KeySize)
                throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
            key.CopyTo(_key);
        }

        public static IAead Create(ReadOnlySpan<byte> key) => new ManagedChaCha20Poly1305(key);

        public void Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData)
        {
            CheckUsable();
            if (ciphertext.Length != plaintext.Length)
                throw new ArgumentException("Ciphertext must have the length of the plaintext.", nameof(ciphertext));
            if (tag.Length != TagSize)
                throw new ArgumentException($"Tag must be {TagSize} bytes.", nameof(tag));

            ChaCha20.Xor(_key, 1, nonce, plaintext, ciphertext);
            ComputeTag(nonce, ciphertext, associatedData, tag);
        }

        public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
        {
            CheckUsable();
            if (plaintext.Length != ciphertext.Length)
                throw new ArgumentException("Plaintext must have the length of the ciphertext.", nameof(plaintext));
            if (tag.Length != TagSize)
                throw new ArgumentException($"Tag must be {TagSize} bytes.", nameof(tag));

            Span<byte> expected = stackalloc byte[TagSize];
            ComputeTag(nonce, ciphertext, associatedData, expected);
            var ok = CryptographicOperations.FixedTimeEquals(expected, tag);
            CryptographicOperations.ZeroMemory(expected);
            if (!ok)
                return false;

            ChaCha20.Xor(_key, 1, nonce, ciphertext, plaintext);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
        }

        private void ComputeTag(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> associatedData, Span<byte> tag)
        {
            // The one-time Poly1305 key is the first half of keystream block 0 (RFC 8439 section 2.6).
            Span<byte> block = stackalloc byte[ChaCha20.BlockSize];
            ChaCha20.Block(_key, 0, nonce, block);
            var mac = new Poly1305(block.Slice(0, Poly1305.KeySize));
            CryptographicOperations.ZeroMemory(block);

            mac.UpdatePadded(associatedData);
            mac.UpdatePadded(ciphertext);
            Span<byte> lengths = stackalloc byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(lengths, (ulong)associatedData.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(lengths.Slice(8), (ulong)ciphertext.Length);
            mac.UpdatePadded(lengths);
            mac.Finish(tag);
        }

        private void CheckUsable()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ManagedChaCha20Poly1305));
        }
    }
}
