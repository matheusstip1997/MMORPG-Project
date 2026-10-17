using LiteNetLib;
using Shared.Protocol;

namespace GameServer.Net;

/// <summary><see cref="ITransport"/> over LiteNetLib (decided in spec 11.2).</summary>
public sealed class LiteNetLibTransport : ITransport
{
    private readonly ConnectionApprover _approver;
    private readonly ITransportListener _listener;
    private readonly int _maxPeers;
    private readonly NetManager _manager;

    public LiteNetLibTransport(ConnectionApprover approver, ITransportListener listener, int maxPeers)
    {
        _approver = approver;
        _listener = listener;
        _maxPeers = maxPeers;

        var events = new EventBasedNetListener();
        events.ConnectionRequestEvent += OnConnectionRequest;
        events.PeerConnectedEvent += OnPeerConnected;
        events.PeerDisconnectedEvent += OnPeerDisconnected;
        events.NetworkReceiveEvent += OnNetworkReceive;

        _manager = new NetManager(events)
        {
            AutoRecycle = true,
            // R-2.5-03: unused features are disabled.
            UnconnectedMessagesEnabled = false,
            NatPunchEnabled = false,
            BroadcastReceiveEnabled = false,
        };
    }

    public int LocalPort => _manager.LocalPort;

    public int ConnectedPeers => _manager.ConnectedPeersCount;

    public void Start(int port)
    {
        if (!_manager.Start(port))
            throw new InvalidOperationException($"Could not bind UDP port {port}.");
    }

    public void Stop() => _manager.Stop();

    public void PollEvents() => _manager.PollEvents();

    public void Send(int peerId, Channel channel, ReadOnlySpan<byte> data)
    {
        var peer = _manager.GetPeerById(peerId);
        peer?.Send(data, ToDeliveryMethod(channel));
    }

    public void Disconnect(int peerId)
    {
        var peer = _manager.GetPeerById(peerId);
        if (peer != null)
            _manager.DisconnectPeer(peer);
    }

    public void Dispose() => _manager.Stop();

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (_manager.ConnectedPeersCount >= _maxPeers)
        {
            Reject(request, ConnectRejectReason.ServerFull);
            return;
        }

        var approval = _approver.Approve(request.Data.GetRemainingBytesSpan());
        if (!approval.Ok)
        {
            Reject(request, approval.Reason);
            return;
        }

        var peer = request.Accept();
        peer.Tag = approval.Ticket;
    }

    private static void Reject(ConnectionRequest request, ConnectRejectReason reason)
    {
        var data = new byte[ConnectReject.Size];
        ConnectReject.Write(data, reason, ProtocolVersion.Current);
        request.Reject(data);
    }

    private void OnPeerConnected(NetPeer peer) =>
        _listener.OnPeerConnected(peer.Id, (Tickets.SessionTicket)peer.Tag);

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        // Rejected requests never became peers the game knows about.
        if (peer.Tag == null)
            return;

        var reason = info.Reason switch
        {
            DisconnectReason.Timeout => PeerDisconnectReason.Timeout,
            DisconnectReason.RemoteConnectionClose => PeerDisconnectReason.ClosedByClient,
            DisconnectReason.DisconnectPeerCalled => PeerDisconnectReason.ClosedByServer,
            _ => PeerDisconnectReason.NetworkError,
        };
        _listener.OnPeerDisconnected(peer.Id, reason);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        var channel = deliveryMethod == DeliveryMethod.ReliableOrdered ? Channel.ReliableOrdered : Channel.UnreliableSequenced;
        _listener.OnDataReceived(peer.Id, channel, reader.GetRemainingBytesSpan());
    }

    private static DeliveryMethod ToDeliveryMethod(Channel channel) => channel switch
    {
        Channel.ReliableOrdered => DeliveryMethod.ReliableOrdered,
        Channel.UnreliableSequenced => DeliveryMethod.Sequenced,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };
}
