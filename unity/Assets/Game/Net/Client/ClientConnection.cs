#nullable enable
using System;
using System.Net;
using LiteNetLib;
using Shared.Protocol;
using Shared.Security;

namespace Game.Net
{
    /// <summary>
    /// The client end of the game connection (spec 2.5, 11.2): LiteNetLib with the ticket handshake (R-2.5-01) and
    /// session crypto (R-2.5-02) of the server's <c>LiteNetLibTransport</c>.
    /// <list type="bullet">
    /// <item>Connect sends <see cref="ConnectRequest"/> (protocol version + ticket); a refusal arrives as a
    /// <see cref="ConnectRejectReason"/> for the login UI (spec 9.10).</item>
    /// <item>Reliable messages are sealed with ChaCha20-Poly1305 both ways; one that fails to open is a protocol
    /// violation and the client disconnects, as the server does.</item>
    /// <item>Input packets carry the truncated HMAC tag; server snapshots arrive as they are.</item>
    /// </list>
    /// Uses no UnityEngine API, so GameServer.Tests compiles this file and runs it against the real server transport.
    /// Not thread safe: call everything from one thread (the Unity main thread); listener callbacks run inside
    /// <see cref="PollEvents"/>. Sending and receiving do not allocate.
    /// </summary>
    public sealed class ClientConnection : IDisposable
    {
        private readonly IClientConnectionListener _listener;
        private readonly ClientNetLimits _limits;
        private readonly NetManager _manager;
        private readonly byte[] _reliableSendBuffer;
        private readonly byte[] _inputSendBuffer;
        private readonly byte[] _receiveBuffer;
        private SessionCrypto? _crypto;
        private NetPeer? _peer;
        private bool _violation;
        private bool _closedByClient;
        private bool _disposed;

        public ClientConnection(IClientConnectionListener listener, ClientNetLimits limits)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            if (limits.MaxReliableMessageBytes <= 0 || limits.MaxUnreliableMessageBytes <= 0)
                throw new ArgumentException("Message size caps must be positive.", nameof(limits));
            _limits = limits;
            _reliableSendBuffer = new byte[limits.MaxReliableMessageBytes + SessionCrypto.ReliableOverhead];
            _inputSendBuffer = new byte[limits.MaxUnreliableMessageBytes + SessionCrypto.InputOverhead];
            _receiveBuffer = new byte[limits.MaxReliableMessageBytes];

            var events = new EventBasedNetListener();
            events.PeerConnectedEvent += OnPeerConnected;
            events.PeerDisconnectedEvent += OnPeerDisconnected;
            events.NetworkReceiveEvent += OnNetworkReceive;
            _manager = new NetManager(events)
            {
                AutoRecycle = true,
                // R-2.5-03: unused features stay off on the client too.
                UnconnectedMessagesEnabled = false,
                NatPunchEnabled = false,
                BroadcastReceiveEnabled = false,
            };
        }

        public ConnectionState State { get; private set; }

