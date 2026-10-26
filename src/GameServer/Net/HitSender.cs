using GameServer.Entities;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary>
/// Sends each connected player the hits of this tick whose target it has in interest (<see cref="HitMessage"/>, hot
/// message on the unreliable channel, tick phase 10, every tick). Interest already applies the visibility rule
/// (architecture rule 9), so a hidden target's hits are never sent. A target that left interest this tick (a monster
/// killed and removed in phase 8) still counts, so the killing blow reaches whoever saw it. Hits that do not fit one
/// message (<c>maxSnapshotBytes</c> or the peer's current limit, <see cref="ITransport.MaxUnreliableBytes"/>) go out in
/// further messages. One reused buffer and entry array; sending allocates nothing.
/// </summary>
internal sealed class HitSender
{
    private readonly ITransport _transport;
    private readonly byte[] _buffer;
    private readonly HitEntry[] _batch;

    public HitSender(ITransport transport, int maxMessageBytes)
    {
        int perMessage = HitMessage.MaxHitsFor(maxMessageBytes);
        if (perMessage < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes), "must hold one hit");
        _transport = transport;
        _buffer = new byte[HitMessage.SizeFor(perMessage)];
        _batch = new HitEntry[perMessage];
    }

    public long MessagesSent { get; private set; }

    public void Send(Player observer, uint serverTick, IReadOnlyList<HitEntry> hits)
    {
        // Each message is one datagram within the peer's current limit (R-2.5-03); 0 when the peer is gone.
        int perMessage = Math.Min(_batch.Length, HitMessage.MaxHitsFor(_transport.MaxUnreliableBytes(observer.PeerId)));
        if (perMessage < 1)
            return;
        var interest = observer.Interest;
        int count = 0;
        for (int i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            if (!Sees(interest, hit.TargetId))
                continue;
            _batch[count++] = hit;
            if (count == perMessage)
            {
                Flush(observer, serverTick, count);
                count = 0;
            }
        }
        if (count > 0)
            Flush(observer, serverTick, count);
    }

    private void Flush(Player observer, uint serverTick, int count)
    {
        int length = HitMessage.Write(_buffer, serverTick, _batch.AsSpan(0, count));
        _transport.Send(observer.PeerId, Channel.UnreliableSequenced, _buffer.AsSpan(0, length));
        MessagesSent++;
    }

    /// <summary>Visible is sorted by id: binary search. Left holds this tick's departures only.</summary>
    internal static bool Sees(World.InterestSet interest, uint entityId)
    {
        var visible = interest.Visible;
        int lo = 0, hi = visible.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            uint id = visible[mid].Id;
            if (id == entityId)
                return true;
            if (id < entityId)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        var left = interest.Left;
        for (int i = 0; i < left.Count; i++)
        {
            if (left[i] == entityId)
                return true;
        }
        return false;
    }
}
