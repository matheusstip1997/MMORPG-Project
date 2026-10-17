using System.Security.Cryptography;
using System.Text;

namespace GameServer.Net.Tickets;

/// <summary>
/// Ticket secrets by key ID, so keys can rotate (R-2.5-05). Secrets come from the parameter store
/// or the environment, never from the repository.
/// </summary>
public sealed class TicketKeyRing
{
    /// <summary>Secrets shorter than the HMAC-SHA256 hash size are weak.</summary>
    public const int MinKeySize = 32;

    private readonly Dictionary<byte, TicketKey> _keys = new();

    public TicketKeyRing Add(byte keyId, byte[] secret)
    {
        _keys[keyId] = TicketKey.FromSecret(secret);
        return this;
    }

    public bool TryGet(byte keyId, out TicketKey key) => _keys.TryGetValue(keyId, out key!);
}

/// <summary>
/// Keys derived from one ticket secret with HKDF-SHA256: one signs the ticket, the other encrypts the session key
/// inside it (R-2.5-02). One secret is never used directly for two purposes.
/// </summary>
public sealed class TicketKey
{
    private static readonly byte[] SigningInfo = Encoding.ASCII.GetBytes("mmorpg/ticket/v2/sign");
    private static readonly byte[] EncryptionInfo = Encoding.ASCII.GetBytes("mmorpg/ticket/v2/session-key");

    private TicketKey(byte[] signingKey, byte[] encryptionKey)
    {
        SigningKey = signingKey;
        EncryptionKey = encryptionKey;
    }

    internal byte[] SigningKey { get; }

    internal byte[] EncryptionKey { get; }

    public static TicketKey FromSecret(byte[] secret)
    {
        if (secret.Length < TicketKeyRing.MinKeySize)
            throw new ArgumentException($"Ticket keys must be at least {TicketKeyRing.MinKeySize} bytes.", nameof(secret));

        return new TicketKey(
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: [], info: SigningInfo),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt: [], info: EncryptionInfo));
    }
}
