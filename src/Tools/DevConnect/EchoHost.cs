using System.Text;
using GameServer.Net;
using GameServer.Net.Tickets;
using Shared.Protocol;

namespace DevConnect;

/// <summary>
/// Game side of the dev test host: greets each peer, echoes reliable messages, and answers each input packet with a
/// snapshot that acknowledges the newest input frame. Enough to see the whole client path work; no game logic.
/// Callbacks come from <see cref="ITransport.PollEvents"/> on the host's one thread.
/// </summary>
public sealed class EchoHost : ITransportListener
{
    private readonly InputFrame[] _frames = new InputFrame[InputPacket.MaxFrames];
    private readonly byte[] _snapshot = new byte[Snapshot.SizeFor(despawns: 0, spawns: 0, entities: 1)];
    private uint _serverTick;

    public ITransport? Transport { get; set; }

    public event Action? PeerConnected;

    public void OnPeerConnected(int peerId, SessionTicket ticket)
    {
        Console.WriteLine($"peer {peerId} connected: account {ticket.AccountId}, realm {ticket.RealmId}, slot {ticket.Slot}");
        Transport?.Send(peerId, Channel.ReliableOrdered, Encoding.UTF8.GetBytes($"welcome account {ticket.AccountId}"));
        PeerConnected?.Invoke();
    }

    public void OnPeerDisconnected(int peerId, PeerDisconnectReason reason) =>
        Console.WriteLine($"peer {peerId} disconnected: {reason}");

    public void OnDataReceived(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        if (channel == Channel.ReliableOrdered)
        {
            Console.WriteLine($"peer {peerId} reliable ({data.Length} bytes), echoing");
            Transport?.Send(peerId, Channel.ReliableOrdered, data);
            return;
        }

        if (!InputPacket.TryRead(data, _frames, out var count) || count == 0)
        {
            Console.WriteLine($"peer {peerId} sent an unreadable input packet ({data.Length} bytes)");
            return;
        }

        var header = new SnapshotHeader { ServerTick = ++_serverTick, HasInputAck = true, LastInputSequence = _frames[0].Sequence };
        var writer = new SnapshotWriter(_snapshot, header);
        writer.TryAddEntity(new EntitySnapshot { EntityId = (uint)peerId + 1, Facing = _frames[0].Facing, HpBucket = Snapshot.TopHpBucket });
        Transport?.Send(peerId, Channel.UnreliableSequenced, _snapshot.AsSpan(0, writer.Finish()));
    }
}
