using Shared.Simulation;

namespace GameServer.Combat;

/// <summary>
/// A short ring of where an entity stood at the end of recent ticks, for the position rewind of R-3.1-03 (players hitting
/// monsters are checked against what the attacker saw, capped at <c>StartingValuesM1.lagCompensationCapMs</c>). Fixed
/// size, so recording allocates nothing. Filled at the end of tick phase 2; read by HitResolver (WP4).
/// </summary>
public sealed class PositionHistory
{
    private readonly uint[] _ticks;
    private readonly FixVec2[] _positions;
    private int _newest = -1;
    private int _count;

    public PositionHistory(int capacity)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity), "must be positive");
        _ticks = new uint[capacity];
        _positions = new FixVec2[capacity];
    }

    public int Capacity => _ticks.Length;

    public int Count => _count;

    /// <summary>Records the position at the end of <paramref name="tick"/>; the same tick again overwrites it.</summary>
    public void Record(uint tick, FixVec2 position)
    {
        if (_count > 0 && _ticks[_newest] == tick)
        {
            _positions[_newest] = position;
            return;
        }
        _newest = (_newest + 1) % _ticks.Length;
        _ticks[_newest] = tick;
        _positions[_newest] = position;
        if (_count < _ticks.Length)
            _count++;
    }

    /// <summary>Forgets everything (a teleport or respawn must not be rewound across).</summary>
    public void Clear()
    {
        _newest = -1;
        _count = 0;
    }

    /// <summary>
    /// The position at the end of the newest recorded tick not after <paramref name="tick"/>; a tick older than the
    /// history gets the oldest entry (the cap). False only when nothing is recorded.
    /// </summary>
    public bool TryGetAt(uint tick, out FixVec2 position)
    {
        if (_count == 0)
        {
            position = default;
            return false;
        }
        int index = _newest;
        for (int i = 0; i < _count; i++)
        {
            if ((int)(tick - _ticks[index]) >= 0)
            {
                position = _positions[index];
                return true;
            }
            if (i < _count - 1)
                index = (index - 1 + _ticks.Length) % _ticks.Length;
        }
        position = _positions[index]; // older than everything recorded: the oldest entry
        return true;
    }

    /// <summary>
    /// The position on a view clock in 1/64 ticks (<c>ViewTick * 64 + SubTick</c>, R-3.1-04): between the ends of its tick and
    /// the next by the fraction (<see cref="LockAim.Lerp64"/>), as the client interpolates snapshots. Without the next tick
    /// recorded, its tick's position (<see cref="TryGetAt"/>, the cap for older ones). False only when nothing is recorded.
    /// </summary>
    public bool TryGetAtView64(long view64, out FixVec2 position)
    {
        if (view64 < 0)
            view64 = 0;
        uint tick = (uint)(view64 / LockAim.SubTicks);
        int sub = (int)(view64 % LockAim.SubTicks);
        if (!TryGetAt(tick, out position))
            return false;
        if (sub > 0 && _ticks[_newest] != tick && (int)(_ticks[_newest] - tick) > 0 && TryGetAt(tick + 1, out var next))
            position = LockAim.Lerp64(position, next, sub);
        return true;
    }
}
