using System;
using MemoryPack;

namespace Shared.Protocol
{
    /// <summary>What a <see cref="LootBagRequest"/> asks (WP-M4-9). Append only, never renumber.</summary>
    public enum LootBagOp : byte
    {
        None = 0,
        /// <summary>Show the bag (<see cref="LootBagContent"/>), and keep sending it while it changes and stays in reach.</summary>
        Open = 1,
        /// <summary>Take one row (<see cref="LootBagRequest.Row"/>).</summary>
        TakeOne = 2,
        /// <summary>Take every row.</summary>
        TakeAll = 3,
        /// <summary>Stop showing the bag.</summary>
        Close = 4,
    }

    /// <summary>
    /// What the viewer may do with a bag now (spec 4.3 R-4.3-03, M4 decision 8; the pure rule is
    /// <c>Shared.Simulation.Items.LootBagRules.Access</c>). Append only, never renumber.
    /// </summary>
    public enum LootBagAccess : byte
    {
        None = 0,
        /// <summary>The viewer's own bag: a take goes back to the Normal bag, what does not fit by mail.</summary>
        Own = 1,
        /// <summary>A party member's bag inside its owner window: a take is sent to the owner by mail, no stolen mark.</summary>
        CollectForOwner = 2,
        /// <summary>Someone else's bag after its owner window: a take marks the units stolen (R-4.4-01).</summary>
        TakeStolen = 3,
        /// <summary>Someone else's bag inside its owner window: nothing may be taken yet (<see cref="LootBagReason.BagLocked"/>).</summary>
        Locked = 4,
    }

    /// <summary>
    /// Why the server refused a <see cref="LootBagRequest"/> (the client shows the <c>ReasonCodeStrings</c> text). Append only,
    /// never renumber.
    /// </summary>
    public enum LootBagReason : byte
    {
        None = 0,
        /// <summary>No such bag in sight (taken, gone, never there), or a row that is not in it.</summary>
        BagNotFound = 1,
        /// <summary>Someone else's bag inside its owner window, and the viewer is not in the owner's party (R-4.3-03).</summary>
        BagLocked = 2,
        /// <summary>The bag expired (its items were destroyed, a sink) or is expiring.</summary>
        BagExpired = 3,
        /// <summary>Nothing is left in the bag, or the rows asked for are being taken by a save in flight.</summary>
        BagEmpty = 4,
        /// <summary>Farther than <c>LootSettings.pickupRangeCm</c>.</summary>
        OutOfRange = 5,
        /// <summary>Not one unit fits the receiving bag (a stranger's take; the owner's overflow goes by mail).</summary>
        BagFull = 6,
        /// <summary>Dead players cannot take.</summary>
        PickupWhileDead = 7,
        /// <summary>The character's items are not available on this server right now.</summary>
        InventoryUnavailable = 8,
    }

    /// <summary>
    /// Client to server, reliable (WP-M4-9): open, take from or close the death loot bag <see cref="BagEntityId"/>. The server
    /// checks sight, range, the owner window, the party and the bag space (architecture rule 1); the client only names the bag
    /// and the row. MemoryPack after the <see cref="ReliableMessageId"/> byte; append fields only (spec 11.2).
    /// </summary>
    [MemoryPackable]
    public sealed partial class LootBagRequest
    {
        /// <summary>Echoed in the <see cref="LootBagResult"/>; a repeated id is answered once.</summary>
        public uint RequestId { get; set; }
        public uint BagEntityId { get; set; }
        public LootBagOp Op { get; set; }
        /// <summary>The row's <see cref="LootBagRowEntry.Index"/> for <see cref="LootBagOp.TakeOne"/>; ignored otherwise.</summary>
        public byte Row { get; set; }
    }

