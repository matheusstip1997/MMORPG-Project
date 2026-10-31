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
    /// <see cref="ChatLineMessage"/> and <see cref="ChatRefusedMessage"/> (R-6.5-01, spec 9.12). 13: the reliable bag move
    /// messages InventoryMoveRequest and InventoryMoveResult (backlog G inventory window, WP16). 14: the disconnect reason
    /// <see cref="ConnectRejectReason.Kicked"/> (admin panel v0, spec 7.4). 15: ActionResult parry results ParryFailedEarly
    /// and ParryFailedLate in place of ParryFailedTiming (spec 3.3, 9.7). 16: containers and item instances (WP-M2-1, spec 4.1 M2
    /// item model): InventoryMoveRequest container codes, InventoryMoveReason InCombat to BagFull, InventoryContents
    /// instance ids, non-tradable flags and the containers besides the Normal bag. 17: NPC entities (EntityKind.Npc) and the
    /// outpost and vendor messages OutpostInteract*, SellQuote*, Sell*, VendorBuy* and GoldBalance (spec 4.2, WP-M2-4). 18: the
    /// reliable <see cref="ProgressionState"/> message, the observer's level, exp, rank and skill points (spec 3.6, 9.4; WP-M2-3).
    /// 19: the reliable <see cref="OwnStats"/> message, the observer's final stats, attributes and toxicity and satiety meters
    /// (spec 3.4, 9.4; WP-M2-2). 20: the skill tree (spec 3.5, 9.6; WP-M2-5): the reliable <see cref="SkillTreeState"/>,
    /// <see cref="SkillTreeRequest"/> and <see cref="SkillTreeResult"/> messages, <see cref="ActionReason.WrongWeapon"/> and
    /// <see cref="ProgressionCause.SkillPoints"/>. 21: the Bystial book messages <see cref="BookState"/>, <see cref="BookChangeRequest"/> and
    /// <see cref="BookChangeResult"/> (spec 14.2, 9.4; WP-M2-8). 22: outpost quests and runs (spec 5.5, 9.3; WP-M2-6): the
    /// reliable QuestBoard, QuestRunRequest, QuestRunResult and RunStatus messages and OutpostReason BoardExpired to
    /// RunNotComplete. 23: statuses and mechanics (spec 3.7, 3.8; WP-M2-9): the entity action state code 8 Stunned (an
    /// interrupt like a stagger: age and duration).
    /// 24: the split count appended to <see cref="InventoryMoveRequest"/> (spec 9.13 stack splitting; WP-M2-11).
    /// 25: free attribute points (R-3.4-08, WP-M2-13): <see cref="AttributeSpendRequest"/>, <see cref="AttributeSpendResult"/>,
    /// the unspent and placed points appended to <see cref="ProgressionState"/> and <see cref="ProgressionCause.AttributePoints"/>.
    /// 26: item model v3 (spec 4.1 M3 item model, 9.13; WP-M3-2): each <see cref="InventorySlotEntry"/> appends the reserved
    /// flag and the instance's typed properties (enchant, quality, crafter id, tainted, gems, crafted stats);
    /// <see cref="InventoryMoveReason.ItemReserved"/> and <see cref="OutpostReason.ItemReserved"/>.
    /// 27: resource nodes (spec 5.2, WP-M3-4): entity kind ResourceNode with <c>NodeDef.netId</c> as its spawn definition id,
    /// and the entity flag <see cref="EntityFlags.Depleted"/> (entity flags 6 bits).
    /// 28: jobs (spec 5.1, 9.13; WP-M3-1): <see cref="JobState"/>, <see cref="JobRequest"/>, <see cref="JobResult"/> and the
    /// NPC interaction <c>jobs</c> in <see cref="OutpostInteractRequest"/>.
    /// 29: consumable use (spec 3.4, 9.13; WP-M3-3): <see cref="UseItemRequest"/>, <see cref="UseItemResult"/> and
    /// <see cref="UseItemReason"/>.
    /// 30: harvesting (spec 5.2, 9.13; WP-M3-5): <see cref="HarvestRequest"/>, <see cref="HarvestResult"/> and
    /// <see cref="HarvestReason"/>; the harvest action kind plays on the snapshot's action state (the cast bar).
    /// 32: job skill trees (spec 5.1, 9.13; WP-M3-7): <see cref="JobTreeRequest"/>, <see cref="JobTreeResult"/>, the learned
    /// nodes appended to each <see cref="JobEntry"/>, <see cref="JobReason"/> JobRequired to JobActivityRunning and
    /// <see cref="JobCause.TreeChanged"/>. (31 is WP-M3-6, the forge engine, in merge order.)
    /// </summary>
    public static class ProtocolVersion
    {
        public const ushort Current = 32;
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
        /// <summary>An admin disconnected this session (spec 7.4); the character lingers like after a disconnect (R-2.1-08).</summary>
        Kicked = 10,
    }
}
