using GameServer.Entities;

namespace GameServer.Combat;

/// <summary>
/// Monsters' hits on players waiting for the defender's input (R-3.1-03 pending hit), in the order they happened: phase 4
/// projectiles in projectile order, then phase 5 strikes in attacker-id order, older ticks first. A hit is due once the
/// defender's view lag has passed since it happened (<see cref="DefenceTimeline.LagTicksCeil"/>), never later than
/// <c>lagCompensationCapMs</c>: by then every input the defender sent while seeing that tick has been applied.
/// Grows when needed and never shrinks, so steady state allocates nothing. World simulation thread only.
/// </summary>
internal sealed class PendingHits
{
    private readonly List<HitRequest> _hits = new(16);

    public int Count => _hits.Count;

    public HitRequest this[int index] => _hits[index];

    public void Add(in HitRequest request) => _hits.Add(request);

    /// <summary>The tick a pending hit is due on, for a defender with this view lag.</summary>
    public static uint DueTick(in HitRequest request, Player defender, int capTicks) =>
        request.Tick + (uint)Math.Min(capTicks, defender.Defence.LagTicksCeil);

    /// <summary>
    /// Hands each due hit to <paramref name="land"/>, in order, and keeps the rest in order. A hit whose target left the
    /// world (<see cref="Drop"/>) is gone already.
    /// </summary>
    public void TakeDue(uint tick, int capTicks, Action<HitRequest> land)
    {
        int kept = 0;
        for (int i = 0; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            if (hit.Target is not Player defender || DueTick(hit, defender, capTicks) <= tick)
                land(hit);
            else
                _hits[kept++] = hit;
        }
        _hits.RemoveRange(kept, _hits.Count - kept);
    }

    /// <summary>Drops the hits on a target that left the world.</summary>
    public void Drop(Entity target)
    {
        int kept = 0;
        for (int i = 0; i < _hits.Count; i++)
        {
            if (_hits[i].Target != target)
                _hits[kept++] = _hits[i];
        }
        _hits.RemoveRange(kept, _hits.Count - kept);
    }
}
