using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GameServer.Net.Tickets;

/// <summary>
/// Signed, single-use session ticket issued by the Auth API (R-2.5-01). Version 2 also carries the session key
/// encrypted with the server secret (R-2.5-02); the key is read with <see cref="SessionTicketCodec.TryDecryptSessionKey"/>
/// and is not part of this record, so it never leaves the transport.
/// </summary>
public sealed record SessionTicket(long AccountId, int RealmId, byte Slot, long ExpiresAtMs, byte[] Nonce, byte KeyId)
{
    public const byte CurrentVersion = 2;
    public const int NonceSize = 16;

    /// <summary>The session key the Auth API also gives the client over HTTPS (R-2.5-02).</summary>
    public const int SessionKeySize = 32;
}

/// <summary>
/// Binary layout (little endian), version 2:
/// byte version, long accountId, int realmId, byte slot, long expiresAtMs, byte[16] nonce, byte keyId,
/// byte[32] session key encrypted with ChaCha20-Poly1305 (the header above is associated data, the nonce is the first
/// 12 bytes of the ticket nonce), byte[16] Poly1305 tag, then HMAC-SHA256 of all previous bytes.
/// Any other version is refused: there is no fallback to version 1, which had no session key.
/// </summary>
public static class SessionTicketCodec
{
    public const int HeaderSize = 1 + 8 + 4 + 1 + 8 + SessionTicket.NonceSize + 1;
    public const int SealedKeyTagSize = 16;
    public const int PayloadSize = HeaderSize + SessionTicket.SessionKeySize + SealedKeyTagSize;
    public const int SignatureSize = 32;
    public const int Size = PayloadSize + SignatureSize;

    private const int AeadNonceSize = 12;

    public static byte[] Encode(SessionTicket ticket, ReadOnlySpan<byte> sessionKey, TicketKey key)
    {
        if (ticket.Nonce.Length != SessionTicket.NonceSize)
            throw new ArgumentException($"Nonce must be {SessionTicket.NonceSize} bytes.", nameof(ticket));
        if (sessionKey.Length != SessionTicket.SessionKeySize)
            throw new ArgumentException($"Session key must be {SessionTicket.SessionKeySize} bytes.", nameof(sessionKey));

        var bytes = new byte[Size];
        var span = bytes.AsSpan();
        span[0] = SessionTicket.CurrentVersion;
        BinaryPrimitives.WriteInt64LittleEndian(span[1..], ticket.AccountId);
        BinaryPrimitives.WriteInt32LittleEndian(span[9..], ticket.RealmId);
        span[13] = ticket.Slot;
        BinaryPrimitives.WriteInt64LittleEndian(span[14..], ticket.ExpiresAtMs);
        ticket.Nonce.CopyTo(span[22..]);
        span[38] = ticket.KeyId;

        using (var aead = new ChaCha20Poly1305(key.EncryptionKey))
        {
            aead.Encrypt(
                span.Slice(22, AeadNonceSize),
                sessionKey,
                span.Slice(HeaderSize, SessionTicket.SessionKeySize),
                span.Slice(HeaderSize + SessionTicket.SessionKeySize, SealedKeyTagSize),
                span[..HeaderSize]);
        }

        HMACSHA256.HashData(key.SigningKey, span[..PayloadSize], span[PayloadSize..]);
        return bytes;
    }

    /// <summary>Reads the unsigned fields. Call <see cref="HasValidSignature"/> before trusting them.</summary>
    public static bool TryReadPayload(ReadOnlySpan<byte> bytes, out SessionTicket ticket)
    {
        ticket = null!;
        if (bytes.Length != Size || bytes[0] != SessionTicket.CurrentVersion)
            return false;

        ticket = new SessionTicket(
            AccountId: BinaryPrimitives.ReadInt64LittleEndian(bytes[1..]),
            RealmId: BinaryPrimitives.ReadInt32LittleEndian(bytes[9..]),
            Slot: bytes[13],
            ExpiresAtMs: BinaryPrimitives.ReadInt64LittleEndian(bytes[14..]),
            Nonce: bytes.Slice(22, SessionTicket.NonceSize).ToArray(),
            KeyId: bytes[38]);
        return true;
    }

    public static bool HasValidSignature(ReadOnlySpan<byte> bytes, TicketKey key)
    {
        if (bytes.Length != Size)
            return false;
        Span<byte> expected = stackalloc byte[SignatureSize];
        HMACSHA256.HashData(key.SigningKey, bytes[..PayloadSize], expected);
        return CryptographicOperations.FixedTimeEquals(expected, bytes.Slice(PayloadSize, SignatureSize));
    }

    /// <summary>Decrypts the session key into <paramref name="sessionKey"/>. Returns false if it does not authenticate.</summary>
    public static bool TryDecryptSessionKey(ReadOnlySpan<byte> bytes, TicketKey key, Span<byte> sessionKey)
    {
        if (bytes.Length != Size || sessionKey.Length != SessionTicket.SessionKeySize)
            return false;

        using var aead = new ChaCha20Poly1305(key.EncryptionKey);
        try
        {
            aead.Decrypt(
                bytes.Slice(22, AeadNonceSize),
                bytes.Slice(HeaderSize, SessionTicket.SessionKeySize),
                bytes.Slice(HeaderSize + SessionTicket.SessionKeySize, SealedKeyTagSize),
                sessionKey,
                bytes[..HeaderSize]);
            return true;
        }
        catch (AuthenticationTagMismatchException)
        {
            return false;
        }
    }
}
