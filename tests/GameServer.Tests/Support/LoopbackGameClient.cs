using System.Net;
using GameServer.Net.Security;
using GameServer.Net.Tickets;
using LiteNetLib;
using Shared.Protocol;

namespace GameServer.Tests.Support;

/// <summary>
/// A minimal game client over a real LiteNetLib connection: connects with a ticket, sends tagged input packets
/// (R-2.5-02) and decodes the snapshots it receives. Plays the client side the way the Unity client and bots will.
/// </summary>
public sealed class LoopbackGameClient : IDisposable
{
    private readonly NetManager _manager;
    private readonly SessionCrypto _crypto;
    private readonly List<InputFrame> _sent = [];
    private NetPeer? _peer;

    public LoopbackGameClient(int port, IssuedTicket issued)
    {
        _crypto = new SessionCrypto(issued.SessionKey, SessionRole.Client);
        var events = new EventBasedNetListener();
        events.PeerConnectedEvent += peer => _peer = peer;
        events.NetworkReceiveEvent += (_, reader, _, method) =>
        {
            if (method == DeliveryMethod.Sequenced)
                Snapshots.Add(DecodedSnapshot.Decode(reader.GetRemainingBytes()));
        };
        _manager = new NetManager(events) { AutoRecycle = true };
        _manager.Start();

        var request = new byte[ConnectRequest.SizeFor(issued.Ticket.Length)];
        ConnectRequest.Write(request, ProtocolVersion.Current, issued.Ticket);
        _manager.Connect(new IPEndPoint(IPAddress.Loopback, port), request);
    }

    public bool Connected => _peer != null;

    public List<DecodedSnapshot> Snapshots { get; } = [];

    /// <summary>The entity this client controls: the first entity of its snapshots.</summary>
    public uint? OwnEntityId => Snapshots.Count > 0 ? Snapshots[^1].Self.EntityId : null;

    public ushort LastSequence => _sent.Count > 0 ? _sent[^1].Sequence : (ushort)0;

    public void Poll() => _manager.PollEvents();

    /// <summary>Samples one input frame and sends it with the frames before it (spec 1.7 redundancy).</summary>
    public void SendFrame(MoveKeys keys, ushort facing, int redundancy)
    {
        ushort sequence = (ushort)(LastSequence + 1);
        _sent.Add(new InputFrame { Sequence = sequence, ClientTick = (uint)(_sent.Count + 1), Keys = keys, Facing = facing });

        int count = Math.Min(redundancy, _sent.Count);
        var frames = new InputFrame[count];
        for (int i = 0; i < count; i++)
            frames[i] = _sent[_sent.Count - 1 - i];

        var payload = new byte[InputPacket.SizeFor(count)];
        InputPacket.Write(payload, frames);
        var packet = new byte[payload.Length + SessionCrypto.InputOverhead];
        _crypto.TagInput(payload, packet);
        _peer!.Send(packet, DeliveryMethod.Sequenced);
    }

    public void Dispose()
    {
        _manager.Stop();
        _crypto.Dispose();
    }
}
