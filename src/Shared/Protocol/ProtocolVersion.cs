namespace Shared.Protocol
{
    /// <summary>
    /// Version of the client/server protocol. The handshake refuses any mismatch (spec 1.7, R-2.1-09).
    /// Bump it whenever a message layout changes incompatibly.
    /// </summary>
    public static class ProtocolVersion
    {
        public const ushort Current = 1;
    }

    /// <summary>Transport channels (spec 1.7).</summary>
    public enum Channel : byte
    {
        /// <summary>Action confirmations, inventory, trade, chat, quests, guild, login.</summary>
        ReliableOrdered = 0,

        /// <summary>Input frames, snapshots, projectile spawns.</summary>
        UnreliableSequenced = 1,
    }

    /// <summary>Why the server refused a connection (spec 9.10, <c>LoginReasonCodes</c>).</summary>
    public enum ConnectRejectReason : byte
    {
        None = 0,
        VersionMismatch = 1,
        ServerFull = 2,
        /// <summary>Unreadable request, bad signature, unknown key, wrong realm, or a ticket that was already used.</summary>
        InvalidTicket = 3,
        TicketExpired = 4,
        /// <summary>Too many connection attempts from this address, or from everyone at once (R-2.5-03); retry later.</summary>
        TooManyAttempts = 5,
    }
}
