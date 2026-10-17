using System;

namespace Shared.Security
{
    /// <summary>
    /// Authenticated encryption with associated data, 12-byte nonce and 16-byte tag. <see cref="SessionCrypto"/> uses it
    /// so the server can plug in the platform ChaCha20-Poly1305 and the Unity client the managed one (R-2.5-02).
    /// Instances are not thread safe.
    /// </summary>
    public interface IAead : IDisposable
    {
        /// <summary><paramref name="ciphertext"/> has the length of <paramref name="plaintext"/> and may be the same memory.</summary>
        void Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData);

        /// <summary>
        /// Returns false when the tag does not authenticate <paramref name="ciphertext"/> and
        /// <paramref name="associatedData"/>; <paramref name="plaintext"/> then holds no decrypted bytes (it may be cleared).
        /// </summary>
        bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData);
    }

    /// <summary>Creates an <see cref="IAead"/> for a 32-byte key. The implementation copies the key.</summary>
    public delegate IAead AeadFactory(ReadOnlySpan<byte> key);
}