        /// <summary>
        /// Starts connecting. <paramref name="sessionKey"/> is what the Auth API returned with the ticket; the caller
        /// should clear its copy afterwards (only derived keys are kept). The outcome arrives through the listener.
        /// </summary>
        public void Connect(string host, int port, ReadOnlySpan<byte> ticket, ReadOnlySpan<byte> sessionKey)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(ClientConnection));
            if (State != ConnectionState.Disconnected)
                throw new InvalidOperationException("Already connecting or connected.");
            if (ticket.Length > ConnectRequest.MaxTicketLength)
                throw new ArgumentException($"Ticket is longer than {ConnectRequest.MaxTicketLength} bytes.", nameof(ticket));

            var address = NetUtils.ResolveAddress(host)
                ?? throw new ArgumentException($"Cannot resolve host '{host}'.", nameof(host));
            _crypto = new SessionCrypto(sessionKey, SessionRole.Client, ManagedChaCha20Poly1305.Create);
            _violation = false;
            _closedByClient = false;

            Span<byte> request = stackalloc byte[ConnectRequest.SizeFor(ticket.Length)];
            ConnectRequest.Write(request, ProtocolVersion.Current, ticket);
            if (!_manager.IsRunning && !_manager.Start())
                throw new InvalidOperationException("Could not open a UDP socket.");
            State = ConnectionState.Connecting;
            _manager.Connect(new IPEndPoint(address, port), request);
        }

        /// <summary>Call every frame. Raises the listener callbacks.</summary>
        public void PollEvents()
        {
            if (!_disposed && _manager.IsRunning)
                _manager.PollEvents();
        }

        /// <summary>Encrypts and sends a reliable message. False when not connected or over the size cap (R-2.5-03).</summary>
        public bool SendReliable(ReadOnlySpan<byte> message)
        {
            if (State != ConnectionState.Connected || message.Length > _limits.MaxReliableMessageBytes)
                return false;
            var size = _crypto!.SealReliable(message, _reliableSendBuffer);
            _peer!.Send(_reliableSendBuffer, 0, size, 0, DeliveryMethod.ReliableOrdered);
            return true;
        }

        /// <summary>
        /// Tags and sends an input packet (<see cref="InputPacket"/>) on the unreliable sequenced channel.
        /// False when not connected or over the size cap.
        /// </summary>
        public bool SendInput(ReadOnlySpan<byte> inputPacket)
        {
            if (State != ConnectionState.Connected || inputPacket.Length > _limits.MaxUnreliableMessageBytes)
                return false;
            var size = _crypto!.TagInput(inputPacket, _inputSendBuffer);
            _peer!.Send(_inputSendBuffer, 0, size, 0, DeliveryMethod.Sequenced);
            return true;
        }

        /// <summary>Closes the connection; the listener hears <see cref="ClientDisconnectReason.ClosedByClient"/>.</summary>
        public void Disconnect()
        {
            if (State == ConnectionState.Disconnected)
                return;
            _closedByClient = true;
            if (_peer != null)
                _peer.Disconnect();
            else
                _manager.DisconnectAll();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _manager.Stop();
            ResetSession();
        }

        private void OnPeerConnected(NetPeer peer)
        {
            _peer = peer;
            State = ConnectionState.Connected;
            _listener.OnConnected();
        }

        private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            var rejectReason = ConnectRejectReason.None;
            ushort serverVersion = 0;
            ClientDisconnectReason reason;
            if (_violation)
                reason = ClientDisconnectReason.SecurityViolation;
            else if (_closedByClient)
                reason = ClientDisconnectReason.ClosedByClient;
            else if (info.Reason == DisconnectReason.ConnectionRejected)
            {
                reason = ClientDisconnectReason.Rejected;
                // R-2.5-04: an unreadable refusal is still a refusal; it just has no reason code.
                if (info.AdditionalData == null
                    || !ConnectReject.TryRead(info.AdditionalData.GetRemainingBytesSpan(), out rejectReason, out serverVersion))
                {
                    rejectReason = ConnectRejectReason.None;
                    serverVersion = 0;
                }
            }
            else
            {
                reason = info.Reason switch
                {
                    DisconnectReason.Timeout => ClientDisconnectReason.Timeout,
                    DisconnectReason.ConnectionFailed => ClientDisconnectReason.Unreachable,
                    DisconnectReason.RemoteConnectionClose => ClientDisconnectReason.ClosedByServer,
                    DisconnectReason.DisconnectPeerCalled => ClientDisconnectReason.ClosedByClient,
                    _ => ClientDisconnectReason.NetworkError,
                };
            }

            ResetSession();
            _listener.OnDisconnected(reason, rejectReason, serverVersion);
        }

        private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
        {
            if (_crypto == null || _violation || State != ConnectionState.Connected)
                return;
            var data = reader.GetRemainingBytesSpan();

            if (deliveryMethod == DeliveryMethod.ReliableOrdered)
            {
                // R-2.5-04: bound-check before any work. A reliable message that does not open cannot be skipped
                // (it was already acknowledged), so the connection is dropped.
                if (data.Length > _limits.MaxReliableMessageBytes + SessionCrypto.ReliableOverhead
                    || _crypto.OpenReliable(data, _receiveBuffer, out var length) != OpenResult.Ok)
                {
                    _violation = true;
                    peer.Disconnect();
                    return;
                }
                _listener.OnReliableReceived(new ReadOnlySpan<byte>(_receiveBuffer, 0, length));
                return;
            }

            // Server hot messages (snapshots) are not encrypted or tagged (R-2.5-02). Unknown or oversized ones are
            // dropped; the decoders validate the rest (SnapshotReader.TryBegin).
            if (data.Length > _limits.MaxUnreliableMessageBytes || !HotMessage.TryPeekId(data, out _))
                return;
            _listener.OnUnreliableReceived(data);
        }

        private void ResetSession()
        {
            State = ConnectionState.Disconnected;
            _peer = null;
            _crypto?.Dispose();
            _crypto = null;
        }
    }
}
