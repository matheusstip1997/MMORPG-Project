#nullable enable
using System;
using Game.Net;
using Shared.Protocol;

namespace Game.UI.Login
{
    /// <summary>Opens the game connection for "Enter world" (R-9.10-05). <see cref="ClientConnectionWorldConnector"/> in the game.</summary>
    public interface IWorldConnector : IDisposable
    {
        /// <summary>The server accepted the connection; call <see cref="HandOff"/> before the world scene loads.</summary>
        event Action Connected;

        /// <summary>Connecting failed: a reason code name (see <see cref="ConnectFailure"/>) and the server's protocol version (0 if unknown).</summary>
        event Action<string, ushort> Failed;

        bool Connecting { get; }

        /// <summary>Starts connecting. The caller clears <paramref name="sessionKey"/> afterwards.</summary>
        void Connect(string host, int port, byte[] ticket, byte[] sessionKey, ClientNetLimits limits);

        /// <summary>Call every frame while connecting.</summary>
        void Poll();

        /// <summary>Passes the accepted connection to <see cref="WorldHandoff"/>; this connector no longer owns it.</summary>
        void HandOff(int realmId, long characterId);
    }

    /// <summary>Maps a failed or closed game connection to the reason code the login screens show.</summary>
    public static class ConnectFailure
    {
        /// <summary>
        /// Empty for <see cref="ClientDisconnectReason.ClosedByClient"/>: the player cancelled, nothing to show. A kick right
        /// after the acceptance (for example <c>SessionReplaced</c>) shows its own code (spec 9.10).
        /// </summary>
        public static string ReasonFor(ClientDisconnectReason reason, ConnectRejectReason rejectReason) =>
            DisconnectReasonCode.For(reason, rejectReason);
    }

    /// <summary>
    /// <see cref="IWorldConnector"/> over <see cref="ClientConnection"/>. The connection listens through a
    /// <see cref="ClientConnectionRelay"/>, so the world scene can take over the same connection.
    /// </summary>
    public sealed class ClientConnectionWorldConnector : IWorldConnector, IClientConnectionListener
    {
        private ClientConnection? _connection;
        private ClientConnectionRelay? _relay;
        private bool _connected;
        private string? _failure;
        private ushort _failureServerVersion;

        public event Action? Connected;
        public event Action<string, ushort>? Failed;

        public bool Connecting => _connection != null && _connection.State == ConnectionState.Connecting;

        public void Connect(string host, int port, byte[] ticket, byte[] sessionKey, ClientNetLimits limits)
        {
            Close();
            _relay = new ClientConnectionRelay { Target = this };
            _connection = new ClientConnection(_relay, limits);
            try
            {
                _connection.Connect(host, port, ticket, sessionKey);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                // Unresolvable host or no UDP socket.
                UnityEngine.Debug.LogWarning($"[Login] cannot connect to {host}:{port}: {e.Message}");
                Close();
                Failed?.Invoke(ClientReasonCode.GameServerUnreachable.ToString(), 0);
            }
        }

        /// <summary>Polls, then raises the outcome outside LiteNetLib's callbacks (the connection may be disposed then).</summary>
        public void Poll()
        {
            if (_connection == null)
                return;
            _connection.PollEvents();
            if (_failure != null)
            {
                var failure = _failure;
                _failure = null;
                Close();
                if (failure.Length > 0)
                    Failed?.Invoke(failure, _failureServerVersion);
            }
            else if (_connected)
            {
                _connected = false;
                Connected?.Invoke();
            }
        }

        public void HandOff(int realmId, long characterId)
        {
            if (_connection == null || _relay == null || _connection.State != ConnectionState.Connected)
                throw new InvalidOperationException("No accepted connection to hand off.");
            _relay.Target = null;
            WorldHandoff.Offer(new WorldEntry(_connection, _relay, realmId, characterId));
            _connection = null;
            _relay = null;
        }

        public void Dispose() => Close();

        void IClientConnectionListener.OnConnected() => _connected = true;

        void IClientConnectionListener.OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion)
        {
            _connected = false;
            _failure = ConnectFailure.ReasonFor(reason, rejectReason);
            _failureServerVersion = serverProtocolVersion;
        }

        // Only what arrives in the same poll as the acceptance lands here and is dropped: snapshots repeat every tick,
        // and later messages wait in LiteNetLib until the world scene polls.
        void IClientConnectionListener.OnReliableReceived(ReadOnlySpan<byte> message) { }

        void IClientConnectionListener.OnUnreliableReceived(ReadOnlySpan<byte> message) { }

        private void Close()
        {
            _connection?.Dispose();
            _connection = null;
            _relay = null;
            _connected = false;
        }
    }
}
