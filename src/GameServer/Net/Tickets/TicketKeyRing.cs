namespace GameServer.Net.Tickets;

/// <summary>
/// Ticket signing keys by key ID, so keys can rotate (R-2.5-05). Keys come from the parameter store
/// or the environment, never from the repository.
/// </summary>
public sealed class TicketKeyRing
{
    /// <summary>HMAC-SHA256 keys shorter than the hash size are weak.</summary>
    public const int MinKeySize = 32;

    private readonly Dictionary<byte, byte[]> _keys = new();

    public TicketKeyRing Add(byte keyId, byte[] key)
    {
        if (key.Length < MinKeySize)
            throw new ArgumentException($"Ticket keys must be at least {MinKeySize} bytes.", nameof(key));
        _keys[keyId] = key.ToArray();
        return this;
    }

    public bool TryGet(byte keyId, out byte[] key) => _keys.TryGetValue(keyId, out key!);
}
