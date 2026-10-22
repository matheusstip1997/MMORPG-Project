using GameServer.Combat;
using GameServer.Entities;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Net;

/// <summary>
/// Tick phase 10, every tick: projectile spawns and despawns on the unreliable channel (spec 1.7, 3.2), one
/// <see cref="HotMessageId.ProjectileSpawn"/> and one <see cref="HotMessageId.ProjectileDespawn"/> message per player
/// (more when they do not fit <c>maxSnapshotBytes</c>). Reused buffers; sending allocates nothing once the projectiles'
/// observer lists have grown.
/// <para>
/// Who gets a spawn: a connected player whose interest area the part of the path still to draw crosses (from where the
/// projectile was before this tick's step to the end of its path), checked every tick, so a player who walks into the path
/// later gets the spawn too and its client fast-forwards from the start tick. The square is the one
/// <see cref="World.InterestManager"/> uses. A projectile that ended this tick is still offered to new players for its
/// last segment, so a point-blank shot is drawn. Who gets a despawn: every player that got the spawn.
/// </para>
/// <para>
/// Loss: each spawn and each despawn is sent <c>ProjectileSettings.eventRepeatTicks</c> ticks in a row (like the input
/// redundancy of spec 1.7); clients drop duplicates by projectile id.
/// TODO(open): whether a player that cannot see a hidden owner (spec 2.4) gets its projectiles; the origin shows where the
/// owner stood (rule 9). M1 has no hiding, so everyone gets them.
/// TODO: O(players x projectiles) per tick is fine for M1; use the spatial grid for hundreds of players.
/// </para>
/// </summary>
internal sealed class ProjectileSender
{
    private readonly ITransport _transport;
    private readonly int _halfSizeCm;
    private readonly int _repeats;
    private readonly byte[] _spawnBuffer;
    private readonly byte[] _despawnBuffer;

    public ProjectileSender(ITransport transport, int maxMessageBytes, int interestRadiusCm, int eventRepeatTicks)
    {
        if (ProjectileMessages.SpawnsThatFit(maxMessageBytes) < 1 || ProjectileMessages.DespawnsThatFit(maxMessageBytes) < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), "must hold one projectile spawn");
        _transport = transport;
        _halfSizeCm = interestRadiusCm;
        _repeats = eventRepeatTicks;
        _spawnBuffer = new byte[maxMessageBytes];
        _despawnBuffer = new byte[maxMessageBytes];
    }

    public long SpawnsSent { get; private set; }

    public long DespawnsSent { get; private set; }

    public void Send(Player observer, IReadOnlyList<Projectile> projectiles, uint tick)
    {
        if (projectiles.Count == 0)
            return;
        var spawns = new ProjectileSpawnWriter(_spawnBuffer);
        for (int i = 0; i < projectiles.Count; i++)
        {
            var projectile = projectiles[i];
            int index = ObserverIndex(projectile, observer);
            if (index < 0)
            {
                bool offered = projectile.IsLive || projectile.EndTick == tick;
                if (!offered || !CrossesInterest(projectile, observer))
                    continue;
                projectile.Observers.Add(new ProjectileObserver { Player = observer, PeerId = observer.PeerId });
                index = projectile.Observers.Count - 1;
            }
            var entry = projectile.Observers[index];
            if (entry.SpawnSends >= _repeats)
                continue;
            var spawn = projectile.ToSpawn();
            if (!spawns.TryAdd(spawn))
            {
                Flush(observer, ref spawns);
                spawns = new ProjectileSpawnWriter(_spawnBuffer);
                spawns.TryAdd(spawn);
            }
            entry.SpawnSends++;
            projectile.Observers[index] = entry;
            SpawnsSent++;
        }
        Flush(observer, ref spawns);

        var despawns = new ProjectileDespawnWriter(_despawnBuffer);
        for (int i = 0; i < projectiles.Count; i++)
        {
            var projectile = projectiles[i];
            if (projectile.IsLive || ObserverIndex(projectile, observer) < 0)
                continue;
            var despawn = new ProjectileDespawn { ProjectileId = projectile.Id, Tick = projectile.EndTick, Reason = projectile.Reason };
            if (!despawns.TryAdd(despawn))
            {
                Flush(observer, ref despawns);
                despawns = new ProjectileDespawnWriter(_despawnBuffer);
                despawns.TryAdd(despawn);
            }
            DespawnsSent++;
        }
        Flush(observer, ref despawns);
    }

    private void Flush(Player observer, ref ProjectileSpawnWriter writer)
    {
        if (writer.Count == 0)
            return;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _spawnBuffer.AsSpan(0, length));
    }

    private void Flush(Player observer, ref ProjectileDespawnWriter writer)
    {
        if (writer.Count == 0)
            return;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _despawnBuffer.AsSpan(0, length));
    }

    private static int ObserverIndex(Projectile projectile, Player observer)
    {
        var observers = projectile.Observers;
        for (int i = 0; i < observers.Count; i++)
        {
            if (observers[i].Player == observer && observers[i].PeerId == observer.PeerId)
                return i;
        }
        return -1;
    }

    /// <summary>Whether the path still to draw (this tick's start to the path end) crosses the observer's interest square.</summary>
    private bool CrossesInterest(Projectile projectile, Player observer)
    {
        var from = projectile.SegmentStart;
        var to = projectile.IsLive ? projectile.Path.End : projectile.Position;
        return SegmentCrossesSquare(FixedPoint.ToCmFloor(from.X), FixedPoint.ToCmFloor(from.Y),
            FixedPoint.ToCmFloor(to.X), FixedPoint.ToCmFloor(to.Y), observer.XCm, observer.YCm, _halfSizeCm);
    }

    /// <summary>
    /// Exact segment-against-square test in whole cm (inclusive edges): the bounding boxes overlap and the square's corners
    /// are not all strictly on one side of the segment's line.
    /// </summary>
    internal static bool SegmentCrossesSquare(int ax, int ay, int bx, int by, int centerX, int centerY, int halfSize)
    {
        long minX = (long)centerX - halfSize, maxX = (long)centerX + halfSize;
        long minY = (long)centerY - halfSize, maxY = (long)centerY + halfSize;
        if (Math.Max(ax, bx) < minX || Math.Min(ax, bx) > maxX || Math.Max(ay, by) < minY || Math.Min(ay, by) > maxY)
            return false;
        long dx = (long)bx - ax, dy = (long)by - ay;
        int sign = 0;
        bool mixed = false;
        Side(dx, dy, minX - ax, minY - ay, ref sign, ref mixed);
        Side(dx, dy, maxX - ax, minY - ay, ref sign, ref mixed);
        Side(dx, dy, minX - ax, maxY - ay, ref sign, ref mixed);
        Side(dx, dy, maxX - ax, maxY - ay, ref sign, ref mixed);
        return mixed || sign == 0;
    }

    private static void Side(long dx, long dy, long px, long py, ref int sign, ref bool mixed)
    {
        int side = Math.Sign(dx * py - dy * px);
        if (side == 0)
        {
            mixed = true; // a corner on the line touches the segment (the boxes overlap)
            return;
        }
        if (sign == 0)
            sign = side;
        else if (sign != side)
            mixed = true;
    }
}
