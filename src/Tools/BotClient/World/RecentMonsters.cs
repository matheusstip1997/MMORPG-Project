using Shared.Simulation;

namespace BotClient.World;

/// <summary>
/// The monsters the bot saw last, kept past their despawn: a <c>Killed</c> hit can arrive after the snapshot that removed its
/// victim, and the bot still has to count the kill against the victim's type and note where it died (#139 review: a
/// <c>fight:&lt;monster&gt;</c> camp count missed such kills). A ring of the newest entries, so it stays bounded.
/// </summary>
public sealed class RecentMonsters
{
    public const int Capacity = 256;

    private readonly Dictionary<uint, (int DefId, FixVec2 At)> _byId = new();
    private readonly Queue<uint> _order = new();

    /// <summary>Records what a snapshot shows of monster <paramref name="id"/>.</summary>
    public void Note(uint id, int defId, FixVec2 at)
    {
        if (!_byId.ContainsKey(id))
        {
            if (_order.Count == Capacity)
                _byId.Remove(_order.Dequeue());
            _order.Enqueue(id);
        }
        _byId[id] = (defId, at);
    }

    /// <summary>The newest record of monster <paramref name="id"/>; false when the bot never saw it (or it fell out of the ring).</summary>
    public bool TryGet(uint id, out int defId, out FixVec2 at)
    {
        bool known = _byId.TryGetValue(id, out var seen);
        defId = seen.DefId;
        at = seen.At;
        return known;
    }
}
