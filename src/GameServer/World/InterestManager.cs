using GameServer.Entities;

namespace GameServer.World;

/// <summary>What one observer currently has in interest, and what changed at the last update. Sorted by entity id.</summary>
internal sealed class InterestSet
{
    private List<Entity> _visible = new();
    private List<Entity> _next = new();

    /// <summary>Entities in interest, the observer included.</summary>
    public IReadOnlyList<Entity> Visible => _visible;

    /// <summary>Entities that came into interest at the last update (spawn events).</summary>
    public List<Entity> Entered { get; } = new();

    /// <summary>Ids of entities that left interest at the last update (despawn events).</summary>
    public List<uint> Left { get; } = new();

    /// <summary>True when the entity was in interest at the last update (binary search: the list is in id order).</summary>
    public bool Contains(Entity entity)
    {
        int low = 0, high = _visible.Count - 1;
        uint id = entity.Id;
        while (low <= high)
        {
            int mid = (low + high) >> 1;
            uint midId = _visible[mid].Id;
            if (midId == id)
                return _visible[mid] == entity;
            if (midId < id)
                low = mid + 1;
            else
                high = mid - 1;
        }
        return false;
    }

    internal List<Entity> Next => _next;

    internal List<Entity> VisibleList => _visible;

    internal void Swap() => (_visible, _next) = (_next, _visible);

    // Spawns the client has not confirmed (spec 1.7, protocol 53), oldest first; grows, never shrinks.
    private PendingSpawn[] _pending = new PendingSpawn[16];

    /// <summary>Entities in interest whose spawn the client has not acknowledged yet, oldest first (the observer's own included).</summary>
    public int PendingSpawnCount { get; private set; }

    internal ref PendingSpawn PendingSpawnAt(int index) => ref _pending[index];

    /// <summary>True once a snapshot went to this observer since the last reset; <see cref="LastSnapshotTick"/> is its tick.</summary>
    public bool HasSentSnapshot { get; private set; }

    /// <summary>The server tick of the newest snapshot sent to this observer: no acknowledgement can name a later one.</summary>
    public uint LastSnapshotTick { get; private set; }

    internal void SnapshotSent(uint tick)
    {
        HasSentSnapshot = true;
        LastSnapshotTick = tick;
    }

    /// <summary>
    /// After an update: drops the pending spawns of entities that left (their despawn went out; a return is a new spawn) and
    /// appends the entities that entered.
    /// </summary>
    internal void TrackSpawns()
    {
        int kept = 0;
        for (int i = 0; i < PendingSpawnCount; i++)
        {
            if (Contains(_pending[i].Entity))
                _pending[kept++] = _pending[i];
        }
        for (int i = kept; i < PendingSpawnCount; i++)
            _pending[i] = default; // no stale entity references
        PendingSpawnCount = kept;
        foreach (var entity in Entered)
        {
            if (PendingSpawnCount == _pending.Length)
                Array.Resize(ref _pending, _pending.Length * 2);
            _pending[PendingSpawnCount++] = new PendingSpawn { Entity = entity };
        }
    }

    /// <summary>
    /// The client has the snapshot of <paramref name="ackTick"/>: every spawn written in each snapshot since a tick at or
    /// before it reached the client, so it leaves the list.
    /// </summary>
    internal void ConfirmSpawns(uint ackTick)
    {
        int kept = 0;
        for (int i = 0; i < PendingSpawnCount; i++)
        {
            ref var spawn = ref _pending[i];
            if (!(spawn.InRun && (int)(ackTick - spawn.RunStartTick) >= 0))
                _pending[kept++] = spawn;
        }
        for (int i = kept; i < PendingSpawnCount; i++)
            _pending[i] = default;
        PendingSpawnCount = kept;
    }

    /// <summary>Forgets what the observer saw, so the next update reports everything in interest as entered.</summary>
    internal void Reset()
    {
        _visible.Clear();
        _next.Clear();
        Entered.Clear();
        Left.Clear();
        Array.Clear(_pending, 0, PendingSpawnCount);
        PendingSpawnCount = 0;
        HasSentSnapshot = false;
        LastSnapshotTick = 0;
    }
}

