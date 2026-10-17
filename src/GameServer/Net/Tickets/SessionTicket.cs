using System.Buffers.Binary;
using System.Security.Cryptography;

namespace GameServer.Net.Tickets;

/// <summary>
/// Signed, single-use session ticket issued by the Auth API (R-2.5-01).
/// The session key of R-2.5-02 is added in a later ticket version together with channel encryption.
/// </summary>
public sealed record SessionTicket(long AccountId, int RealmId, byte Slot, long ExpiresAtMs, byte[] Nonce, byte KeyId)
{
    public const byte CurrentVersion = 1;
    public const int NonceSize = 16;
}

/// <summary>
/// Binary layout (little endian):
/// byte version, long accountId, int realmId, byte slot, long expiresAtMs, byte[16] nonce, byte keyId,
/// then HMAC-SHA256 of all previous bytes.
/// </summary>
public static class SessionTicketCodec
{
    public const int PayloadSize = 1 + 8 + 4 + 1 + 8 + SessionTicket.NonceSize + 1;
    public const int SignatureSize = 32;
    public const int Size = PayloadSize + SignatureSize;

    public static byte[] Encode(SessionTicket ticket, ReadOnlySpan<byte> key)
    {
        if (ticket.Nonce.Length != SessionTicket.NonceSize)
            throw new ArgumentException($"Nonce must be {SessionTicket.NonceSize} bytes.", nameof(ticket));

        var bytes = new byte[Size];
        var span = bytes.AsSpan();
        span[0] = SessionTicket.CurrentVersion;
        BinaryPrimitives.WriteInt64LittleEndian(span[1..], ticket.AccountId);
        BinaryPrimitives.WriteInt32LittleEndian(span[9..], ticket.RealmId);
        span[13] = ticket.Slot;
        BinaryPrimitives.WriteInt64LittleEndian(span[14..], ticket.ExpiresAtMs);
        ticket.Nonce.CopyTo(span[22..]);
        span[38] = ticket.KeyId;
        HMACSHA256.HashData(key, span[..PayloadSize], span[PayloadSize..]);
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

    public static bool HasValidSignature(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> key)
    {
        Span<byte> expected = stackalloc byte[SignatureSize];
        HMACSHA256.HashData(key, bytes[..PayloadSize], expected);
        return CryptographicOperations.FixedTimeEquals(expected, bytes.Slice(PayloadSize, SignatureSize));
    }
}
