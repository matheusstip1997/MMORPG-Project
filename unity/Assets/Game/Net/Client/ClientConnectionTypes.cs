#nullable enable
using System;
using Shared.Protocol;

namespace Game.Net
{
    public enum ConnectionState : byte
    {
        Disconnected,
        Connecting,
        Connected,
    }

    /// <summary>
    /// Why the connection ended; with <see cref="Rejected"/> and <see cref="ClosedByServer"/> the server's
    /// <see cref="ConnectRejectReason"/> may say more (spec 9.10).
    /// </summary>
    public enum ClientDisconnectReason : byte
    {
        /// <summary>The server refused the connection request (R-2.5-01).</summary>
        Rejected,
        /// <summary>No answer from the server address.</summary>
        Unreachable,
        Timeout,
        /// <summary>The server ended an accepted session; a kick carries its reason code (spec 9.10, for example <c>Kicked</c>).</summary>
        ClosedByServer,
        ClosedByClient,
        /// <summary>A server message failed to decrypt or was too large, so the client dropped the connection (R-2.5-02).</summary>
        SecurityViolation,
        NetworkError,
    }

    /// <summary>
    /// The <c>ReasonCodeStrings</c> code the UI shows for an ended connection (spec 9.10): the server's reason code when a
    /// refusal or a kick carried one, else a <see cref="ClientReasonCode"/>. Used by the login screens and the world HUD.
    /// </summary>
    public static class DisconnectReasonCode
    {
        /// <summary>Empty for <see cref="ClientDisconnectReason.ClosedByClient"/>: the player left, nothing to show.</summary>
        public static string For(ClientDisconnectReason reason, ConnectRejectReason rejectReason)
        {
            switch (reason)
            {
                case ClientDisconnectReason.Rejected:
                    return rejectReason == ConnectRejectReason.None
                        ? ClientReasonCode.ConnectionRejected.ToString()
                        : rejectReason.ToString();
                case ClientDisconnectReason.ClosedByServer:
                    return rejectReason == ConnectRejectReason.None
                        ? ClientReasonCode.ConnectionLost.ToString()
                        : rejectReason.ToString();
                case ClientDisconnectReason.Unreachable:
                    return ClientReasonCode.GameServerUnreachable.ToString();
                case ClientDisconnectReason.Timeout:
                    return ClientReasonCode.ConnectionTimeout.ToString();
                case ClientDisconnectReason.SecurityViolation:
                    return ClientReasonCode.ConnectionSecurityViolation.ToString();
                case ClientDisconnectReason.ClosedByClient:
                    return "";
                default:
                    return ClientReasonCode.ConnectionLost.ToString();
            }
        }
    }

    /// <summary>
    /// Message size caps, from the <c>NetSecurity</c> table (R-2.5-03): the client never sends more than the server
    /// accepts and never buffers more than it would send.
    /// </summary>
    public readonly struct ClientNetLimits
    {
        public ClientNetLimits(int maxReliableMessageBytes, int maxUnreliableMessageBytes)
        {
            MaxReliableMessageBytes = maxReliableMessageBytes;
            MaxUnreliableMessageBytes = maxUnreliableMessageBytes;
        }

        /// <summary>Largest reliable payload, before crypto overhead.</summary>
        public int MaxReliableMessageBytes { get; }

        /// <summary>Largest unreliable payload (an input packet or a snapshot), before the input tag.</summary>
        public int MaxUnreliableMessageBytes { get; }
    }

    /// <summary>Receives <see cref="ClientConnection"/> events inside <see cref="ClientConnection.PollEvents"/>.</summary>
    public interface IClientConnectionListener
    {
        void OnConnected();

        /// <param name="rejectReason">
        /// Set when <paramref name="reason"/> is <see cref="ClientDisconnectReason.Rejected"/> or
        /// <see cref="ClientDisconnectReason.ClosedByServer"/> and the server sent readable reason data; otherwise None.
        /// </param>
        /// <param name="serverProtocolVersion">The server's version from that data (0 if unknown), for "please update" text.</param>
        void OnDisconnected(ClientDisconnectReason reason, ConnectRejectReason rejectReason, ushort serverProtocolVersion);

        /// <summary>A decrypted reliable message; <paramref name="message"/> is only valid during the call.</summary>
        void OnReliableReceived(ReadOnlySpan<byte> message);

        /// <summary>A server hot message (snapshot) whose id is known; only valid during the call. Decode with <c>SnapshotReader</c>.</summary>
        void OnUnreliableReceived(ReadOnlySpan<byte> message);
    }
}
