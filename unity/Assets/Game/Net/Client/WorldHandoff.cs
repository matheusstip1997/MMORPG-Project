#nullable enable
using System;
using Shared.Protocol;

namespace Game.Net
{
    /// <summary>
    /// Forwards <see cref="ClientConnection"/> events to a listener that can change, so a connection opened by the login
    /// screens (spec 9.10) carries on in the world scene. Events wait inside LiteNetLib until the next
    /// <see cref="ClientConnection.PollEvents"/>, so nothing is lost while the scene loads as long as nobody polls.
    /// </summary>
    public sealed class ClientConnectionRelay : IClientConnectionListener
    {
        public IClientConnectionListener? Target { get; set; }

        public void OnConnected() => Target?.OnConnected();

        public void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion) =>
            Target?.OnDisconnected(reason, rejectReason, serverProtocolVersion);

        public void OnReliableReceived(ReadOnlySpan<byte> message) => Target?.OnReliableReceived(message);

        public void OnUnreliableReceived(ReadOnlySpan<byte> message) => Target?.OnUnreliableReceived(message);
    }

    /// <summary>An accepted game connection on its way from the login screens to the world scene (R-9.10-05).</summary>
    public sealed class WorldEntry
    {
        public WorldEntry(ClientConnection connection, ClientConnectionRelay relay, int realmId, long characterId)
        {
            Connection = connection;
            Relay = relay;
            RealmId = realmId;
            CharacterId = characterId;
        }

        public ClientConnection Connection { get; }

        /// <summary>Set <see cref="ClientConnectionRelay.Target"/> to the world's listener, then poll the connection.</summary>
        public ClientConnectionRelay Relay { get; }

        public int RealmId { get; }

        public long CharacterId { get; }
    }

    /// <summary>
    /// Holds at most one <see cref="WorldEntry"/> between scenes. The world scene takes it once; a scene that starts
    /// without one (opened directly in the editor) falls back to its dev setup. Main thread only.
    /// </summary>
    public static class WorldHandoff
    {
        private static WorldEntry? _pending;

        /// <summary>Stores the entry; one that was never taken is disposed.</summary>
        public static void Offer(WorldEntry entry)
        {
            _pending?.Connection.Dispose();
            _pending = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        public static bool TryTake(out WorldEntry? entry)
        {
            entry = _pending;
            _pending = null;
            return entry != null;
        }
    }
}
