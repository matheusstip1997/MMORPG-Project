using GameServer.Combat;
using GameServer.Entities;
using Shared.Protocol;
using Shared.Simulation;

namespace GameServer.Net;

/// <summary>
/// Tick phase 10, every tick: ground area spawns and despawns on the unreliable channel (spec 1.7, 3.1; protocol 9), one
/// <see cref="HotMessageId.GroundAreaSpawn"/> and one <see cref="HotMessageId.GroundAreaDespawn"/> message per player (more
/// when they do not fit <c>maxSnapshotBytes</c> or the peer's current limit, <see cref="ITransport.MaxUnreliableBytes"/>).
/// Reused buffers; sending allocates nothing once the areas' observer lists have grown.
/// <para>
/// Who gets a spawn: a connected player whose interest square (the one <see cref="World.InterestManager"/> uses) overlaps the
/// square the area's shapes can reach, checked every tick while the area is live, so a player who walks up later gets it
/// too. Who gets a despawn: every player that got the spawn. Each event is sent <c>GroundAreaSettings.eventRepeatTicks</c>
/// ticks in a row against loss; clients drop duplicates by area id.
/// TODO(open): whether a player that cannot see a hidden owner (spec 2.4) gets its areas (rule 9), as for projectiles. M1
/// has no hiding, so everyone gets them. TODO: O(players x areas) per tick is fine for M1; use the grid for hundreds.
/// </para>
/// </summary>
internal sealed class GroundAreaSender
{
    private readonly ITransport _transport;
    private readonly int _halfSizeCm;
    private readonly int _repeats;
    private readonly byte[] _spawnBuffer;
    private readonly byte[] _despawnBuffer;

    public GroundAreaSender(ITransport transport, int maxMessageBytes, int interestRadiusCm, int eventRepeatTicks)
    {
        if (GroundAreaMessages.SpawnsThatFit(maxMessageBytes) < 1 || GroundAreaMessages.DespawnsThatFit(maxMessageBytes) < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), "must hold one ground area spawn");
        _transport = transport;
        _halfSizeCm = interestRadiusCm;
        _repeats = eventRepeatTicks;
        _spawnBuffer = new byte[maxMessageBytes];
        _despawnBuffer = new byte[maxMessageBytes];
    }

    public long SpawnsSent { get; private set; }

    public long DespawnsSent { get; private set; }

    public void Send(Player observer, IReadOnlyList<GroundArea> areas, uint tick)
    {
        if (areas.Count == 0)
            return;
        // Each message is one datagram within the peer's current limit (R-2.5-03); 0 when the peer is gone.
        int limit = Math.Min(_spawnBuffer.Length, _transport.MaxUnreliableBytes(observer.PeerId));
        if (GroundAreaMessages.SpawnsThatFit(limit) < 1 || GroundAreaMessages.DespawnsThatFit(limit) < 1)
            return;
        var spawns = new GroundAreaSpawnWriter(_spawnBuffer.AsSpan(0, limit));
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            int index = ObserverIndex(area, observer);
            if (index < 0)
            {
                if (!area.IsLive || !InInterest(area, observer))
                    continue;
                area.Observers.Add(new GroundAreaObserver { Player = observer, PeerId = observer.PeerId });
                index = area.Observers.Count - 1;
            }
            var entry = area.Observers[index];
            if (entry.SpawnSends >= _repeats)
                continue;
            var spawn = area.ToSpawn();
            if (!spawns.TryAdd(spawn))
            {
                Flush(observer, ref spawns);
                spawns = new GroundAreaSpawnWriter(_spawnBuffer.AsSpan(0, limit));
                spawns.TryAdd(spawn);
            }
            entry.SpawnSends++;
            area.Observers[index] = entry;
            SpawnsSent++;
        }
        Flush(observer, ref spawns);

        var despawns = new GroundAreaDespawnWriter(_despawnBuffer.AsSpan(0, limit));
        for (int i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            if (area.IsLive || ObserverIndex(area, observer) < 0)
                continue;
            var despawn = new GroundAreaDespawn { AreaId = area.Id, Tick = area.EndTick, Reason = area.Reason };
            if (!despawns.TryAdd(despawn))
            {
                Flush(observer, ref despawns);
                despawns = new GroundAreaDespawnWriter(_despawnBuffer.AsSpan(0, limit));
                despawns.TryAdd(despawn);
            }
            DespawnsSent++;
        }
        Flush(observer, ref despawns);
    }

    private void Flush(Player observer, ref GroundAreaSpawnWriter writer)
    {
        if (writer.Count == 0)
            return;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _spawnBuffer.AsSpan(0, length));
    }

    private void Flush(Player observer, ref GroundAreaDespawnWriter writer)
    {
        if (writer.Count == 0)
            return;
        int length = writer.Finish();
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _despawnBuffer.AsSpan(0, length));
    }

    private static int ObserverIndex(GroundArea area, Player observer)
    {
        var observers = area.Observers;
        for (int i = 0; i < observers.Count; i++)
        {
            if (observers[i].Player == observer && observers[i].PeerId == observer.PeerId)
                return i;
        }
        return -1;
    }

    /// <summary>Whether the square the area's shapes reach overlaps the observer's interest square (inclusive edges).</summary>
    private bool InInterest(GroundArea area, Player observer)
    {
        long reach = (long)area.Shapes.ReachCm + _halfSizeCm;
        long dx = Math.Abs((long)FixedPoint.ToCmFloor(area.Origin.X) - observer.XCm);
        long dy = Math.Abs((long)FixedPoint.ToCmFloor(area.Origin.Y) - observer.YCm);
        return dx <= reach && dy <= reach;
    }
}