    /// <summary>One stack in a bag: its place in the bag and its item, rarity, stolen flag and units.</summary>
    [MemoryPackable]
    public sealed partial class LootBagRowEntry
    {
        public byte Index { get; set; }
        /// <summary><c>ItemDef.netId</c>.</summary>
        public ushort ItemNetId { get; set; }
        public byte Rarity { get; set; }
        public bool Stolen { get; set; }
        public ushort Count { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-9): a bag the player opened, as committed, with what the player may do. Sent on
    /// <see cref="LootBagOp.Open"/> and again after every committed change while it stays open and in reach;
    /// <see cref="Closed"/> tells the client it is gone (taken, expired, out of reach). Times are left from the server's now,
    /// so the client needs no clock. The owner is never named (rule 8): <see cref="Access"/> says whose bag it is to the viewer.
    /// </summary>
    [MemoryPackable]
    public sealed partial class LootBagContent
    {
        public uint BagEntityId { get; set; }
        /// <summary>Counts the contents messages of one connection.</summary>
        public uint Revision { get; set; }
        public LootBagAccess Access { get; set; }
        /// <summary>Milliseconds until the owner window ends (0 once it ended).</summary>
        public uint OwnerWindowLeftMs { get; set; }
        /// <summary>Milliseconds until the bag expires and what is left is destroyed.</summary>
        public uint ExpiresInMs { get; set; }
        public LootBagRowEntry[] Rows { get; set; } = Array.Empty<LootBagRowEntry>();
        public bool Closed { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-9): the answer to a <see cref="LootBagRequest"/>. A take is answered once the save
    /// holding it committed (rule 5): <see cref="Taken"/> units went to the player's bag, <see cref="Mailed"/> units by mail
    /// (the owner's overflow, or everything a party member collected for the owner); <see cref="Stolen"/> says the units
    /// carry the stolen mark (R-4.4-01).
    /// </summary>
    [MemoryPackable]
    public sealed partial class LootBagResult
    {
        public uint RequestId { get; set; }
        public uint BagEntityId { get; set; }
        public LootBagOp Op { get; set; }
        public LootBagReason Reason { get; set; }
        public ushort Taken { get; set; }
        public ushort Mailed { get; set; }
        public bool Stolen { get; set; }
    }

    /// <summary>One of the player's own bags in <see cref="OwnLootBags"/>.</summary>
    [MemoryPackable]
    public sealed partial class OwnLootBagEntry
    {
        /// <summary>The bag's entity id (fixed for the bag's life on this server run).</summary>
        public uint BagEntityId { get; set; }
        public int XCm { get; set; }
        public int YCm { get; set; }
        public uint OwnerWindowLeftMs { get; set; }
        public uint ExpiresInMs { get; set; }
        /// <summary>Units still in the bag.</summary>
        public ushort Units { get; set; }
    }

    /// <summary>
    /// Server to client, reliable (WP-M4-9, spec 9.12): the player's own open bags at any distance, newest first, for the
    /// minimap's own-bag marker and the run back. Sent when the player enters the world and after each committed change (a
    /// death's bag, a take, an expiry).
    /// </summary>
    [MemoryPackable]
    public sealed partial class OwnLootBags
    {
        public uint Revision { get; set; }
        public OwnLootBagEntry[] Bags { get; set; } = Array.Empty<OwnLootBagEntry>();
    }

    public static partial class ReliableMessages
    {
        /// <summary>Most rows a <see cref="LootBagContent"/> may carry (a Normal bag's slots, <c>ContainerDef.MaxSlots</c>).</summary>
        public const int MaxLootBagRows = MaxInventorySlots;

        /// <summary>Most bags an <see cref="OwnLootBags"/> may carry (<c>BagExpiry.ownBagsListedMax</c> is at most this).</summary>
        public const int MaxOwnLootBags = 16;

        public static bool TryRead(ReadOnlySpan<byte> message, out LootBagRequest request)
        {
            request = null!;
            if (!TryBody(message, ReliableMessageId.LootBagRequest, out LootBagRequest? value) || value == null
                || value.Op == LootBagOp.None || value.Op > LootBagOp.Close || value.BagEntityId == 0)
                return false;
            request = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out LootBagContent content)
        {
            content = null!;
            if (!TryBody(message, ReliableMessageId.LootBagContent, out LootBagContent? value) || value?.Rows == null
                || value.Access > LootBagAccess.Locked || value.Rows.Length > MaxLootBagRows || value.BagEntityId == 0)
                return false;
            for (int i = 0; i < value.Rows.Length; i++)
            {
                var row = value.Rows[i];
                if (row == null || row.ItemNetId == 0 || row.Count == 0)
                    return false;
                for (int j = 0; j < i; j++)
                {
                    if (value.Rows[j].Index == row.Index)
                        return false;
                }
            }
            content = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out LootBagResult result)
        {
            result = null!;
            if (!TryBody(message, ReliableMessageId.LootBagResult, out LootBagResult? value) || value == null
                || value.Op == LootBagOp.None || value.Op > LootBagOp.Close || value.Reason > LootBagReason.InventoryUnavailable)
                return false;
            result = value;
            return true;
        }

        public static bool TryRead(ReadOnlySpan<byte> message, out OwnLootBags bags)
        {
            bags = null!;
            if (!TryBody(message, ReliableMessageId.OwnLootBags, out OwnLootBags? value) || value?.Bags == null
                || value.Bags.Length > MaxOwnLootBags)
                return false;
            foreach (var bag in value.Bags)
            {
                if (bag == null || bag.BagEntityId == 0)
                    return false;
            }
            bags = value;
            return true;
        }
    }
}
