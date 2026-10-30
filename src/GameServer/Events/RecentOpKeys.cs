namespace GameServer.Events;

/// <summary>
/// The operation keys of activity events a consumer saw lately, per character (architecture rule 3: consumers are
/// idempotent; WP-M3-1). The bus's <see cref="EventWatermark"/> drops a re-dispatched envelope; this drops a second event
/// for the same operation (a producer that publishes twice), so a repeated <c>Harvested</c> adds nothing. Bounded: each
/// character keeps its last <see cref="Capacity"/> keys, in memory while it is on the world. World thread only.
/// </summary>
public sealed class RecentOpKeys
{
    private readonly Dictionary<long, Ring> _byCharacter = new();

    public RecentOpKeys(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>True the first time <paramref name="opKey"/> is seen for <paramref name="characterId"/> (and remembers it); false for a repeat.</summary>
    public bool TryAccept(long characterId, string opKey)
    {
        if (!_byCharacter.TryGetValue(characterId, out var ring))
            _byCharacter[characterId] = ring = new Ring(Capacity);
        return ring.TryAdd(opKey);
    }

    /// <summary>The character left the world.</summary>
    public void Forget(long characterId) => _byCharacter.Remove(characterId);

    private sealed class Ring(int capacity)
    {
        private readonly string[] _keys = new string[capacity];
        private readonly HashSet<string> _set = new(StringComparer.Ordinal);
        private int _next;

        public bool TryAdd(string key)
        {
            if (!_set.Add(key))
                return false;
            if (_keys[_next] is { } oldest)
                _set.Remove(oldest);
            _keys[_next] = key;
            _next = (_next + 1) % _keys.Length;
            return true;
        }
    }
}