/// <summary>
/// A spawn one observer has not confirmed (spec 1.7, protocol 53). <see cref="InRun"/>: it was written in every snapshot sent
/// since <see cref="RunStartTick"/>, so an acknowledgement of any of those ticks confirms it; a snapshot it did not fit
/// breaks the run. <see cref="EverWritten"/>: some snapshot carried it, so the entity's state may go out.
/// </summary>
internal struct PendingSpawn
{
    public Entity Entity;
    public uint RunStartTick;
    public bool InRun;
    public bool EverWritten;
}

/// <summary>
/// Computes each observer's interest set from nearby grid cells and the enter/leave deltas that become spawn and
/// despawn events (spec 2.3, R-2.3-03).
/// <para>
/// Shape: R-2.3-03 asks for the camera's ground footprint plus a margin. The camera is fixed, so its footprint is
/// aligned to the world axes; for M1 the area is a square with half-size <c>StartingValuesM1.InterestRadiusCm</c>
/// around the observer, standing for footprint plus margin.
/// TODO(open): the real footprint (camera-aligned rectangle or ellipse) once the camera numbers exist, and whether
/// leaving needs a larger threshold than entering (hysteresis) to stop spawn/despawn flicker at the border.
/// </para>
/// Allocates nothing once its lists have grown. Simulation thread only.
/// </summary>
public sealed class InterestManager
{
    // A cached Comparison: List.Sort(IComparer) would allocate a delegate on every call.
    private static readonly Comparison<Entity> ById = (a, b) => a.Id.CompareTo(b.Id);

    private readonly SpatialGrid _grid;
    private readonly int _halfSizeCm;
    private readonly IVisibilityQuery _visibility;
    private readonly List<Entity> _candidates = new();

    public InterestManager(SpatialGrid grid, int interestRadiusCm, IVisibilityQuery visibility)
    {
        if (interestRadiusCm <= 0)
            throw new ArgumentOutOfRangeException(nameof(interestRadiusCm), "must be positive");
        _grid = grid;
        _halfSizeCm = interestRadiusCm;
        _visibility = visibility;
    }

    public void Update(Player observer)
    {
        var interest = observer.Interest;
        int x = observer.XCm, y = observer.YCm;

        _candidates.Clear();
        _grid.QueryCells(x - _halfSizeCm, y - _halfSizeCm, x + _halfSizeCm, y + _halfSizeCm, _candidates);

        var next = interest.Next;
        next.Clear();
        foreach (var candidate in _candidates)
        {
            // An observer always sees itself: the client needs its own authoritative state (spec 2.2).
            if (candidate == observer || (IsInside(x, y, candidate) && _visibility.CanSee(observer, candidate)))
                next.Add(candidate);
        }
        next.Sort(ById);

        Diff(interest.VisibleList, next, interest.Entered, interest.Left);
        interest.Swap();
        interest.TrackSpawns();
    }

    private bool IsInside(int x, int y, Entity target) =>
        Math.Abs((long)target.XCm - x) <= _halfSizeCm && Math.Abs((long)target.YCm - y) <= _halfSizeCm;

    /// <summary>Merge of two id-sorted lists.</summary>
    private static void Diff(List<Entity> before, List<Entity> after, List<Entity> entered, List<uint> left)
    {
        entered.Clear();
        left.Clear();
        int i = 0, j = 0;
        while (i < before.Count || j < after.Count)
        {
            if (j == after.Count || (i < before.Count && before[i].Id < after[j].Id))
            {
                left.Add(before[i].Id);
                i++;
            }
            else if (i == before.Count || after[j].Id < before[i].Id)
            {
                entered.Add(after[j]);
                j++;
            }
            else
            {
                // Same id. A removed entity whose id came back would be a new entity, but ids are never reused.
                i++;
                j++;
            }
        }
    }
}
