namespace GameServer.Items.LootBags;

/// <summary>
/// A player's loot bag traffic on its connection (WP-M4-9): answers and contents written in order (sent in phase 10), the bag it
/// has open, and whether its own bags must be sent again. Reset for every new connection.
/// </summary>
public sealed class LootBagAnswers
{
    private const int RememberedRequests = 32;
    private readonly Queue<uint> _seenOrder = new();
    private readonly HashSet<uint> _seen = new();

    /// <summary>Encoded reliable messages (results and contents), in the order they were written.</summary>
    public List<byte[]> Messages { get; } = new();

    /// <summary>The bag this connection has open (0: none): its contents follow every committed change while it stays in reach.</summary>
    public uint OpenBagEntityId { get; set; }

    /// <summary>The open bag's change counter last sent (-1: never), so a contents message goes out only after a change.</summary>
    public long SentBagRevision { get; set; } = -1;

    public uint ContentRevision { get; set; }

    /// <summary>The own bags changed (a death's bag, a take, an expiry) or the connection is new: an <c>OwnLootBags</c> goes out.</summary>
    public bool OwnBagsDirty { get; set; } = true;

    public uint OwnBagsRevision { get; set; }

    /// <summary>
    /// First sight of <paramref name="requestId"/> on this connection: true once, so a repeated request (a resend) is applied
    /// and answered once (the last <see cref="RememberedRequests"/> ids are kept).
    /// </summary>
    public bool FirstSight(uint requestId)
    {
        if (!_seen.Add(requestId))
            return false;
        _seenOrder.Enqueue(requestId);
        if (_seenOrder.Count > RememberedRequests)
            _seen.Remove(_seenOrder.Dequeue());
        return true;
    }

    /// <summary>A new connection: nothing open, everything to send again, request ids start again.</summary>
    public void Reset()
    {
        Messages.Clear();
        OpenBagEntityId = 0;
        SentBagRevision = -1;
        ContentRevision = 0;
        OwnBagsDirty = true;
        OwnBagsRevision = 0;
        _seen.Clear();
        _seenOrder.Clear();
    }

    /// <summary>Answers no client will read (the player is not connected).</summary>
    public void ClearAnswers()
    {
        Messages.Clear();
        OpenBagEntityId = 0;
        SentBagRevision = -1;
    }
}
