using System.Security.Cryptography;
using Shared.Security;

namespace GameServer.Net.Security;

/// <summary>
/// The platform ChaCha20-Poly1305 behind <see cref="IAead"/>: the server's fast path for <see cref="SessionCrypto"/>
/// (R-2.5-02). The Unity client uses <see cref="ManagedChaCha20Poly1305"/>; interop tests prove they match.
/// </summary>
public sealed class SystemChaCha20Poly1305 : IAead
{
    private readonly ChaCha20Poly1305 _aead;

    public SystemChaCha20Poly1305(ReadOnlySpan<byte> key)
    {
        if (!ChaCha20Poly1305.IsSupported)
            throw new PlatformNotSupportedException("ChaCha20-Poly1305 is not available on this platform (R-2.5-02).");
        _aead = new ChaCha20Poly1305(key);
    }

    public static IAead Create(ReadOnlySpan<byte> key) => new SystemChaCha20Poly1305(key);

    public void Seal(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData) =>
        _aead.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

    public bool TryOpen(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        try
        {
            _aead.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }

    public void Dispose() => _aead.Dispose();
}
