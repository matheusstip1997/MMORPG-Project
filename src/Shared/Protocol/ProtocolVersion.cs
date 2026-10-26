namespace Shared.Protocol
{
    /// <summary>
    /// Version of the client/server protocol. The handshake refuses any mismatch (spec 1.7, R-2.1-09).
    /// Bump it whenever a message layout changes incompatibly. 2: action state fields in snapshot entities, ActionResult (D2).
    /// 3: the observer's own HP, MP and stamina in the snapshot header; spawn definition ids name ClassDef and MonsterDef rows;
    /// ActionResult reasons NoStamina, DodgeNoCharge and ParryNoCharge (D4). 4: the view tick in input packets (R-3.1-03 lag
    /// compensation) and the hit message (D3). 5: projectile spawn and despawn hot messages (D3, <see cref="ProjectileMessages"/>).
    /// 6: the parry color (3 bits) of entities in an action in snapshots (R-3.1-05), <c>SubTick</c> as the view tick's
    /// fraction, ActionResult parry results (D4, WP6). 7: the observer's own statuses after its resources in the snapshot header
    /// (spec 3.7 buff row), hit kind Healed (D6, WP8). 8: ActionResult reason NoMp (R-3.1-04; backlog E, WP9). 9: ground area
    /// spawn and despawn hot messages (<see cref="GroundAreaMessages"/>) and the observer's own build-up meters after its
    /// statuses in the snapshot header (spec 3.8; D6, WP11). 10: the reliable <see cref="EntityIdentity"/> message, player
    /// names for the name plates (spec 9.12; backlog I, WP13). 11: entity kind LootDrop, the first MemoryPack reliable messages
    /// PickupRequest, PickupResult and InventoryContents (backlog G, WP14). 12: local chat, <see cref="ChatSendMessage"/>,
    /// <see cref="ChatLineMessage"/> and <see cref="ChatRefusedMessage"/> (R-6.5-01, spec 9.12).
    /// </summary>
    public static class ProtocolVersion
    {
        public const ushort Current = 12;
    }

    /// <summary>Transport channels (spec 1.7).</summary>
    public enum Channel : byte
    {
        /// <summary>Action confirmations, inventory, trade, chat, quests, guild, login.</summary>
        ReliableOrdered = 0,

        /// <summary>Input frames, snapshots, projectile spawns.</summary>
        UnreliableSequenced = 1,
    }

    /// <summary>
    /// Why the server refused a connection or ended a session (spec 9.10, <c>LoginReasonCodes</c>). A refusal carries it
    /// in the reject data; a kick of an accepted session carries it in the disconnect data, in the same
    /// <see cref="ConnectReject"/> layout. Append only, never renumber.
    /// </summary>
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
        /// <summary>A new login for the same account replaced this session (R-2.1-04).</summary>
        SessionReplaced = 6,
        /// <summary>The server is shutting down or not taking logins (R-2.1-09).</summary>
        ServerMaintenance = 7,
        /// <summary>A backing service (session store, database) failed; retry later.</summary>
        ServerUnavailable = 8,
        /// <summary>
        /// No playable character in the ticket's slot, the character is loaded on another server (R-2.1-06), or this
        /// server lost its ownership.
        /// </summary>
        CharacterUnavailable = 9,
    }
}
